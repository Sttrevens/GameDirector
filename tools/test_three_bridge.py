"""Exercise a running offline Three bridge with real game assets, without a live game."""
import argparse,copy,json,urllib.request,urllib.error,hashlib,subprocess
from pathlib import Path
repo=Path(__file__).resolve().parents[1]
p=argparse.ArgumentParser();p.add_argument('--endpoint',required=True);p.add_argument('--out',required=True);p.add_argument('--gd',default=str(repo/'src/GameDirector.Cli/bin/Debug/net8.0/gd.dll'));a=p.parse_args()
contract=json.loads(subprocess.check_output(['dotnet',a.gd,'contract','--json']))
states=contract['take']['states'];errors=contract['bridge']['errors'];budgets=contract['bridge']['budgets']
out=Path(a.out);out.mkdir(parents=True,exist_ok=False)
def request(route,data=None,raw=None):
 payload=raw if raw is not None else (json.dumps(data).encode() if data is not None else None)
 req=urllib.request.Request(a.endpoint+route,data=payload,headers={'Content-Type':'application/json'})
 with urllib.request.urlopen(req,timeout=budgets['takeStartTimeoutSeconds'] if route=='/take/start' else budgets['requestTimeoutSeconds']) as r:return r.read()
def get(route):return json.loads(request(route))
def post(route,data):return json.loads(request(route,data))
def check_roles(receipt):
 for review in receipt.get('reviews',[]):
  c=review['composition']
  if not c.get('active'):continue
  for role in c.get('roles',[]):
   assert role['kind'] in contract['review']['roles']['kinds'] and role['state'] in contract['review']['roles']['states'],role
   if 'rect' in role:assert len(role['rect'])==4 and role['state']=='in-front',role
   if 'crown' in role:assert len(role['crown'])==3,role
m=get('/manifest');roles=[r['id'] for r in m['roles'] if r['presentAtStart']];assert len(roles)>=2
loc=m['locations'][0]['id'];rows=[]
for cut_first in [False,True]:
 shot=lambda role,t,d:{'t':t,'type':'camera.shot','shot':{'type':'lockoff','subject':role,'from':loc,'frame':'wide','durationSeconds':d}}
 cut=shot(roles[1],1,1);despawn={'t':1,'type':'actor.despawn','role':roles[0]}
 timeline={'version':'0.1','id':'bridge-regression','cues':[shot(roles[0],0,1)]+([cut,despawn] if cut_first else [despawn,cut])}
 take=post('/take/start',dict(timeline=timeline,frameRate=1,width=64,height=64,sourceFingerprint=m['capabilities']['presentation.sourceFingerprint']))
 tid=take['takeId']
 try:
  for bad,kind in [(dict(takeId='stale-owner',frameIndex=0),'stateConflict'),(dict(takeId=tid,frameIndex=999),'stateConflict'),(b'{broken','invalidRequest')]:
   try:
    if isinstance(bad,bytes):request('/take/frame',raw=bad)
    else:request('/take/frame',bad)
    raise AssertionError('invalid request accepted')
   except urllib.error.HTTPError as e:assert e.code==errors[kind]['status'] and json.loads(e.read())['code']==errors[kind]['code'],e
   current=get('/take')['take'];assert current['state']==states['capturing'] and current['takeId']==tid and current['capturedFrames']==0
  hashes=[]
  for i in range(take['frameCount']):
   data=dict(takeId=tid,frameIndex=i);frame=request('/take/frame',data)
   assert frame==request('/take/frame',data),'retry changed pixels'
   (out/f'{int(cut_first)}-{i}.png').write_bytes(frame);hashes.append(hashlib.sha256(frame).hexdigest())
  receipt=get('/take');check_roles(receipt);assert receipt['take']['state']==states['completed'];assert len(receipt['events'])==3
  rows.append(dict(cutFirst=cut_first,invalidRequestsPreserveOwner=True,identicalRetries=True,receipt=receipt,frameHashes=hashes))
 finally:post('/take/stop',dict(takeId=tid))
# Replay-only steps are ordered/idempotent 204s; no camera is required before
# capture. With the same timeline, the first kept PNG equals the full take.
base_timeline={'version':'0.1','id':'advance-regression','cues':[shot(roles[0],0,2)]}
full=post('/take/start',dict(timeline=base_timeline,frameRate=1,width=64,height=64))
reference=[request('/take/frame',dict(takeId=full['takeId'],frameIndex=i)) for i in range(2)]
post('/take/stop',dict(takeId=full['takeId']))
for late_camera in [False,True]:
 timeline=copy.deepcopy(base_timeline)
 if late_camera:timeline['cues']=[{'t':0,'type':'marker','label':'empty-prefix'},shot(roles[0],1,1)]
 take=post('/take/start',dict(timeline=timeline,frameRate=1,width=64,height=64,captureFromFrame=1))
 assert take.get('captureFromFrame')==1 and take['frameCount']==2,take
 tid=take['takeId']
 try:
  for bad in [dict(takeId=tid,frameIndex=0),dict(takeId=tid,frameIndex=1,advanceOnly=True),dict(takeId=tid,frameIndex=1)]:
   try:request('/take/frame',bad);raise AssertionError('wrong mode/order accepted')
   except urllib.error.HTTPError as e:assert e.code==errors['stateConflict']['status'] and json.loads(e.read())['code']==errors['stateConflict']['code']
  data=dict(takeId=tid,frameIndex=0,advanceOnly=True)
  for _ in range(2):
   req=urllib.request.Request(a.endpoint+'/take/frame',data=json.dumps(data).encode(),headers={'Content-Type':'application/json'})
   with urllib.request.urlopen(req,timeout=budgets['requestTimeoutSeconds']) as response:assert response.status==204 and response.read()==b''
  snapshot=get('/take');assert snapshot['take']['capturedFrames']==0 and snapshot['reviews']==[]
  data=dict(takeId=tid,frameIndex=1);frame=request('/take/frame',data)
  assert frame==request('/take/frame',data)
  if not late_camera:assert frame==reference[1],'replay-only changed captured pixels'
  (out/f'advance-{int(late_camera)}.png').write_bytes(frame)
  snapshot=get('/take');check_roles(snapshot);assert snapshot['take']['state']==states['completed'] and snapshot['take']['capturedFrames']==1
  assert [r['frame'] for r in snapshot['reviews']]==[1]
  rows.append(dict(lateCamera=late_camera,advanceRetries=True,capturedFrameMatchesFull=not late_camera,receipt=snapshot,frameHashes=[hashlib.sha256(frame).hexdigest()]))
 finally:post('/take/stop',dict(takeId=tid))
for from_frame in [-1,2]:
 try:post('/take/start',dict(timeline=base_timeline,frameRate=1,width=64,height=64,captureFromFrame=from_frame));raise AssertionError('invalid capture start accepted')
 except urllib.error.HTTPError as e:assert e.code==errors['invalidRequest']['status'] and json.loads(e.read())['code']==errors['invalidRequest']['code']
(out/'result.json').write_text(json.dumps(rows,indent=2)+'\n')
print(json.dumps(dict(passed=True,orders=2,invalidRequests=14,advanceRetries=4,frames=8,evidence=str(out))))
