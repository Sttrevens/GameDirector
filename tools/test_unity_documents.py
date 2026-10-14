"""Real Unity sample, native document client, HTTP revision and film acceptance.
Only uses a project carrying our explicit ownership marker; no customer projects.
"""
import argparse, copy, hashlib, json, os, socket, subprocess, time, urllib.error, urllib.request
from pathlib import Path
from workbench_test_client import auth_headers, wait_service

p=argparse.ArgumentParser();p.add_argument("--project",required=True);p.add_argument("--out",required=True);p.add_argument("--editor",default=os.environ.get("UNITY_EDITOR"));p.add_argument("--workbench",help="Published native Workbench executable; defaults to the Debug development build");p.add_argument("--gd",help="Core CLI gd.dll; defaults to the Debug development build");p.add_argument("--gd-executable",help="Published native gd; do not read a repo development build");a=p.parse_args()
if not a.editor or not Path(a.editor).is_file(): p.error("Pass --editor with the installed Unity executable (or set UNITY_EDITOR).")
urllib.request.install_opener(urllib.request.build_opener(urllib.request.ProxyHandler({})))
repo=Path(__file__).resolve().parents[1];project=Path(a.project).resolve();root=Path(a.out).resolve()
gd_command=[str(Path(a.gd_executable).resolve())] if a.gd_executable else ['dotnet',a.gd or str(repo/'src/GameDirector.Cli/bin/Debug/net8.0/gd.dll')]
contract=json.loads(subprocess.check_output([*gd_command,'contract','--json']))
states=contract['take']['states'];errors=contract['bridge']['errors'];budgets=contract['bridge']['budgets'];limits=contract['bridge']['limits']
assert (project/".gamedirector-validation-project").is_file()
root.mkdir(parents=True,exist_ok=False)
def freeport():
    with socket.socket() as s:s.bind(("127.0.0.1",0));return s.getsockname()[1]
port,bridge=freeport(),freeport();base=f"http://127.0.0.1:{port}/api/"
env=dict(os.environ,
    GAMEDIRECTOR_WORKBENCH=f"http://127.0.0.1:{port}",GAMEDIRECTOR_BRIDGE=f"http://127.0.0.1:{bridge}",
    GD_VALIDATION_BRIDGE_PORT=str(bridge),GD_VALIDATION_EVIDENCE=str(root))
log=(root/"workbench.log").open("w")
server_command=[str(Path(a.workbench).resolve())] if a.workbench else ["dotnet",str(repo/"src/GameDirector.Workbench/bin/Debug/net8.0/gamedirector-workbench.dll")]
server=subprocess.Popen([*server_command,"--port",str(port),"--data",str(root/"store")],env=env,stdout=log,stderr=log)
unity=None;service=None
def call(path,body=None,expected=200):
    r=urllib.request.Request(base+path,data=None if body is None else json.dumps(body).encode(),headers={"Content-Type":"application/json",**auth_headers(service)})
    try:
        with urllib.request.urlopen(r,timeout=120) as response:status,data=response.status,response.read()
    except urllib.error.HTTPError as error:status,data=error.code,error.read()
    assert status==expected,(path,status,data)
    return json.loads(data)
def bridge_call(route,body=None,expected=200,headers=None,raw=None):
    req=urllib.request.Request(f"http://127.0.0.1:{bridge}"+route,data=raw if raw is not None else (None if body is None else json.dumps(body).encode()),headers={"Content-Type":"application/json",**(headers or {})})
    try:
        with urllib.request.urlopen(req,timeout=budgets['takeStartTimeoutSeconds'] if route=="/take/start" else budgets['requestTimeoutSeconds']) as response:status,data=response.status,response.read()
    except urllib.error.HTTPError as error:status,data=error.code,error.read()
    assert status==expected,(route,status,data)
    return data if route=="/take/frame" and status==200 else json.loads(data)
def bridge_admission(film):
    denied=0
    for site in ("cross-site","same-site",""):
        for route in ("/health","/manifest","/capture"):
            bridge_call(route,expected=errors['forbidden']['status'],headers={"Sec-Fetch-Site":site});denied+=1
    for site in ("none","same-origin"):
        assert bridge_call("/health",headers={"Sec-Fetch-Site":site})["ok"]
    manifest=bridge_call("/manifest")
    camera=copy.deepcopy(film["scenes"][0]["shots"][0]["camera"]);camera["durationSeconds"]=1
    take=bridge_call("/take/start",dict(timeline=dict(version="0.1",id="admission",cues=[dict(t=0,type="camera.shot",shot=camera)]),
        frameRate=1,width=64,height=64,sourceFingerprint=manifest["capabilities"]["presentation.sourceFingerprint"]))
    try:
        # More than the queue capacity: these routes must reject before claiming
        # slots, while health, take inspection and the owned frame remain usable.
        # Busy uses bridge.errors.busy, never the stateConflict definition.
        for _ in range(limits['maxQueuedRequests']+1):
            for route in ("/manifest","/capture","/status"):
                assert bridge_call(route,expected=errors['busy']['status'])["code"]==errors['busy']['code'];denied+=1
        assert bridge_call("/health")["ok"]
        assert bridge_call("/take")["take"]["takeId"]==take["takeId"]
        assert bridge_call("/take/frame",dict(takeId=take["takeId"],frameIndex=0)).startswith(b"\x89PNG\r\n\x1a\n")
        assert bridge_call("/take")["take"]["state"]==states['completed']
    finally:bridge_call("/take/stop",dict(takeId=take["takeId"]))
    # Live error-protocol probes on a fresh take: an out-of-order frame is a
    # stateConflict and malformed JSON an invalidRequest (bridge.errors);
    # neither disturbs the take owner.
    probe_camera=copy.deepcopy(camera);probe_camera["durationSeconds"]=2
    probe=bridge_call("/take/start",dict(timeline=dict(version="0.1",id="probe",cues=[dict(t=0,type="camera.shot",shot=probe_camera)]),
        frameRate=1,width=64,height=64,sourceFingerprint=manifest["capabilities"]["presentation.sourceFingerprint"]))
    try:
        mismatch=bridge_call("/take/frame",dict(takeId=probe["takeId"],frameIndex=1),expected=errors['stateConflict']['status'])
        assert mismatch["code"]==errors['stateConflict']['code'] and "frame index mismatch" in mismatch["error"],mismatch
        malformed=bridge_call("/take/frame",raw=b"{broken",expected=errors['invalidRequest']['status'])
        assert malformed["code"]==errors['invalidRequest']['code'],malformed
        for route in ("/take/start","/take/stop"):
            for raw in (b"{broken",b"null",b"[]"):
                malformed=bridge_call(route,raw=raw,expected=errors['invalidRequest']['status'])
                assert malformed["code"]==errors['invalidRequest']['code'],malformed
                denied+=1
        assert bridge_call("/take")["take"]["state"]==states['capturing']
        denied+=2
    finally:bridge_call("/take/stop",dict(takeId=probe["takeId"]))
    return dict(rejectedRequests=denied,protectedFrames=1,allowedFetchSites=2)
