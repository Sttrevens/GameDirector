"""Extract a local candidate and drive its Workbench through its MCP against a real bridge."""
import argparse,hashlib,json,os,select,signal,socket,subprocess,tarfile,time,urllib.request,zipfile
from pathlib import Path
from workbench_test_client import auth_headers, wait_service
p=argparse.ArgumentParser();p.add_argument('--archive',required=True);p.add_argument('--endpoint',required=True);p.add_argument('--out',required=True);a=p.parse_args()
repo=Path(__file__).resolve().parents[1];root=Path(a.out).resolve();root.mkdir(parents=True,exist_ok=False)
if str(a.archive).endswith('.zip'):
 with zipfile.ZipFile(a.archive) as z:
  for n in z.namelist():
   target=(root/n).resolve();assert target.is_relative_to(root),n
  z.extractall(root)
else:
 with tarfile.open(a.archive) as archive: archive.extractall(root,filter='data')
folders=list(root.iterdir());assert len(folders)==1 and folders[0].is_dir()
root=folders[0]
layout=json.loads((root/'distribution.json').read_text())
runtime=root/layout['runtimeFolder'];manifest=json.loads((runtime/layout['runtimeManifestFile']).read_text())
index=json.loads((root/layout['releaseManifestFile']).read_text())
for name,digest in index['files'].items():assert hashlib.sha256((root/name).read_bytes()).hexdigest()==digest,name
with socket.socket() as s:s.bind(('127.0.0.1',0));port=s.getsockname()[1]
url=f'http://127.0.0.1:{port}';log=open(root/'install-smoke.log','w')
env=dict(os.environ,GAMEDIRECTOR_WORKBENCH=url)
server=subprocess.Popen([str(runtime/manifest['apps']['workbench']),'--port',str(port),'--data',str(root/'local-data')],cwd=root,env=env,stdout=log,stderr=log,start_new_session=os.name!='nt')
mcp=None
try:
 service=wait_service(root/'local-data/.service.json',server,timeout=40)
 env['GAMEDIRECTOR_WORKBENCH_TOKEN']=service['token']
 with urllib.request.urlopen(urllib.request.Request(url+'/api/state',headers=auth_headers(service)),timeout=2) as r:assert json.load(r)['projects']==[]
 with urllib.request.urlopen(url) as r:
  page=r.read().decode();assert '<html lang="en"' in page and 'data-i18n=' in page
 with urllib.request.urlopen(url+'/ui-catalog.mjs') as r:assert 'GameDirector · Workbench' in r.read().decode()
 mcp=subprocess.Popen([str(runtime/manifest['apps']['cli']),'mcp'],cwd=root,env=env,stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=log,text=True,bufsize=1,start_new_session=os.name!='nt')
 def send(i,method,params):mcp.stdin.write(json.dumps(dict(jsonrpc='2.0',id=i,method=method,params=params))+'\n');mcp.stdin.flush()
 def read(i):
  end=time.monotonic()+90
  while time.monotonic()<end:
   if select.select([mcp.stdout],[],[],1)[0]:
    line=mcp.stdout.readline()
    if not line:raise RuntimeError('MCP exited')
    r=json.loads(line)
    if r.get('id')==i:return r
  raise TimeoutError(i)
 def tool(i,name,arguments):
  send(i,'tools/call',dict(name=name,arguments=arguments));result=read(i);assert not result.get('error'),result
  content=result['result']['content'];assert not result['result'].get('isError'),result
  return json.loads(next(c['text'] for c in content if c['type']=='text'))
 send(1,'initialize',dict(protocolVersion='2024-11-05',capabilities={},clientInfo=dict(name='workbench-install-smoke',version='1')));assert 'result' in read(1)
 mcp.stdin.write(json.dumps(dict(jsonrpc='2.0',method='notifications/initialized'))+'\n');mcp.stdin.flush()
 send(2,'tools/list',{});names=[t['name'] for t in read(2)['result']['tools']];assert 'gd_studio_produce' in names and 'gd_studio_media_import' in names
 project=tool(3,'gd_studio_project',dict(id='sandring',name='Sandring installation smoke',engine='Three.js',endpoint=a.endpoint));assert len(project['manifest']['roles'])==2
 film=json.loads((root/'samples/films/sandring-greeting.json').read_text());film.update(frameRate=6,width=320,height=180)
 # Exercise the packaged MCP audio import, FilmPlan finishing and media IDs.
 tone=root/'install-tone.wav'
 subprocess.run([env.get('GAMEDIRECTOR_FFMPEG','ffmpeg'),'-v','error','-f','lavfi','-i','sine=frequency=330:duration=1','-c:a','pcm_s16le',str(tone)],check=True)
 audio=tool(30,'gd_studio_media_import',dict(projectId='sandring',filePath=str(tone)))
 film['audio']=[dict(id='installed-sound',mediaId=audio['id'],bus='sfx',at=.5,sourceStart=0,duration=.8,volume=.3,fadeIn=.02,fadeOut=.1)]
 film['subtitles']=[dict(start=.5,end=1.5,text='HELLO')]
 script=root/'film.json';script.write_text(json.dumps(film));assert tool(4,'gd_studio_validate',dict(projectId='sandring',filmPath=str(script)))['valid']
 job=tool(5,'gd_studio_produce',dict(projectId='sandring',filmPath=str(script),requestId='installed-first-film'))
 again=tool(6,'gd_studio_produce',dict(projectId='sandring',filmPath=str(script),requestId='installed-first-film'));assert job['id']==again['id']
 for i in range(7,607):
  job=tool(i,'gd_studio_job_status',dict(jobId=job['id']))
  if job['state'] not in ('Queued','Running'):break
  time.sleep(.5)
 assert job['state']=='Verified',job
 with urllib.request.urlopen(urllib.request.Request(url+'/api/jobs/'+job['id']+'/video',headers=auth_headers(service))) as r:
  data=r.read();assert hashlib.sha256(data).hexdigest()==job['videoHash']
 result=dict(passed=True,archive=str(Path(a.archive).resolve()),archiveSha256=hashlib.sha256(Path(a.archive).read_bytes()).hexdigest(),verifiedFiles=len(index['files']),tools=names,project=project['name'],job=job['id'],state=job['state'],video=job['video'],frames=18,duplicateRequestSameJob=True,servedVideoHashMatches=True,packagedMcpAudioAndCaptions=True)
 (root/'installed-validation.json').write_text(json.dumps(result,indent=2)+'\n');print(json.dumps(result))
finally:
 for process in [mcp,server]:
  if process and process.poll() is None:
   if os.name=='nt':subprocess.run(['taskkill','/PID',str(process.pid),'/T','/F'],stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL)
   else:os.killpg(process.pid,signal.SIGTERM)
   try:process.wait(timeout=20)
   except subprocess.TimeoutExpired:process.kill();process.wait()
 log.close()
