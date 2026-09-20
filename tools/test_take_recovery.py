"""Black-box CLI recovery against an engine-neutral deterministic HTTP fixture.
The fixture is protocol evidence, not another game's runtime acceptance.
"""
import hashlib, json, os, struct, subprocess, threading, uuid, zlib
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
repo=Path(__file__).resolve().parents[1]
root=repo/'captures'/'recovery-tests'/uuid.uuid4().hex[:12];root.mkdir(parents=True)
cli=repo/'src/GameDirector.Cli/bin/Debug/net8.0/gd.dll'
source=root/'source-project';source.mkdir();(source/'source.txt').write_text('user game source stays unchanged')
manifest=dict(game='RecoveryFixture',gameVersion='1',manifestVersion='0.1',roles=[dict(id='actor',defaultActor='body',presentAtStart=True)],actors=[dict(id='body',clips=['Idle'])],locations=[],audio=[],shotTypes=['lockoff'],frameTypes=['full'],capabilities={'director.mode':'offline-sandbox','presentation.sourceFingerprint':'fixture-v1','project.sourceRoot':str(source)})
timeline=dict(version='0.1',id='recovery',cues=[dict(t=0,type='camera.shot',shot=dict(type='lockoff',subject='actor',frame='full',durationSeconds=1))])
(root/'timeline.json').write_text(json.dumps(timeline))
def png(index):
 def chunk(tag,data): return struct.pack('>I',len(data))+tag+data+struct.pack('>I',zlib.crc32(tag+data))
 pixels=b''.join(b'\0'+bytes((index*13%256,90,160))*64 for _ in range(64))
 return b'\x89PNG\r\n\x1a\n'+chunk(b'IHDR',struct.pack('>IIBBBBB',64,64,8,2,0,0,0))+chunk(b'IDAT',zlib.compress(pixels))+chunk(b'IEND',b'')
state={'take':None,'fail_frame':None,'starts':0,'lose_start':False}
class Handler(BaseHTTPRequestHandler):
 def log_message(self,*a):pass
 def send(self,data,status=200,mime='application/json'):
  if not isinstance(data,bytes):data=json.dumps(data).encode()
  self.send_response(status);self.send_header('Content-Type',mime);self.send_header('Content-Length',str(len(data)));self.end_headers();self.wfile.write(data)
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
   state['take']=dict(takeId=uuid.uuid4().hex,timelineId='recovery',state='Capturing',frameRate=body['frameRate'],width=body['width'],height=body['height'],frameCount=body['frameRate'],capturedFrames=0)
   self.send(state['take'],503 if state['lose_start'] else 200)
  elif self.path=='/take/stop':state['take']['state']='Cancelled';self.send({})
  elif self.path=='/take/frame':
   i=body['frameIndex'];t=state['take']
   if i==state['fail_frame']:self.send({'error':'injected frame failure'},500);return
   if body['takeId']!=t['takeId']:self.send({},409);return
   t['capturedFrames']=i+1
   if i+1==t['frameCount']:t['state']='Completed'
   self.send(png(i),mime='image/png')
  else:self.send({},404)
server=ThreadingHTTPServer(('127.0.0.1',0),Handler);threading.Thread(target=server.serve_forever,daemon=True).start()
endpoint=f'http://127.0.0.1:{server.server_port}'
def run(*args,ok=True):
 p=subprocess.run(['dotnet',str(cli),*map(str,args)],capture_output=True,text=True)
 if ok and p.returncode:raise AssertionError(p.stderr+p.stdout)
 if not ok and not p.returncode:raise AssertionError('expected failure')
 return p
try:
 partial=root/'partial';state['fail_frame']=3
 run('take',root/'timeline.json','--out',partial,'--endpoint',endpoint,'--fps',6,'--width',64,'--height',64,ok=False)
 old=hashlib.sha256((partial/'frames/000000.png').read_bytes()).hexdigest();state['fail_frame']=None
 run('resume',partial)
 assert hashlib.sha256((partial/'frames/000000.png').read_bytes()).hexdigest()==old
 child=partial/json.loads((partial/'job.json').read_text())['nextAttempt']
 assert json.loads((child/'take.json').read_text())['state']=='Verified'
 stale=root/'stale';state['fail_frame']=2
 run('take',root/'timeline.json','--out',stale,'--endpoint',endpoint,'--fps',6,'--width',64,'--height',64,ok=False)
 before=state['starts'];manifest['capabilities']['presentation.sourceFingerprint']='fixture-v2'
 assert 'source changed' in run('resume',stale,ok=False).stderr
 assert state['starts']==before
 manifest['capabilities']['presentation.sourceFingerprint']='fixture-v1';state['fail_frame']=None
 unknown=root/'unknown';state['lose_start']=True
 run('take',root/'timeline.json','--out',unknown,'--endpoint',endpoint,'--fps',6,'--width',64,'--height',64,ok=False)
 assert 'unacknowledged' in run('resume',unknown,ok=False).stderr
 state['lose_start']=False;state['take']['state']='Cancelled'
 run('resume',unknown)
 # A complete frame set survives loss of the encoded output and an offline engine.
 (child/'picture.mp4').unlink();server.shutdown();server.server_close()
 run('resume',partial)
 assert (child/'picture.mp4').exists()
 (child/'take.json').write_text('{"state":')
 run('resume',partial)
 assert json.loads((child/'take.json').read_text())['state']=='Verified'
 (child/'take.json').unlink()
 run('resume',partial)
 assert json.loads((child/'take.json').read_text())['state']=='Verified' 
 print('PASS: partial replay preserves frames; source drift rejects; lost start preserves active take; offline re-encode; '+str(root))
finally:
 server.shutdown();server.server_close()
