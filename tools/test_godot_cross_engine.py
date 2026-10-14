"""Stardeck v2: sparse gd measure on Three vs real Godot per-frame poses.
Requires a completed Godot landscape take's /status JSON (retained by the M1 runner).
Camera tolerance 1e-4 world units / basis-vector components; screen centres are descriptive, not pixel equivalence.
"""
import argparse, contextlib, json, math, os, select, signal, subprocess, time
from pathlib import Path
from godot_test_support import CLI, REPO

p=argparse.ArgumentParser(); p.add_argument('--godot-poses',required=True); p.add_argument('--work',required=True)
p.add_argument('--game-root',required=True); p.add_argument('--three-root',default=str(REPO/'packages/gamedirector-three/node_modules/three'))
p.add_argument('--gd',default=str(CLI)); p.add_argument('--measure',help='reuse a previously measured Three directory (comparison only)')
a=p.parse_args(); work=Path(a.work); work.mkdir(parents=True,exist_ok=True)
timeline_path=REPO/'samples/timelines/stardeck/v2.json'; timeline=json.loads(timeline_path.read_text())
shots=sorted((c for c in timeline['cues'] if c['type']=='camera.shot'),key=lambda c:c['t'])
frames=[]; groups=[]
for i,c in enumerate(shots):
    end=shots[i+1]['t'] if i+1<len(shots) else c['t']+c['shot']['durationSeconds']
    first=math.ceil(c['t']*24-1e-7); last=math.ceil(end*24-1e-7)-1
    indices=[first,(first+last)//2,last]; frames.extend(indices); groups.append(dict(shot=i,type=c['shot']['type'],subject=c['shot']['subject'],frames=indices))
frames=sorted(set(frames)); measured=Path(a.measure) if a.measure else work/'three-measure'
if not a.measure:
    with (work/'three-server.log').open('w') as log:
        server=subprocess.Popen(['node',str(REPO/'packages/gamedirector-three/server.mjs'),'--game-root',a.game_root,
          '--adapter',str(REPO/'adapters/stardeck'),'--three-root',a.three_root,'--cli',a.gd,'--browser','chrome','--port','0'],stdout=subprocess.PIPE,stderr=log)
        try:
            data=b''; deadline=time.monotonic()+90
            while time.monotonic()<deadline:
                if select.select([server.stdout],[],[],1)[0]:
                    chunk=os.read(server.stdout.fileno(),4096)
                    if not chunk: raise RuntimeError('Three startup: '+(work/'three-server.log').read_text()[-2000:])
                    data+=chunk
                    try:endpoint=json.loads(data)['endpoint'];break
                    except json.JSONDecodeError:pass
            else:raise TimeoutError('Three readiness')
            q=subprocess.run(['dotnet',a.gd,'measure',str(timeline_path),'--endpoint',endpoint,'--frames',','.join(map(str,frames)),
              '--fps','24','--width','960','--height','540','--out',str(measured)],capture_output=True,text=True,timeout=180)
            (work/'measure.log').write_text(q.stdout+q.stderr); assert q.returncode==0,q.stderr[-2000:]
        finally:
            if server.poll() is None:server.send_signal(signal.SIGTERM)
            try:server.wait(timeout=20)
            except subprocess.TimeoutExpired:server.kill();server.wait()
            server.stdout.close()
        assert server.returncode==0,server.returncode

godot={r['frame']:r for r in json.loads(Path(a.godot_poses).read_text())['poses']}
report=json.loads((measured/'measure.json').read_text()); three={r['frame']:r for r in report['rows']}
assert report['frames']==frames
sub=lambda a,b:[x-y for x,y in zip(a,b)]
norm=lambda v:math.sqrt(sum(x*x for x in v))
rows=[]
for group in groups:
    for frame in group['frames']:
        g=godot[frame]; t=three[frame]; camera=t['camera']; x,y,z,w=g['rotation']; length=math.sqrt(x*x+y*y+z*z+w*w);x,y,z,w=(v/length for v in (x,y,z,w))
        right=[1-2*(y*y+z*z),2*(x*y+z*w),2*(x*z-y*w)]
        up=[2*(x*y-z*w),1-2*(x*x+z*z),2*(y*z+x*w)]
        forward=[-2*(x*z+y*w),-2*(y*z-x*w),-(1-2*(x*x+y*y))]
        position=norm(sub(g['position'],camera['position']))
        basis=max(abs(d) for a0,b0 in ((right,camera['right']),(up,camera['up']),(forward,camera['forward'])) for d in sub(a0,b0))
        subject=group['subject']; gc=g['roleCentres'][subject][:2]; tc=t['composition']['center'][:2]
        delta=max(abs(d) for d in sub(gc,tc))
        secondary=[]
        for role in t['composition'].get('roles',[]):
            if role['role']==subject or 'rect' not in role:continue
            rect=role['rect']; centre=[(rect[0]+rect[2])/2,(rect[1]+rect[3])/2]
            secondary.append(dict(role=role['role'],threeProjectedRectCentre=centre,godotBoundsCentre=g['roleCentres'][role['role']][:2]))
        rows.append(dict(shot=group['shot'],type=group['type'],frame=frame,positionError=position,basisError=basis,
          subject=subject,godotCentre=gc,threeLegacyCentre=tc,centreMaxUvError=delta,secondary=secondary,passed=position<=1e-4 and basis<=1e-4))
result=dict(passed=all(r['passed'] for r in rows),shots=len(groups),frames=len(rows),positionTolerance=1e-4,basisTolerance=1e-4,
            maxPositionError=max(r['positionError'] for r in rows),maxBasisError=max(r['basisError'] for r in rows),
            maxSubjectCentreUvError=max(r['centreMaxUvError'] for r in rows),rows=rows,
            differences='Godot CPU posed bounds vs Three legacy cached bounds; imported skinning, asset-specific animation/fade sampling, lighting, tone mapping, shadows, AA and renderer differ. Secondary Three centres are projected rect midpoints, not world-box centre projections.')
(work/'results.json').write_text(json.dumps(result,indent=2)+'\n')
print(json.dumps({k:v for k,v in result.items() if k not in ('rows','differences')}))
assert result['passed'],'cross-engine camera pose mismatch'
