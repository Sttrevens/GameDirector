#!/usr/bin/env python3
"""Role-bounds-v1 samples via real rendered worker (not camera evaluator doubles)."""
import argparse, json, shutil, subprocess
from pathlib import Path
from godot_test_support import CLI, fixture, serve

p=argparse.ArgumentParser(description=__doc__); p.add_argument('--godot',required=True); p.add_argument('--work',type=Path,required=True)
p.add_argument('--addon',type=Path,help='Test-only mutated addon copy')
a=p.parse_args(); a.work.mkdir(parents=True,exist_ok=True)
c=json.loads(subprocess.check_output(['dotnet',str(CLI),'contract','--json']))
results=[]
for i,row in enumerate(c['review']['roles']['samples']):
    work=a.work/f'row-{i}';work.mkdir()
    camera=dict(c['camera']);camera['scene']=dict(camera['scene'])
    camera['scene'].update(subjectSize=[hi-lo for lo,hi in zip(row['min'],row['max'])],
                           subjectCenter=[(hi+lo)/2 for lo,hi in zip(row['min'],row['max'])],**{'from':row['from']})
    project=fixture(work,camera)
    if a.addon: shutil.copytree(a.addon,project/'addons/gamedirector',dirs_exist_ok=True)
    width,height=(72,128) if row['aspect']<1 else (128,72) if row['aspect']>1 else (128,128)
    shot=dict(type='lockoff',subject='hero',frame='full',**{'from':'anchor'},durationSeconds=1,ease=c['camera']['eases'][0]['id'],
              fov=row['fov'],params=dict(targetHeight=row['aim'][1],pan=row['pan'],tilt=row['tilt'],roll=row['roll']))
    with serve(project,a.godot,work/'logs') as bridge:
        snap=bridge.capture(dict(version='0.1',id='role-sample',cues=[dict(t=0,type='camera.shot',shot=shot)]),2,width,height)
    roles=snap['reviews'][0]['composition'].get('roles',[])
    assert len(roles)==1,'composition must contain one subject role'
    role=roles[0];assert role['role']=='hero' and role['kind']==c['review']['roles']['kinds'][0],role
    assert role['state']==row['state'],(role,row)
    for key in ('rect','crown'):
        if row.get(key) is None:assert key not in role or role[key] is None,(key,role)
        else:assert max(abs(x-y) for x,y in zip(role[key],row[key]))<=row['tolerance'],(key,role,row)
    results.append(dict(index=i,state=role['state'],kind=role['kind'],rect=role.get('rect'),crown=role.get('crown'),passed=True))
    (a.work/'results.json').write_text(json.dumps(results,indent=2))
print(f'PASS: {len(results)} role projection rows, crown/rect/state/kind')
