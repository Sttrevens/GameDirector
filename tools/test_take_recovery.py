"""Black-box CLI recovery against an engine-neutral deterministic HTTP fixture.
The fixture is protocol evidence, not another game's runtime acceptance.
"""
import hashlib, json, os, struct, subprocess, tempfile, threading, uuid, zlib
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from test_temp import temp_directory
repo=Path(__file__).resolve().parents[1]
with temp_directory('gd-recovery-tests-') as root:
 cli=repo/'src/GameDirector.Cli/bin/Debug/net8.0/gd.dll'
 contract=json.loads(subprocess.check_output(['dotnet',str(cli),'contract','--json']))
 states=contract['take']['states'];errors=contract['bridge']['errors'];defaults=contract['take']['requestDefaults']
 def frame_count(body):
  with tempfile.TemporaryDirectory(dir=root) as scratch:
   path=Path(scratch);(path/'timeline.json').write_text(json.dumps(body['timeline']));(path/'manifest.json').write_text(json.dumps(manifest))
   return json.loads(subprocess.check_output(['dotnet',str(cli),'compile',str(path/'timeline.json'),'--manifest',str(path/'manifest.json'),'--take','--fps',str(body['frameRate']),'--width',str(body['width']),'--height',str(body['height']),'--from-frame',str(body.get('captureFromFrame',defaults['captureFromFrame']))]))['frameCount']
 source=root/'source-project';source.mkdir();(source/'source.txt').write_text('user game source stays unchanged')
 manifest=dict(game='RecoveryFixture',gameVersion='1',manifestVersion='0.1',roles=[dict(id='actor',defaultActor='body',presentAtStart=True)],actors=[dict(id='body',clips=['Idle'])],locations=[],audio=[],shotTypes=['lockoff'],frameTypes=['full'],capabilities={'director.mode':'offline-sandbox','presentation.sourceFingerprint':'fixture-v1','project.sourceRoot':str(source)})
 timeline=dict(version='0.1',id='recovery',cues=[dict(t=0,type='camera.shot',shot=dict(type='lockoff',subject='actor',frame='full',durationSeconds=1))])
 (root/'timeline.json').write_text(json.dumps(timeline))
 def png(index):
  def chunk(tag,data): return struct.pack('>I',len(data))+tag+data+struct.pack('>I',zlib.crc32(tag+data))
  pixels=b''.join(b'\0'+bytes((index*13%256,90,160))*64 for _ in range(64))
  return b'\x89PNG\r\n\x1a\n'+chunk(b'IHDR',struct.pack('>IIBBBBB',64,64,8,2,0,0,0))+chunk(b'IDAT',zlib.compress(pixels))+chunk(b'IEND',b'')
 state={'take':None,'fail_frame':None,'starts':0,'lose_start':False,'requests':[],'next':0}
 class Handler(BaseHTTPRequestHandler):
  def log_message(self,*a):pass
  def send(self,data,status=200,mime='application/json'):
   if not isinstance(data,bytes):data=json.dumps(data).encode()
   self.send_response(status);self.send_header('Content-Type',mime);self.send_header('Content-Length',str(len(data)));self.end_headers();self.wfile.write(data)
  def fail(self,kind,message):self.send({'error':message,'code':errors[kind]['code']},errors[kind]['status'])
  def do_GET(self):
   self.send(manifest if self.path=='/manifest' else {'take':state['take']})
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
    state['starts']+=1
    state['next']=0
    state['take']=dict(takeId=uuid.uuid4().hex,timelineId=body['timeline']['id'],state=states['capturing'],frameRate=body['frameRate'],width=body['width'],height=body['height'],frameCount=frame_count(body),captureFromFrame=body.get('captureFromFrame',defaults['captureFromFrame']),capturedFrames=0)
    self.send(state['take'],errors['busy']['status'] if state['lose_start'] else 200)
   elif self.path=='/take/stop':state['take']['state']=states['cancelled'];self.send({})
   elif self.path=='/take/frame':
    i=body['frameIndex'];t=state['take'];advance=body.get('advanceOnly',defaults['advanceOnly'])
    state['requests'].append((i,advance))
    if i==state['fail_frame']:self.fail('internal','injected frame failure');return
    if body['takeId']!=t['takeId'] or i!=state['next'] or advance!=(i<t['captureFromFrame']):self.fail('stateConflict','frame order/mode mismatch');return
    state['next']+=1
    if not advance:t['capturedFrames']+=1
    if i+1==t['frameCount']:t['state']=states['completed']
    self.send(b'',204) if advance else self.send(png(i),mime='image/png')
   else:self.send({},404)
 server=ThreadingHTTPServer(('127.0.0.1',0),Handler);threading.Thread(target=server.serve_forever,daemon=True).start()
 endpoint=f'http://127.0.0.1:{server.server_port}'
 def run(*args,ok=True):
  p=subprocess.run(['dotnet',str(cli),*map(str,args)],capture_output=True,text=True)
  if ok and p.returncode:raise AssertionError(p.stderr+p.stdout)
  if not ok and not p.returncode:raise AssertionError('expected failure')
  return p
 try:
  # Failure in either replay or capture must start a fresh performance at zero,
  # preserve the original journal, and keep picture numbering independent of time.
  for fail,phase in [(1,'advance'),(4,'capture')]:
   partial_from=root/phase;state['fail_frame']=fail;state['requests']=[]
   failed=run('take',root/'timeline.json','--out',partial_from,'--endpoint',endpoint,'--fps',6,'--width',64,'--height',64,'--keep-frames','--from-frame',3,ok=False)
   assert 'injected frame failure' in failed.stderr,failed.stderr
   original={f.name:f.read_bytes() for f in (partial_from/'frames').glob('*.png')}
   assert len(original)==(0 if phase=='advance' else 1)
   job=json.loads((partial_from/'job.json').read_text());assert job['captureFromFrame']==3 and job['expectedFrames']==3
   state['fail_frame']=None;state['requests']=[]
   resumed=run('resume',partial_from)
   assert state['requests']==[(i,i<3) for i in range(6)],state['requests']
   child_from=partial_from/json.loads((partial_from/'job.json').read_text())['nextAttempt']
   journal=[json.loads(line) for line in (child_from/'frames.jsonl').read_text().splitlines()]
   assert [f['frameIndex'] for f in journal]==[0,1,2]
   assert [f['sha256'] for f in journal]==[hashlib.sha256(png(i)).hexdigest() for i in [3,4,5]]
   artifact=json.loads((child_from/'take.json').read_text());assert artifact['captureFromFrame']==3 and artifact['frameCount']==3 and artifact['duration']==.5
   assert original=={f.name:f.read_bytes() for f in (partial_from/'frames').glob('*.png')}
   assert 'rendered 3 frames, advanced 3' in resumed.stderr
  partial=root/'partial';state['fail_frame']=3
  run('take',root/'timeline.json','--out',partial,'--endpoint',endpoint,'--fps',6,'--width',64,'--height',64,'--keep-frames',ok=False)
  old=hashlib.sha256((partial/'frames/000000.png').read_bytes()).hexdigest();state['fail_frame']=None
  run('resume',partial)
  assert hashlib.sha256((partial/'frames/000000.png').read_bytes()).hexdigest()==old
  child=partial/json.loads((partial/'job.json').read_text())['nextAttempt']
  assert json.loads((child/'take.json').read_text())['state']=='Verified'
  assert (child/'frames').is_dir(),'--keep-frames retains the frame journal after verify'
  stale=root/'stale';state['fail_frame']=2
  run('take',root/'timeline.json','--out',stale,'--endpoint',endpoint,'--fps',6,'--width',64,'--height',64,ok=False)
  before=state['starts'];manifest['capabilities']['presentation.sourceFingerprint']='fixture-v2'
  assert 'source changed' in run('resume',stale,ok=False).stderr
  assert state['starts']==before
  manifest['capabilities']['presentation.sourceFingerprint']='fixture-v1';state['fail_frame']=None
  unknown=root/'unknown';state['lose_start']=True
  run('take',root/'timeline.json','--out',unknown,'--endpoint',endpoint,'--fps',6,'--width',64,'--height',64,ok=False)
  assert 'unacknowledged' in run('resume',unknown,ok=False).stderr
  state['lose_start']=False;state['take']['state']=states['cancelled']
  run('resume',unknown)
  # Default retention: a verified take keeps picture.mp4, the receipts and the
  # frames.jsonl provenance, but reclaims the frame directory itself.
  unknown_child=unknown/json.loads((unknown/'job.json').read_text())['nextAttempt']
  assert not (unknown_child/'frames').exists() and (unknown_child/'picture.mp4').exists() and (unknown_child/'frames.jsonl').exists()
  # A complete retained frame set survives loss of the encoded output and an offline engine.
  (child/'picture.mp4').unlink();server.shutdown();server.server_close()
  (child_from/'picture.mp4').unlink();run('resume',partial_from)
  assert json.loads((child_from/'take.json').read_text())['captureFromFrame']==3
  run('resume',partial)
  assert (child/'picture.mp4').exists()
  (child/'take.json').write_text('{"state":')
  run('resume',partial)
  assert json.loads((child/'take.json').read_text())['state']=='Verified'
  (child/'take.json').unlink()
  run('resume',partial)
  assert json.loads((child/'take.json').read_text())['state']=='Verified'
  print('PASS: capture-only journal, ordered replay and mid-advance/mid-capture recovery; partial replay preserves frames; source drift rejects; lost start preserves active take; keep-frames offline re-encode; default take reclaims frames; '+str(root))
 finally:
  server.shutdown();server.server_close()