def snapshot():
    return {str(f.relative_to(project)):hashlib.sha256(f.read_bytes()).hexdigest()
        for folder in ("Assets","Packages","ProjectSettings") for f in (project/folder).rglob("*") if f.is_file()}
def wait(job):
    for _ in range(1800):
        value=call("jobs/"+job["id"])
        if value["state"] not in ("Queued","Running"):
            assert value["state"]=="Verified",value
            return value
        time.sleep(.2)
    raise AssertionError("production timed out")
try:
    service=wait_service(root/"store/.service.json",server)
    env["GAMEDIRECTOR_WORKBENCH_TOKEN"]=service["token"]
    unity=subprocess.Popen([a.editor,"-batchmode","-projectPath",str(project),
        "-executeMethod","FilmDocumentValidation.Run","-logFile",str(root/"unity.log")],env=env,
        creationflags=subprocess.CREATE_NO_WINDOW if os.name == 'nt' else 0)
    for _ in range(1800):
        if (root/"failure.txt").exists():raise AssertionError((root/"failure.txt").read_text(encoding="utf-8"))
        if (root/"ready.json").exists():break
        if unity.poll() is not None:raise AssertionError("Unity exited before preparing example")
        time.sleep(.2)
    else:raise AssertionError("Unity example preparation timed out")
    ready=json.loads((root/"ready.json").read_text(encoding="utf-8"));assert ready["readiness"]["ready"],ready
    path=f"projects/{ready['projectId']}/documents/{ready['documentId']}"
    first_document=call(path);assert first_document["revision"]==1 and first_document["film"]["audio"]
    admission=bridge_admission(first_document["film"])
    before=snapshot();(root/"source-before.json").write_text(json.dumps(before,indent=2))
    first=wait(call(path+"/produce",dict(requestId="first-example",revision=1)))
    assert call(path+"/produce",dict(requestId="first-example",revision=1))["id"]==first["id"]
    revised=copy.deepcopy(first_document["film"]);revised["scenes"][0]["shots"][2]["camera"]["from"]="wide"
    second_document=call(path,dict(expectedRevision=1,requestId="browser-camera-edit",film=revised));assert second_document["revision"]==2
    conflict=call(path,dict(expectedRevision=1,requestId="stale-inspector",film=first_document["film"]),expected=409)
    assert conflict["current"]["revision"]==2
    second=wait(call(path+"/produce",dict(requestId="revised-example",revision=2)))
    assert second["reusedShots"]==2,second
    assert first["request"]["documentRevision"]==1 and second["request"]["documentRevision"]==2
    with urllib.request.urlopen(urllib.request.Request(base+"jobs/"+second["id"]+"/video",headers=auth_headers(service))) as response:video=response.read()
    assert hashlib.sha256(video).hexdigest()==second["videoHash"]
    after=snapshot();(root/"source-after.json").write_text(json.dumps(after,indent=2))
    assert before==after,{"changed":[f for f in set(before)|set(after) if before.get(f)!=after.get(f)]}
    probe=json.loads(subprocess.check_output([os.environ.get("GAMEDIRECTOR_FFPROBE", "ffprobe"),"-v","error","-show_streams","-show_format","-of","json",second["video"]]))
    assert any(s["codec_type"]=="audio" for s in probe["streams"])
    result=dict(passed=True,project=ready["projectId"],document=ready["documentId"],nativeSaveRevision=1,httpSaveRevision=2,
        conflictStatus=409,firstJob=first["id"],revisedJob=second["id"],reusedShots=second["reusedShots"],
        sourceFilesChecked=len(before),sourceWrites=0,bridgeAdmission=admission,video=second["video"],videoHash=second["videoHash"],evidence=str(root))
    (root/"result.json").write_text(json.dumps(result,indent=2)+"\n");print(json.dumps(result))
finally:
    (root/"stop").write_text("Owned validation complete")
    if unity:
        try:unity.wait(timeout=25)
        except subprocess.TimeoutExpired:unity.terminate();unity.wait(timeout=20)
    if server.poll() is None:server.terminate();server.wait(timeout=20)
    log.close()
