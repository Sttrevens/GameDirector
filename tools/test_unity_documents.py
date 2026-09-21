"""Real Unity sample, native document client, HTTP revision and film acceptance.
Only uses a project carrying our explicit ownership marker; no customer projects.
"""
import argparse, copy, hashlib, json, os, socket, subprocess, time, urllib.error, urllib.request
from pathlib import Path

p=argparse.ArgumentParser();p.add_argument("--project",required=True);p.add_argument("--out",required=True);p.add_argument("--editor",default=os.environ.get("UNITY_EDITOR"));p.add_argument("--workbench",help="Published native Workbench executable; defaults to the Debug development build");a=p.parse_args()
if not a.editor or not Path(a.editor).is_file(): p.error("Pass --editor with the installed Unity executable (or set UNITY_EDITOR).")
urllib.request.install_opener(urllib.request.build_opener(urllib.request.ProxyHandler({})))
repo=Path(__file__).resolve().parents[1];project=Path(a.project).resolve();root=Path(a.out).resolve()
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
unity=None
def call(path,body=None,expected=200):
    r=urllib.request.Request(base+path,data=None if body is None else json.dumps(body).encode(),headers={"Content-Type":"application/json"})
    try:
        with urllib.request.urlopen(r,timeout=120) as response:status,data=response.status,response.read()
    except urllib.error.HTTPError as error:status,data=error.code,error.read()
    assert status==expected,(path,status,data)
    return json.loads(data)
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
    for _ in range(100):
        try:call("health");break
        except Exception:time.sleep(.1)
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
    with urllib.request.urlopen(base+"jobs/"+second["id"]+"/video") as response:video=response.read()
    assert hashlib.sha256(video).hexdigest()==second["videoHash"]
    after=snapshot();(root/"source-after.json").write_text(json.dumps(after,indent=2))
    assert before==after,{"changed":[f for f in set(before)|set(after) if before.get(f)!=after.get(f)]}
    probe=json.loads(subprocess.check_output([os.environ.get("GAMEDIRECTOR_FFPROBE", "ffprobe"),"-v","error","-show_streams","-show_format","-of","json",second["video"]]))
    assert any(s["codec_type"]=="audio" for s in probe["streams"])
    result=dict(passed=True,project=ready["projectId"],document=ready["documentId"],nativeSaveRevision=1,httpSaveRevision=2,
        conflictStatus=409,firstJob=first["id"],revisedJob=second["id"],reusedShots=second["reusedShots"],
        sourceFilesChecked=len(before),sourceWrites=0,video=second["video"],videoHash=second["videoHash"],evidence=str(root))
    (root/"result.json").write_text(json.dumps(result,indent=2)+"\n");print(json.dumps(result))
finally:
    (root/"stop").write_text("Owned validation complete")
    if unity:
        try:unity.wait(timeout=25)
        except subprocess.TimeoutExpired:unity.terminate();unity.wait(timeout=20)
    if server.poll() is None:server.terminate();server.wait(timeout=20)
    log.close()
