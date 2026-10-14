#!/usr/bin/env python3
"""Native gd measure must reproduce after-render M1 hook poses, never the advanced camera."""
import argparse, json, math, subprocess
from pathlib import Path
from godot_test_support import CLI, REPO, serve

p=argparse.ArgumentParser(description=__doc__);p.add_argument('--godot',required=True);p.add_argument('--work',type=Path,required=True)
p.add_argument('--poses',required=True,type=Path);p.add_argument('--project',type=Path,default=REPO/'samples/godot/stardeck');a=p.parse_args()
a.work.mkdir(parents=True,exist_ok=True)
poses={r['frame']:r for r in json.loads(a.poses.read_text())['poses']}
frames=[0,24,48,72,100,136,137,191,192,240,287,383]
with serve(a.project,a.godot,a.work/'supervisor') as bridge:
    run=subprocess.run(['dotnet',str(CLI),'measure',str(REPO/'samples/timelines/stardeck/v2.json'),
        '--endpoint',bridge.endpoint,'--frames',','.join(map(str,frames)),'--out',str(a.work/'measure'),
        '--fps','24','--width','960','--height','540'],capture_output=True,text=True,timeout=180)
    (a.work/'measure.log').write_text(run.stdout+run.stderr)
    assert run.returncode==0,run.stdout+run.stderr
    snapshot=bridge.get('/take');(a.work/'status.json').write_text(json.dumps(snapshot,indent=2))
report=json.loads((a.work/'measure/measure.json').read_text());assert report['frames']==frames
results=[]
for row in report['rows']:
    expected=poses[row['frame']];camera=row['camera']
    pos=max(abs(x-y) for x,y in zip(camera['position'],expected['position']))
    right=max(abs(x-y) for x,y in zip(camera['right'],expected['right']))
    assert pos<=1e-6 and right<=1e-6,(row['frame'],pos,right)
    assert abs(row['time']-expected['time'])<1e-9 and abs(camera['fov']-expected['fov'])<1e-6
    assert abs(camera['aspect']-960/540)<1e-6
    # A wrong role kind is a wire error even when its geometry remains plausible.
    if row['composition'].get('active'):
        roles=row['composition']['roles'];assert roles[0]['kind']=='subject' and roles[0]['role']==row['composition']['subject']
    results.append(dict(frame=row['frame'],positionError=pos,rightError=right,passed=True))
assert len(snapshot['measures'])==len(frames),snapshot
(a.work/'results.json').write_text(json.dumps(results,indent=2));print(f'PASS: {len(results)} measured poses match M1 hooks and valid role kinds')
