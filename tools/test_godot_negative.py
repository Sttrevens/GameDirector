"""Real-worker clock refusal after successful in-process Core admission, before take-state mutation."""
import argparse, hashlib, json, shutil, subprocess, urllib.error
from pathlib import Path
from godot_test_support import CLI, REPO, serve

p=argparse.ArgumentParser(); p.add_argument('--godot',required=True); p.add_argument('--work',required=True); p.add_argument('--gd',default=str(CLI))
a=p.parse_args(); work=Path(a.work); work.mkdir(parents=True)
timeline=json.loads((REPO/'samples/timelines/stardeck/v2.json').read_text())
results=[]
for clock in ('GPUParticles3D','AnimationTree','Timer','RigidBody3D','StaticBody3D','AnimationPlayer'):
    root=work/clock; project=root/'project'
    shutil.copytree(REPO/'samples/godot/stardeck',project,ignore=shutil.ignore_patterns('.godot','*.import'))
    stage=project/'stage.tscn'; text=stage.read_text()
    if clock=='AnimationPlayer': text=text.replace('callback_mode_process = 2','callback_mode_process = 1')
    else: text+='\n[node name="UnownedClock" type="'+clock+'" parent="."]\n'
    stage.write_text(text); before=hashlib.sha256(stage.read_bytes()).hexdigest()
    with serve(project,a.godot,root/'logs',a.gd) as bridge:
        manifest=bridge.get('/'+bridge.contract['bridge']['routes']['manifest'])
        assert len(manifest['roles'])==2 and len(manifest['actors'])==2
        prior=bridge.get('/'+bridge.contract['bridge']['routes']['take'])
        try: bridge.post('takeStart',dict(timeline=timeline,frameRate=24,width=960,height=540)); raise AssertionError('unowned clock accepted: '+clock)
        except urllib.error.HTTPError as error:
            body=json.loads(error.read()); status=error.code; assert error.code==bridge.contract['bridge']['errors']['stateConflict']['status'],body
            assert body['code']==bridge.contract['bridge']['errors']['stateConflict']['code'],body
            assert clock in body['error'] and ('UnownedClock' in body['error'] or clock=='AnimationPlayer'),body
        after=bridge.get('/'+bridge.contract['bridge']['routes']['take'])
        assert prior==after and after.get('take') is None,(prior,after)
        assert hashlib.sha256(stage.read_bytes()).hexdigest()==before,'stage mutated'
        results.append(dict(clock=clock,status=status,error=body['error'],receiptUnchanged=True,stageUnchanged=True))
# Stage scripts are rejected before ResourceLoader can execute a static initializer.
root=work/'script'; project=root/'project'
shutil.copytree(REPO/'samples/godot/stardeck',project,ignore=shutil.ignore_patterns('.godot','*.import'))
sentinel=root/'static-init-ran'
(project/'unowned.gd').write_text('extends Node3D\nstatic func _static_init():\n\tFileAccess.open("'+str(sentinel)+'", FileAccess.WRITE).store_string("mutated")\nfunc _process(_dt): pass\n')
stage=project/'stage.tscn'; text=stage.read_text(); text=text.replace('[ext_resource type="PackedScene"', '[ext_resource type="Script" path="res://unowned.gd" id="unowned"]\n\n[ext_resource type="PackedScene"',1)
text=text.replace('[node name="Stardeck" type="Node3D"]','[node name="Stardeck" type="Node3D"]\nscript = ExtResource("unowned")')
stage.write_text(text)
q=subprocess.run(['dotnet',a.gd,'godot','manifest','--project',str(project),'--stage','res://stage.tscn','--godot',a.godot],capture_output=True,text=True,timeout=75)
assert q.returncode==1 and 'script resource is not allowed' in q.stderr,q.stderr[-2000:]
assert not sentinel.exists(),'static initializer executed before refusal'
results.append(dict(clock='stage script/static initializer',refusedBeforeLoad=True))
(work/'results.json').write_text(json.dumps(results,indent=2)+'\n')
print(json.dumps(dict(passed=True,clockRefusals=len(results),results=results)))
