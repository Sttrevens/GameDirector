"""Black-box revision contracts against a fresh service/store; no game or model."""
import concurrent.futures, copy, hashlib, json, os, subprocess, time, urllib.error, urllib.request, uuid
from pathlib import Path

repo = Path(__file__).resolve().parents[1]
root = repo / "captures" / "film-document-tests" / uuid.uuid4().hex[:12]
root.mkdir(parents=True)
store = root / "store"
process = None
base = ""
checks = []
film = dict(version=1, title="Original", frameRate=24, width=640, height=360, scenes=[], audio=[], subtitles=[])
log = (root / "server.log").open("w")

def call(path, body=None, expected=200):
    request = urllib.request.Request(base + path, data=None if body is None else json.dumps(body).encode(), headers={"Content-Type": "application/json"})
    try:
        with urllib.request.urlopen(request, timeout=20) as response:
            status, data = response.status, response.read()
    except urllib.error.HTTPError as error:
        status, data = error.code, error.read()
    assert expected is None or status == expected, (path, status, data)
    return status, json.loads(data) if data else None

def launch():
    global process, base
    descriptor = store / ".service.json"
    prior = descriptor.read_bytes() if descriptor.exists() else None
    process = subprocess.Popen(["dotnet", str(repo / "src/GameDirector.Workbench/bin/Debug/net8.0/gamedirector-workbench.dll"),
        "--port", "0", "--data", str(store)], stdout=log, stderr=log,
        env=dict(os.environ, GAMEDIRECTOR_FFMPEG="/nonexistent/gamedirector-test-ffmpeg"))
    for _ in range(150):
        if process.poll() is not None:
            raise AssertionError("service exited: " + (root / "server.log").read_text())
        if descriptor.exists() and descriptor.read_bytes() != prior:
            record = json.loads(descriptor.read_text())
            base = record["address"] + "/api/"
            try:
                _, health = call("health")
                assert health["instanceId"] == record["instanceId"]
                return health
            except (OSError, AssertionError):
                pass
        time.sleep(.1)
    raise AssertionError("service did not become ready")

def save(doc, revision, value, request=None, expected=200):
    return call("projects/fixture/documents/" + doc,
        dict(expectedRevision=revision, requestId=request or uuid.uuid4().hex, film=value), expected)

try:
    health = launch()
    call("projects", dict(id="fixture", name="Isolated document test", engine="Unity", endpoint="http://127.0.0.1:39776"))
    path = "projects/fixture/documents/film-a"
    assert call(path)[1]["revision"] == 0
    original = copy.deepcopy(film)
    first = save("film-a", 0, original, "first-save")[1]
    assert first["revision"] == 1 and first["parentRevision"] == 0
    original["title"] = "client mutation after submit"
    assert call(path)[1]["film"]["title"] == "Original"
    checks.append("accepted snapshot is immutable")
    assert save("film-a", 0, film, "first-save")[1] == first
    checks.append("lost save response retry returns original receipt")
    _, conflict = save("film-a", 0, dict(film, title="stale B"), expected=409)
    assert conflict["code"] == "document_conflict" and conflict["current"]["revision"] == 1
    assert call(path)[1]["film"]["title"] == "Original"
    checks.append("stale client cannot overwrite newer draft")
    save("film-a", 0, dict(film, title="different payload"), "first-save", expected=409)
    checks.append("reused operation ID rejects changed inputs")
    call(path, dict(requestId="missing-revision", film=film), expected=428)
    call("projects/fixture/draft", film, expected=428)
    checks.append("unconditional and legacy writes cannot bypass revision checks")
    value = dict(film, title="Second")
    second = save("film-a", 1, value, "second-save")[1]
    assert second["parentRevision"] == 1
    assert call(path + "?revision=1")[1] == first
    assert save("film-a", 0, film, "first-save")[1] == first
    checks.append("history and retry receipts survive later revisions")
    save("film-b", 0, dict(film, title="Another film"))
    assert len(call("projects/fixture/documents")[1]) == 2
    checks.append("multiple films have independent identities")
    with concurrent.futures.ThreadPoolExecutor(max_workers=8) as pool:
        responses = list(pool.map(lambda n: save("film-a", 2, dict(film, title="concurrent-" + str(n)), expected=None), range(8)))
    assert sum(status == 200 for status, _ in responses) == 1
    assert sum(status == 409 for status, _ in responses) == 7
    assert call(path)[1]["revision"] == 3
    checks.append("concurrent writers have exactly one winner")
    # Read migration is zero-write; first new revision preserves the legacy bytes.
    legacy = store / "projects" / "fixture.draft"
    legacy.write_text(json.dumps(dict(film, title="Legacy")))
    old_bytes, old_mtime = legacy.read_bytes(), legacy.stat().st_mtime_ns
    before = sorted(str(p) for p in store.rglob("*"))
    old = call("projects/fixture/documents/default")[1]
    assert old["revision"] == 1 and old["film"]["title"] == "Legacy"
    assert sorted(str(p) for p in store.rglob("*")) == before
    save("default", 1, film, "migrated-save")
    assert call("projects/fixture/documents/default?revision=1")[1] == old
    assert legacy.read_bytes() == old_bytes and legacy.stat().st_mtime_ns == old_mtime
    checks.append("legacy migration preserves original draft and revision")
    _, report = call(path + "/readiness", dict(revision=3))
    assert not report["ready"] and {c["id"] for c in report["checks"]} == {"performance", "media"}
    assert all(c["action"] for c in report["checks"] if not c["ready"])
    assert call("state")[1]["jobs"] == []
    checks.append("readiness explains independent blockers without starting production")
    call(path, dict(expectedRevision=3, requestId="unknown-field", film=dict(film, inventedFeature=True)), expected=400)
    save("film-a", 3, dict(film, version=99), expected=400)
    checks.append("unknown fields and unsupported schemas are rejected")
    process.terminate(); process.wait(timeout=15)
    restarted = launch()
    assert restarted["storeId"] == health["storeId"] and restarted["instanceId"] != health["instanceId"]
    assert call(path)[1]["revision"] == 3
    assert save("film-a", 1, value, "second-save")[1] == second
    checks.append("restart preserves document history and retry identity")
    # Incomplete atomic-write files are not mistaken for an accepted revision.
    directory = store / "documents/fixture/film-a"
    (directory / "000000000004.json.interrupted.tmp").write_text('{"partial":')
    assert call(path)[1]["revision"] == 3
    checks.append("interrupted temporary writes never advance the document")
    corrupted = directory / "000000000003.json"
    broken = json.loads(corrupted.read_text()); broken["film"]["title"] = "tampered"
    corrupted.write_text(json.dumps(broken)); damaged = corrupted.read_bytes()
    call(path, expected=400)
    assert corrupted.read_bytes() == damaged
    checks.append("inconsistent history is reported without rewriting evidence")
    result = dict(passed=True, checks=checks, evidence=str(root))
    (root / "result.json").write_text(json.dumps(result, indent=2) + "\n")
    print(json.dumps(result))
finally:
    if process and process.poll() is None:
        process.terminate(); process.wait(timeout=15)
    log.close()
