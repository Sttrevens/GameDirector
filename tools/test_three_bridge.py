"""Exercise a running offline Three bridge with real game assets, without a live game."""
import argparse,copy,json,urllib.request,urllib.error,hashlib
from pathlib import Path
p=argparse.ArgumentParser();p.add_argument('--endpoint',required=True);p.add_argument('--out',required=True);a=p.parse_args()
out=Path(a.out);out.mkdir(parents=True,exist_ok=False)
def request(route,data=None,raw=None):
 payload=raw if raw is not None else (json.dumps(data).encode() if data is not None else None)
 req=urllib.request.Request(a.endpoint+route,data=payload,headers={'Content-Type':'application/json'})
 with urllib.request.urlopen(req,timeout=40) as r:return r.read()
def get(route):return json.loads(request(route))
def post(route,data):return json.loads(request(route,data))
m=get('/manifest');roles=[r['id'] for r in m['roles'] if r['presentAtStart']];assert len(roles)>=2
loc=m['locations'][0]['id'];rows=[]
for cut_first in [False,True]:
 shot=lambda role,t,d:{'t':t,'type':'camera.shot','shot':{'type':'lockoff','subject':role,'from':loc,'frame':'wide','durationSeconds':d}}
 cut=shot(roles[1],1,1);despawn={'t':1,'type':'actor.despawn','role':roles[0]}
 timeline={'version':'0.1','id':'bridge-regression','cues':[shot(roles[0],0,1)]+([cut,despawn] if cut_first else [despawn,cut])}
 take=post('/take/start',dict(timeline=timeline,frameRate=1,width=64,height=64,sourceFingerprint=m['capabilities']['presentation.sourceFingerprint']))
 tid=take['takeId']
 try:
  for bad in [dict(takeId='stale-owner',frameIndex=0),dict(takeId=tid,frameIndex=999),b'{broken']:
   try:
    if isinstance(bad,bytes):request('/take/frame',raw=bad)
    else:request('/take/frame',bad)
    raise AssertionError('invalid request accepted')
   except urllib.error.HTTPError as e:assert e.code==400
   current=get('/take')['take'];assert current['state']=='Capturing' and current['takeId']==tid and current['capturedFrames']==0
  hashes=[]
  for i in range(take['frameCount']):
   data=dict(takeId=tid,frameIndex=i);frame=request('/take/frame',data)
   assert frame==request('/take/frame',data),'retry changed pixels'
   (out/f'{int(cut_first)}-{i}.png').write_bytes(frame);hashes.append(hashlib.sha256(frame).hexdigest())
  receipt=get('/take');assert receipt['take']['state']=='Completed';assert len(receipt['events'])==3
  rows.append(dict(cutFirst=cut_first,invalidRequestsPreserveOwner=True,identicalRetries=True,receipt=receipt,frameHashes=hashes))
 finally:post('/take/stop',dict(takeId=tid))
(out/'result.json').write_text(json.dumps(rows,indent=2)+'\n')
print(json.dumps(dict(passed=True,orders=2,invalidRequests=6,frames=sum(len(x['frameHashes']) for x in rows),evidence=str(out))))
