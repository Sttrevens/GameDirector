"""Black-box Workbench contracts, real media encoding, synthetic engine/model.
This is not engine or real model-provider acceptance.
"""
import base64, array, copy, hashlib, json, math, os, socket, struct, subprocess, tempfile, threading, time, urllib.request, urllib.error, uuid, zlib
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from workbench_test_client import auth_headers, wait_service
urllib.request.install_opener(urllib.request.build_opener(urllib.request.ProxyHandler({})))
from test_temp import temp_directory, stop_process
repo=Path(__file__).resolve().parents[1]
with temp_directory('gd-workbench-contract-tests-') as root:
 cli=repo/'src/GameDirector.Cli/bin/Debug/net8.0/gd.dll'
 contract=json.loads(subprocess.check_output(['dotnet',str(cli),'contract','--json']))
 states=contract['take']['states'];errors=contract['bridge']['errors'];defaults=contract['take']['requestDefaults']
 def frame_count(body):
  with tempfile.TemporaryDirectory(dir=root) as scratch:
   path=Path(scratch);(path/'timeline.json').write_text(json.dumps(body['timeline']));(path/'manifest.json').write_text(json.dumps(manifest))
   return json.loads(subprocess.check_output(['dotnet',str(cli),'compile',str(path/'timeline.json'),'--manifest',str(path/'manifest.json'),'--take','--fps',str(body['frameRate']),'--width',str(body['width']),'--height',str(body['height']),'--from-frame',str(body.get('captureFromFrame',defaults['captureFromFrame']))]))['frameCount']
 source=root/'source-project';source.mkdir();(source/'source.txt').write_text('user game source stays unchanged')
 subprocess.run(['git','init','-q',str(source)],check=True)
 source_hash=hashlib.sha256((source/'source.txt').read_bytes()).hexdigest()
 manifest=dict(game='StudioFixture',gameVersion='1',manifestVersion='0.1',roles=[dict(id='hero',defaultActor='body',presentAtStart=True)],actors=[dict(id='body',clips=['wave'])],locations=[dict(id='front',position=[0,1,3]),dict(id='side',position=[3,1,1])],audio=[],shotTypes=['lockoff'],frameTypes=['full','closeup'],capabilities={'director.mode':'offline-sandbox','presentation.sourceFingerprint':'fixture-v1','project.sourceRoot':str(source)})
 film=dict(version=1,title='Workbench contract',frameRate=6,width=64,height=64,scenes=[dict(id='scene',performance=[dict(type='actor.anim',t=0,role='hero',clip='wave')],shots=[dict(id='one',start=0,end=1,purpose='Establish',camera=dict(type='lockoff',subject='hero',frame='full',**{'from':'front'})),dict(id='two',start=1,end=2,purpose='Respond',camera=dict(type='lockoff',subject='hero',frame='closeup',**{'from':'front'}))])])
 state=dict(take=None,starts=0,modelCalls=0,slow=False,stallStart=False)
 def png(index):
  def chunk(tag,data):return struct.pack('>I',len(data))+tag+data+struct.pack('>I',zlib.crc32(tag+data))
  pixels=b''.join(b'\0'+bytes((index*13%256,90,160))*64 for _ in range(64))
  return b'\x89PNG\r\n\x1a\n'+chunk(b'IHDR',struct.pack('>IIBBBBB',64,64,8,2,0,0,0))+chunk(b'IDAT',zlib.compress(pixels))+chunk(b'IEND',b'')
 class Handler(BaseHTTPRequestHandler):
  def log_message(self,*a):pass
  def send(self,data,status=200,mime='application/json'):
   data=data if isinstance(data,bytes) else json.dumps(data).encode();self.send_response(status);self.send_header('Content-Type',mime);self.send_header('Content-Length',str(len(data)));self.end_headers()
   try:self.wfile.write(data)
   except (BrokenPipeError,ConnectionResetError):pass
  def fail(self,kind,message):self.send({'error':message,'code':errors[kind]['code']},errors[kind]['status'])
  def do_GET(self):self.send(manifest if self.path=='/manifest' else {'take':state['take']})
  def do_POST(self):
   if self.headers.get('Transfer-Encoding','').lower()=='chunked':
    raw=b''
    while True:
     n=int(self.rfile.readline().strip().split(b';')[0],16)
     if n==0:self.rfile.readline();break
     raw+=self.rfile.read(n);self.rfile.read(2)
   else:raw=self.rfile.read(int(self.headers.get('Content-Length','0')))
   body=json.loads(raw or '{}')
   if self.path=='/take/start':
    if state['take'] and state['take']['state']==states['capturing']:return self.fail('stateConflict','owned')
    state['starts']+=1
    state['take']=dict(takeId=uuid.uuid4().hex,timelineId=body['timeline']['id'],state=states['capturing'],frameRate=body['frameRate'],width=body['width'],height=body['height'],frameCount=frame_count(body),captureFromFrame=body.get('captureFromFrame',defaults['captureFromFrame']),capturedFrames=0)
    state['next']=0
    if state['stallStart']:return self.fail('timeout','fixture engine start deadline')
    self.send(state['take'])
   elif self.path=='/take/stop':state['take']['state']=states['cancelled'];self.send({})
   elif self.path=='/take/frame':
    t=state['take'];i=body['frameIndex']
    advance=body.get('advanceOnly',defaults['advanceOnly'])
    if body['takeId']!=t['takeId']:return self.fail('stateConflict','take identity mismatch')
    if i!=state['next'] or advance!=(i<t['captureFromFrame']):return self.fail('stateConflict','frame order/mode')
    if state['slow']:time.sleep(.12)
    state['next']+=1
    if not advance:t['capturedFrames']+=1
    if i+1==t['frameCount']:t['state']=states['completed']
    self.send(b'',204) if advance else self.send(png(i),mime='image/png')
   elif self.path=='/chat/completions':
    state['modelCalls']+=1;generated=copy.deepcopy(film)
    if state['modelCalls']==1:generated['scenes'][0]['performance'][0]['clip']='invented'
    self.send(dict(choices=[dict(message=dict(content=json.dumps(generated)))]))
   else:self.send({},404)
 fixture=ThreadingHTTPServer(('127.0.0.1',0),Handler);threading.Thread(target=fixture.serve_forever,daemon=True).start()
 with socket.socket() as sock:sock.bind(('127.0.0.1',0));port=sock.getsockname()[1]
 base=f'http://127.0.0.1:{port}/api/'
 log=open(root/'server.log','w');process=None;service=None
 def launch():
  global process,service
  descriptor=root/'store/.service.json';prior=descriptor.read_bytes() if descriptor.exists() else None
  process=subprocess.Popen(['dotnet',str(repo/'src/GameDirector.Workbench/bin/Debug/net8.0/gamedirector-workbench.dll'),'--port',str(port),'--data',str(root/'store')],stdout=log,stderr=log)
  service=wait_service(descriptor,process,prior,timeout=60)
  call('state')
 def call(path,body=None,status=200,headers=None):
  data=None if body is None else json.dumps(body).encode()
  req=urllib.request.Request(base+path,data=data,headers={'Content-Type':'application/json',**auth_headers(service),**(headers or {})})
  try:
   with urllib.request.urlopen(req,timeout=45) as r:code=r.status;raw=r.read()
  except urllib.error.HTTPError as e:code=e.code;raw=e.read()
  assert code==status,(path,code,raw)
  return json.loads(raw) if raw else None
 def wait(job,expected='Verified'):
  for _ in range(600):
   j=call('jobs/'+job['id'])
   if j['state'] not in ('Queued','Running'):
    assert j['state']==expected,j;return j
   time.sleep(.1)
  raise AssertionError('job timeout')
 def req(f):return dict(requestId=uuid.uuid4().hex,projectId='fixture',film=f)
 try:
  launch();call('projects',dict(id='fixture',name='Fixture',engine='test',endpoint=f'http://127.0.0.1:{fixture.server_port}'))
  call('projects/fixture/connect',{})
  call('state',headers={'Origin':'https://untrusted.invalid'},status=403)
  call('state',headers={'Host':'untrusted.invalid'},status=403)
  call('projects',dict(id='../escape',name='x',endpoint='http://127.0.0.1:4'),status=422)
  call('projects',dict(id='remote',name='x',endpoint='http://192.168.1.1:4'),status=422)
  invalid=copy.deepcopy(film);invalid['scenes'][0]['performance'][0]['clip']='missing';call('jobs',req(invalid),status=422);assert state['starts']==0
  first=req(film);j=call('jobs',first);assert call('jobs',first)['id']==j['id'];wait(j);assert state['starts']==2
  edit=json.loads((root/'store'/'jobs'/j['id']/'edit.json').read_text())
  assert [(r['start'],r['end']) for r in edit['ranges']]==[(0,1),(0,1)],edit
  takes=list((root/'store'/'cache').rglob('take.json'))
  assert sorted((json.loads(t.read_text())['captureFromFrame'],json.loads(t.read_text())['frameCount']) for t in takes)==[(0,6),(6,6)]
  second_take=next(t for t in takes if json.loads(t.read_text())['captureFromFrame']==6)
  journal=[json.loads(line) for line in (second_take.parent/'frames.jsonl').read_text().splitlines()]
  assert [f['frameIndex'] for f in journal]==list(range(6))
  assert [f['sha256'] for f in journal]==[hashlib.sha256(png(i)).hexdigest() for i in range(6,12)]
  conflict=copy.deepcopy(first);conflict['film']['title']='changed';call('jobs',conflict,status=409)
  # A native or browser client produces an immutable saved document revision.
  docpath='projects/fixture/documents/shared-film'
  d1=call(docpath,dict(expectedRevision=0,requestId='doc-save-1',film=film))
  changed=copy.deepcopy(film);changed['title']='Later draft'
  d2=call(docpath,dict(expectedRevision=1,requestId='doc-save-2',film=changed))
  dj=wait(call(docpath+'/produce',dict(revision=1,requestId='doc-take-1')))
  assert dj['request']['documentRevision']==1 and dj['request']['film']['title']==film['title']
  assert dj['reusedShots']==2 and state['starts']==2
  assert call(docpath+'/produce',dict(revision=1,requestId='doc-take-1'))['id']==dj['id']
  call(docpath,dict(expectedRevision=1,requestId='doc-stale',film=film),status=409)

  revision=copy.deepcopy(film);revision['scenes'][0]['shots'][1]['camera']['from']='side'
  j2=wait(call('jobs',req(revision)));assert state['starts']==3 and j2['reusedShots']==1,j2
  # Real sound mixing: imported bytes are immutable, cues use edited-film time,
  # and changing only the mix must not recapture either camera.
  tone=root/'tone.wav'
  subprocess.run([os.environ.get('GAMEDIRECTOR_FFMPEG','ffmpeg'),'-v','error','-f','lavfi','-i','sine=frequency=440:duration=1.5','-c:a','pcm_s16le',str(tone)],check=True)
  asset=call('projects/fixture/media',dict(name='voice.wav',base64=base64.b64encode(tone.read_bytes()).decode()))
  assert call('projects/fixture/media',dict(name='voice.wav',base64=base64.b64encode(tone.read_bytes()).decode()))['id']==asset['id']
  voiced=copy.deepcopy(revision);voiced['audio']=[dict(id='voice',mediaId=asset['id'],bus='dialogue',at=.5,sourceStart=.2,duration=1,volume=.5,fadeIn=.02,fadeOut=.02)]
  voiced['subtitles']=[dict(start=.5,end=1.5,text='Hello')]
  start_count=state['starts'];sound_job=wait(call('jobs',req(voiced)));assert state['starts']==start_count and sound_job['reusedShots']==2
  raw=subprocess.check_output([os.environ.get('GAMEDIRECTOR_FFMPEG','ffmpeg'),'-v','error','-i',sound_job['video'],'-vn','-f','f32le','-ac','1','-ar','48000','-'])
  samples=array.array('f',raw)
  def rms(a,b):
   section=samples[int(a*48000):int(b*48000)];return math.sqrt(sum(x*x for x in section)/len(section))
  assert rms(.1,.3)<.001 and rms(.7,1.2)>.01 and rms(1.7,1.9)<.001,'Audio onset/tail timing mismatch'
  before_mix=state['starts'];voiced['audio'][0]['volume']=.25;mix_job=wait(call('jobs',req(voiced)));assert state['starts']==before_mix and mix_job['reusedShots']==2
  # Container/video length and timestamp offsets cannot invent playable samples.
  offset=root/'offset.m4a'
  subprocess.run([os.environ.get('GAMEDIRECTOR_FFMPEG','ffmpeg'),'-v','error','-f','lavfi','-i','color=size=16x16:duration=5','-f','lavfi','-i','sine=duration=1','-filter:a','asetpts=PTS+2/TB','-c:v','libx264','-c:a','aac',str(offset)],check=True)
  short_asset=call('projects/fixture/media',dict(name='offset.m4a',base64=base64.b64encode(offset.read_bytes()).decode()))
  assert .95 < short_asset['duration'] < 1.1,short_asset
  impossible=copy.deepcopy(voiced);impossible['audio'][0].update(mediaId=short_asset['id'],sourceStart=4)
  call('jobs',req(impossible),status=422);assert state['starts']==before_mix
  call('projects/fixture/media',dict(name='playlist.wav',base64=base64.b64encode(b'#EXTM3U\n#EXTINF:10,\nhttp://127.0.0.1:1/test.wav\n').decode()),status=500)
  invalid_sound=copy.deepcopy(voiced);invalid_sound['audio'][0]['sourceStart']=1;call('jobs',req(invalid_sound),status=422);assert state['starts']==before_mix
  # An incompatible cache entry is set aside and re-recorded, never silently
  # reused and never fatal to the production.
  keys={s['id']:s['cacheKey'] for s in call('validate',req(revision))['shots']}
  cache=root/'store'/'cache'/'format.4'/'fixture'
  victim=cache/keys['one']
  journal=json.loads((victim/'job.json').read_text());journal['version']=99;(victim/'job.json').write_text(json.dumps(journal))
  before_stale=state['starts'];stale_job=wait(call('jobs',req(revision)))
  assert state['starts']==before_stale+1 and stale_job['reusedShots']==1,stale_job
  assert len(list(cache.glob('*.stale-*')))==1
  assert json.loads((victim/'job.json').read_text())['version']==1,'incompatible entry was re-recorded at the same key'
  call('projects',dict(id='inside-store',name='bad',endpoint=f'http://127.0.0.1:{fixture.server_port}',sourceRoot=str(root/'store')),status=422)
  # Startup cannot create even lock files in a normal Git source tree.
  forbidden=source/'output'
  refusal=subprocess.run(['dotnet',str(repo/'src/GameDirector.Workbench/bin/Debug/net8.0/gamedirector-workbench.dll'),'--data',str(forbidden),'--port','0'],stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL,timeout=15)
  assert refusal.returncode!=0 and not forbidden.exists()
  # Reparse-point ownership paths cannot redirect writes into game source.
  # Windows junctions exercise this contract without symbolic-link privileges.
  trap=root/'trap-store';trap.mkdir()
  if os.name == 'nt':
   def psquote(value):return "'"+str(value).replace("'","''")+"'"
   subprocess.run(['powershell.exe','-NoProfile','-NonInteractive','-Command',
       "$ErrorActionPreference='Stop'; New-Item -ItemType Junction -Path "+psquote(trap/'.owner')+' -Target '+psquote(source)+' | Out-Null'],check=True)
  else:(trap/'.owner').symlink_to(source/'should-not-exist')
  refusal=subprocess.run(['dotnet',str(repo/'src/GameDirector.Workbench/bin/Debug/net8.0/gamedirector-workbench.dll'),'--data',str(trap),'--port','0'],stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL,timeout=15)
  assert refusal.returncode!=0 and not (source/'should-not-exist').exists()
  assert hashlib.sha256((source/'source.txt').read_bytes()).hexdigest()==source_hash
  assert sorted(p.name for p in source.iterdir())==['.git','source.txt']
  provider=call('provider',dict(endpoint=f'http://127.0.0.1:{fixture.server_port}/chat/completions',model='fixture',apiKey='test-key-not-to-persist'))
  assert 'apiKey' not in provider
  direct=dict(projectId='fixture',brief='Use only existing assets, two silent shots.',currentFilm=None,render=True,requestId=uuid.uuid4().hex)
  proposal=call('direct',direct);wait(proposal['job']);assert state['modelCalls']==2
  assert call('direct',direct)['job']['id']==proposal['job']['id'];assert state['modelCalls']==2
  # Stop the service during capture, then prove restart does not auto-mutate the engine.
  slow=copy.deepcopy(film);slow['scenes'][0]['shots'][0]['end']=8;slow['scenes'][0]['shots'][1]['end']=9
  state['slow']=True;j3=call('jobs',req(slow))
  for _ in range(100):
   if state['take']['state']==states['capturing'] and state['take']['capturedFrames']>=2:break
   time.sleep(.05)
  process.kill();process.wait();before=state['starts'];launch();assert call('jobs/'+j3['id'])['state']=='Interrupted';assert state['starts']==before
  manifest['capabilities']['presentation.sourceFingerprint']='fixture-v2';call('jobs/'+j3['id']+'/resume',{});wait(j3,'Failed');assert state['starts']==before
  manifest['capabilities']['presentation.sourceFingerprint']='fixture-v1';state['slow']=False;call('jobs/'+j3['id']+'/resume',{});wait(j3)
  # Explicit cancel preserves a resumable job and releases owned engine work.
  cancel=copy.deepcopy(film);cancel['scenes'][0]['shots'][0]['end']=10;state['slow']=True;j4=call('jobs',req(cancel))
  for _ in range(100):
   if state['take']['state']==states['capturing']:break
   time.sleep(.05)
  call('storage/prune',{},status=409)
  call('jobs/'+j4['id']+'/cancel',{});wait(j4,'Cancelled');state['slow']=False
  # An engine-reported deadline is a failed recoverable operation, not a user
  # cancellation. Client-owned budgets/backoff are exercised by .NET transport tests.
  timeout_film=copy.deepcopy(film);timeout_film['scenes'][0]['shots'][0]['end']=12;state['stallStart']=True
  timed=wait(call('jobs',req(timeout_film)),'Failed');assert 'HTTP '+str(errors['timeout']['status']) in timed['progress'] and 'fixture engine start deadline' in timed['progress'],timed
  state['stallStart']=False
  # Storage usage is reported per area; prune reclaims unreferenced leftovers and
  # whole cache generations that are dead by location (old format.N, legacy layout).
  take=next(d for d in cache.iterdir() if d.is_dir() and '.orphan-' not in d.name and '.stale-' not in d.name and (d/'take.json').exists() and (d/'picture.mp4').exists())
  (take/'previous-deadbeef.mp4').write_bytes(b'old');(take/'encode-deadbeef.mp4').write_bytes(b'cand')
  orphan=cache/(take.name+'.orphan-deadbeef');orphan.mkdir();(orphan/'junk.txt').write_text('x'*4096)
  stale_dir=cache/(take.name+'.stale-deadbeef');stale_dir.mkdir();(stale_dir/'junk.txt').write_text('x'*2048)
  fabricated=cache/('0'*64);fabricated.mkdir()
  (fabricated/'job.json').write_text(json.dumps(dict(version=1,state='Verified',endpoint='http://127.0.0.1:1',timelineSha256='0'*64,manifestSha256='0'*64,sourceFingerprint='x',takeId='t',frameRate=6,width=64,height=64,expectedFrames=6)))
  (fabricated/'frames').mkdir();(fabricated/'frames'/'000000.png').write_bytes(png(0))
  (fabricated/'attempts').mkdir();(fabricated/'attempts'/'child.txt').write_text('x')
  (fabricated/'frames.jsonl').write_text('{}\n');(fabricated/'picture.mp4').write_bytes(b'pic');(fabricated/'take.json').write_text('{}')
  oldgen=root/'store'/'cache'/'format.2'/'fixture';oldgen.mkdir(parents=True);(oldgen/'deadkey.mp4').write_bytes(b'old'*4096)
  legacy=root/'store'/'cache'/'fixture';legacy.mkdir();(legacy/'deadkey').mkdir();(legacy/'deadkey'/'junk.txt').write_text('x'*4096)
  docs_before=sorted(str(p) for p in (root/'store'/'documents').rglob('*'));media_before=sorted(str(p) for p in (root/'store'/'media').rglob('*'))
  storage=call('storage')
  assert storage['usage']['cache']>0 and storage['usage']['jobs']>0 and storage['usage']['media']>0 and storage['usage']['documents']>0 and 'other' in storage['usage'],storage
  assert storage['usage']['reclaimable']>0,storage
  pruned=call('storage/prune',{});removed=pruned['removed']
  assert pruned['bytesFreed']>0 and any('.orphan-deadbeef' in r for r in removed) and any('.stale-deadbeef' in r for r in removed),pruned
  assert any(r.endswith('previous-deadbeef.mp4') for r in removed) and any(r.endswith('encode-deadbeef.mp4') for r in removed),pruned
  assert any('format.2' in r for r in removed) and any(r.endswith('fixture') for r in removed),pruned
  assert not (root/'store'/'cache'/'format.2').exists() and not legacy.exists()
  assert not (fabricated/'frames').exists() and not (fabricated/'attempts').exists()
  assert (fabricated/'picture.mp4').exists() and (fabricated/'frames.jsonl').exists() and (fabricated/'job.json').exists()
  assert (take/'picture.mp4').exists() and (take/'take.json').exists() and Path(stale_job['video']).exists()
  assert docs_before==sorted(str(p) for p in (root/'store'/'documents').rglob('*')) and media_before==sorted(str(p) for p in (root/'store'/'media').rglob('*'))
  assert not list(cache.glob('*.stale-*')) and not list(cache.glob('*.orphan-*'))
  assert call('storage')['usage']['reclaimable']==0
  assert call('storage/prune',{})['removed']==[],'prune is idempotent'
  # Current-generation verified takes survive prune and are still reused.
  before_reuse=state['starts'];reuse_job=wait(call('jobs',req(revision)));assert state['starts']==before_reuse and reuse_job['reusedShots']==2,reuse_job
  assert not call('provider')['configured'],'API key must not survive service restart'
  for p in (root/'store').rglob('*'):
   if p.is_file() and p.suffix in ('.json','.log'):assert 'test-key-not-to-persist' not in p.read_text(errors='ignore'),p
  result=dict(passed=True,checks=['saved document production pins exact revision','document production retry and stale editor conflict','origin/host isolation','loopback-only project endpoints','invalid bindings rejected before capture','idempotent request and conflict','one-camera change reuses one shot','model API validation repair bounded to two calls','model request retry reuses saved proposal','interrupted job preserved across restart','stale source rejected without capture','resume rebuilds partial capture','explicit cancellation','transport timeout is Failed not Cancelled','API key memory only','immutable sound import','audio onset and tail verified from decoded samples','sound and caption revision reuses all picture','out-of-source audio refused before capture','Git source startup zero writes','dangling lock link zero writes','source project bytes unchanged','decoded samples reject misleading container timestamps','renamed non-audio import refused'],fixtureStarts=state['starts'],modelCalls=state['modelCalls'])
  (root/'result.json').write_text(json.dumps(result,indent=2)+'\n');print(json.dumps(dict(**result,evidence=str(root))))
 finally:
  try:stop_process(process,timeout=20)
  finally:
   try:fixture.shutdown()
   finally:fixture.server_close();log.close()
