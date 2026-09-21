"""Real Unity capture on a separately running, explicitly owned validation project.
Never launches or mutates a user's existing Unity project. Launch the supplied
CaptureValidation harness in a disposable project, then pass its project/evidence.
"""
import argparse, copy, hashlib, json, os, socket, subprocess, time, urllib.request, uuid
from pathlib import Path

p=argparse.ArgumentParser();p.add_argument('--project',required=True);p.add_argument('--out',required=True);a=p.parse_args()
repo=Path(__file__).resolve().parents[1];project=Path(a.project).resolve();root=Path(a.out).resolve()
assert (project/'.gamedirector-validation-project').is_file(), 'Refuse an unowned Unity project'
assert (root/'ready').read_text()==str(project/'Assets')
def snapshot():
 return {str(f.relative_to(project)):hashlib.sha256(f.read_bytes()).hexdigest() for folder in ('Assets','Packages','ProjectSettings') for f in sorted((project/folder).rglob('*')) if f.is_file()}
before=snapshot();(root/'source-before.json').write_text(json.dumps(before,indent=2))
with socket.socket() as s:s.bind(('127.0.0.1',0));port=s.getsockname()[1]
base=f'http://127.0.0.1:{port}/api/'
env=dict(os.environ)
log=open(root/'workbench.log','w');server=subprocess.Popen(['dotnet',str(repo/'src/GameDirector.Workbench/bin/Debug/net8.0/gamedirector-workbench.dll'),'--port',str(port),'--data',str(root/'store')],env=env,stdout=log,stderr=log)
def call(path,body=None):
 req=urllib.request.Request(base+path,data=None if body is None else json.dumps(body).encode(),headers={'Content-Type':'application/json'})
 with urllib.request.urlopen(req,timeout=40) as r:return json.load(r)
def produce(film):
 job=call('jobs',dict(projectId='unity',requestId=uuid.uuid4().hex,film=film))
 for _ in range(900):
  job=call('jobs/'+job['id'])
  if job['state'] not in ('Queued','Running'):break
  time.sleep(.2)
 assert job['state']=='Verified',job
 return job
try:
 for _ in range(100):
  try:call('state');break
  except Exception:time.sleep(.2)
 call('projects',dict(id='unity',name='Unity isolated capture acceptance',engine='Unity',endpoint='http://127.0.0.1:39779',sourceRoot=str(project)))
 connected=call('projects/unity/connect',{});assert connected['manifest']['capabilities']['project.sourceRoot']==str(project)
 film=dict(version=1,title='Unity owned source validation',frameRate=12,width=320,height=180,scenes=[dict(id='stage',performance=[],shots=[dict(id='wide',start=0,end=1,purpose='Establish',camera=dict(type='lockoff',subject='lead',frame='full',**{'from':'wide'})),dict(id='close',start=1,end=2,purpose='Detail',camera=dict(type='lockoff',subject='lead',frame='closeup',**{'from':'close'}))])])
 first=produce(film);revision=copy.deepcopy(film);revision['scenes'][0]['shots'][1]['camera']['from']='side'
 second=produce(revision);assert second['reusedShots']==1,second
 after=snapshot();(root/'source-after.json').write_text(json.dumps(after,indent=2))
 assert before==after, {'changed':[f for f in set(before)|set(after) if before.get(f)!=after.get(f)]}
 result=dict(passed=True,engine='Unity 2022.3.34f1',project=str(project),sourceFilesChecked=len(before),sourceWrites=0,initialJob=first['id'],revisedJob=second['id'],reusedShots=second['reusedShots'],video=second['video'],videoHash=second['videoHash'])
 (root/'result.json').write_text(json.dumps(result,indent=2));print(json.dumps(result))
finally:
 if server.poll() is None:server.terminate();server.wait(timeout=20)
 log.close();(root/'stop').write_text('Owned validation complete; exit this isolated Editor')
