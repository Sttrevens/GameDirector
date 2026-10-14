#!/usr/bin/env python3
"""Kill three inspection producer mutations in disposable copies; source never modified."""
import argparse, hashlib, json, shutil, subprocess, sys
from pathlib import Path
from godot_test_support import REPO

p=argparse.ArgumentParser(description=__doc__);p.add_argument('--godot',required=True);p.add_argument('--work',type=Path,required=True);p.add_argument('--poses',required=True);a=p.parse_args()
a.work.mkdir(parents=True,exist_ok=True);source=REPO/'packages/gamedirector-godot/addons/gamedirector';before={p.name:p.read_bytes() for p in source.iterdir() if p.is_file()};results=[]
for name,file,old,new in [
 ('crown','role_bounds.gd','top.y = box.end.y','top.y = box.position.y'),
 ('kind','camera.gd','spec.subject, review_graph.kinds[0]','spec.subject, review_graph.kinds[1]'),
 ('after-advance','worker.gd','runtime.advance_to(minf(runtime.compiled.duration, message.nextTime))',
  'runtime.advance_to(minf(runtime.compiled.duration, message.nextTime))\n\t\tif reply.has("measure"): reply.measure = runtime.rig.measure(next_frame, runtime.time, reply.measure.composition)'),
]:
    work=a.work/name;work.mkdir();addon=work/'addon';shutil.copytree(source,addon)
    path=addon/file;text=path.read_text();assert text.count(old)==1,(name,old);path.write_text(text.replace(old,new))
    if name=='after-advance':
        project=work/'project';shutil.copytree(REPO/'samples/godot/stardeck',project,ignore=shutil.ignore_patterns('.godot','*.import'))
        shutil.copytree(addon,project/'addons/gamedirector',dirs_exist_ok=True)
        cmd=[sys.executable,str(REPO/'tools/test_godot_measure.py'),'--project',str(project),'--poses',a.poses]
    else:cmd=[sys.executable,str(REPO/'tools/test_godot_review.py'),'--addon',str(addon)]
    run=subprocess.run(cmd+['--godot',a.godot,'--work',str(work/'test')],text=True,capture_output=True,timeout=100)
    (work/'mutation.log').write_text(run.stdout+run.stderr)
    assert run.returncode!=0 and 'AssertionError' in run.stderr,'mutation survived or failed for an unrelated reason: '+run.stderr
    results.append(dict(mutation=name,killed=True,exit=run.returncode))
    # Restore and byte-check the disposable mutant too.
    path.write_bytes(before[file]);assert path.read_bytes()==before[file]
assert all((source/name).read_bytes()==data for name,data in before.items())
(a.work/'results.json').write_text(json.dumps(results,indent=2));print(json.dumps(results));print('PASS: all killed, restored; canonical producer sources unchanged')
