"""ALL camera sample rows through the real .NET/HTTP/GDScript worker path.
Creates an isolated Core graph.scene fixture. Normal take frames (not an evaluator double).
Usage: python3 tools/test_godot_conformance.py --godot <binary> --work <fresh /tmp/dir>
"""
import argparse, collections, json, math, subprocess
from pathlib import Path
from godot_test_support import Bridge, CLI, fixture, serve

p = argparse.ArgumentParser(); p.add_argument('--godot', required=True); p.add_argument('--work', required=True)
p.add_argument('--gd', default=str(CLI)); a = p.parse_args(); work = Path(a.work); work.mkdir(parents=True)
contract = json.loads(subprocess.check_output(['dotnet', a.gd, 'contract', '--json']))
camera = contract['camera']; scene = camera['scene']
by_motion = {m['id']: m for m in camera['motions']}
shot_for = lambda motion: next(s['id'] for s in camera['shots'] if s['motion'] == motion)
hold, lerp = shot_for('hold'), shot_for('lerp')
full = next(f['id'] for f in camera['frames'] if f['id'] == 'full')

def shot(type=hold, **kw):
    s = dict(type=type, subject='hero', frame=full, **{'from': 'anchor'}, durationSeconds=scene['durationSeconds'], ease=camera['eases'][0]['id'])
    if next(r for r in camera['shots'] if r['id'] == type)['requiresTarget']: s['to'] = 'destination'
    s.update(kw); return s

cases = []
for ease in camera['eases']:
    for row in ease['samples']: cases.append(dict(set='camera.eases.samples', row=row, p=row['p'], shot=shot(lerp, ease=ease['id']), observable='fraction', expected=row['e'], tolerance=0))
for spec in camera['shots']:
    for row in by_motion[spec['motion']]['samples']:
        s = shot(spec['id'], ease=row['ease'], params=row.get('params', {}))
        if row['authoredTo']: s['to'] = 'destination'
        else: s.pop('to', None)
        cases.append(dict(set='camera.motions.samples/' + spec['id'], row=row, p=row['p'], shot=s, **{k:row[k] for k in ('observable','expected','tolerance')}))
for name, rows in (('camera.aim.samples', camera['aim']['samples']), ('camera.lens.aspect.samples', camera['lens']['aspect']['samples'])):
    for row in rows: cases.append(dict(set=name, row=row, p=0, aspect=row['aspect'], shot=shot(fov=row['fov'], params={k:row[k] for k in ('pan','tilt','roll')}), observable='viewport', expected=[row['x'],row['y']], tolerance=row['tolerance']))
for row in camera['lens']['samples']: cases.append(dict(set='camera.lens.samples', row=row, p=0, shot=shot(focalLengthMm=row['focalLengthMm']), observable='fov', expected=row['fov'], tolerance=row['tolerance']))
for row in camera['rig']['samples']:
    s = shot(frame=row['frame'], **{'from': camera['currentAnchor']}, params={'coverage':row['coverage']} if 'coverage' in row else {})
    cases.append(dict(set='camera.rig.samples', row=row, p=0, shot=s, observable='radius3d', expected=row['radius3D'], tolerance=row['tolerance']))

sub = lambda a,b: [x-y for x,y in zip(a,b)]
dot = lambda a,b: sum(x*y for x,y in zip(a,b))
norm = lambda a: math.sqrt(dot(a,a))
results=[]
project = fixture(work, camera)
with serve(project, a.godot, work / 'logs', a.gd) as bridge:
    groups = collections.defaultdict(list)
    for c in cases: groups[c.get('aspect',scene['aspect'])].append(c)
    for aspect, group in groups.items():
        fps=4; cues=[]
        # Reset motion by a zero-distance owned move at each case boundary.
        for i,c in enumerate(group):
            start=i*3*scene['durationSeconds']; cue_time=start+scene['durationSeconds']
            cues += [dict(t=start,type='actor.move',role='hero',to='origin',speed=100), dict(t=start,type='camera.shot',shot=shot()),
                     dict(t=cue_time,type='camera.shot',shot=c['shot'])]
            if c['row'].get('stepSubject'): cues.append(dict(t=cue_time+scene['durationSeconds']/4,type='actor.move',role='hero',to='step',speed=4))
            c['frame']=round((cue_time+c['p']*scene['durationSeconds'])*fps); c['startFrame']=round(cue_time*fps)
        if aspect == scene['aspect']: width=height=128
        elif aspect < 1: width,height=72,128
        else: width,height=128,72
        snapshot=bridge.capture(dict(version='0.1',id='godot-conformance',cues=cues),fps,width,height)
        poses={r['frame']:r for r in snapshot['poses']}
        for c in group:
            pose=poses[c['frame']]; pos=pose['position']; target=pose['target']; initial=poses[c['startFrame']]
            a0=sub(scene['from'],scene['subjectCenter']); b=sub(pos,target); horizontal=lambda v:[v[0],0,v[2]]
            observable=c['observable']
            if observable=='viewport': actual=pose['roleCentres']['hero'][:2]; assert pose['roleCentres']['hero'][2]>0
            elif observable=='fov': actual=pose['fov']
            elif observable=='displacement': actual=norm(sub(pos,scene['from']))
            elif observable=='fraction': actual=norm(sub(pos,scene['from']))/norm(sub(scene['to'],scene['from']))
            elif observable=='radius': actual=norm(horizontal(b))
            elif observable=='radius3d': actual=norm(b)
            elif observable=='swept-deg': actual=math.degrees(math.acos(max(-1,min(1,dot(horizontal(a0),horizontal(b))/(norm(horizontal(a0))*norm(horizontal(b)))))))
            elif observable=='side':
                x=dot(sub(pos,initial['position']),initial['right']); actual=1 if x>0 else -1 if x<0 else 0
            elif observable=='drift': actual=norm(sub(b,a0))
            else: raise AssertionError('uncovered observable '+observable)
            pairs=zip(actual,c['expected']) if isinstance(actual,list) else [(actual,c['expected'])]
            delta=max(abs(x-y) for x,y in pairs); passed=delta<=c['tolerance']
            results.append(dict(set=c['set'],frame=c['frame'],expected=c['expected'],actual=actual,delta=delta,tolerance=c['tolerance'],passed=passed))
report=dict(passed=all(r['passed'] for r in results), counts=dict(collections.Counter(r['set'] for r in results)), rows=results)
(work/'results.json').write_text(json.dumps(report,indent=2)+'\n')
print(json.dumps({k:v for k,v in report.items() if k!='rows'}))
for r in results:
    if not r['passed']: print(json.dumps(r))
assert report['passed'], 'real-worker camera conformance failed; see '+str(work/'results.json')
