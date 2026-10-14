#!/usr/bin/env python3
"""Native posed-skin/morph/hidden/empty geometry probes; original mesh stays unchanged."""
import argparse,json,shutil,subprocess
from pathlib import Path
from godot_test_support import REPO

p=argparse.ArgumentParser();p.add_argument('--godot',required=True);p.add_argument('--work',type=Path,required=True);a=p.parse_args()
a.work.mkdir(parents=True);shutil.copytree(REPO/'packages/gamedirector-godot/addons',a.work/'addons')
(a.work/'project.godot').write_text('config_version=5\n[application]\nconfig/name="BoundsProbe"\n')
(a.work/'probe.gd').write_text('''extends SceneTree
const Bounds = preload("res://addons/gamedirector/role_bounds.gd")
func _initialize():
	call_deferred("run")
func run():
	var role := Node3D.new(); root.add_child(role)
	var skeleton := Skeleton3D.new(); role.add_child(skeleton); skeleton.add_bone("joint")
	var arrays := []; arrays.resize(Mesh.ARRAY_MAX)
	arrays[Mesh.ARRAY_VERTEX] = PackedVector3Array([Vector3.ZERO, Vector3.RIGHT, Vector3.UP])
	arrays[Mesh.ARRAY_BONES] = PackedInt32Array([0,0,0,0, 0,0,0,0, 0,0,0,0])
	arrays[Mesh.ARRAY_WEIGHTS] = PackedFloat32Array([1,0,0,0, 1,0,0,0, 1,0,0,0])
	var mesh := ArrayMesh.new(); mesh.blend_shape_mode = Mesh.BLEND_SHAPE_MODE_NORMALIZED; mesh.add_blend_shape("grow")
	var morph := []; morph.resize(Mesh.ARRAY_MAX)
	morph[Mesh.ARRAY_VERTEX] = PackedVector3Array([Vector3.ZERO, Vector3.RIGHT*3, Vector3.UP*3])
	mesh.add_surface_from_arrays(Mesh.PRIMITIVE_TRIANGLES, arrays, [morph])
	var visual := MeshInstance3D.new(); visual.mesh = mesh; skeleton.add_child(visual)
	var skin := Skin.new(); skin.add_bind(0, Transform3D.IDENTITY); visual.skin = skin; visual.skeleton = NodePath("..")
	skeleton.set_bone_pose_position(0, Vector3(2,0,0)); skeleton.force_update_all_bone_transforms()
	visual.set_blend_shape_value(0, 0.5)
	await process_frame # Skin registration is deferred by MeshInstance3D on tree attachment.
	RenderingServer.force_sync()
	var original := mesh.get_aabb()
	var probe := Bounds.new(); probe._visit(role)
	assert(probe.has_geometry and probe.failure.is_empty(), probe.failure)
	assert(probe.box.position.is_equal_approx(Vector3(2,0,0)), str(probe.box))
	assert(probe.box.end.is_equal_approx(Vector3(4,2,0)), "skin AND morph must both be posed: " + str(probe.box))
	assert(mesh.get_aabb() == original, "original native geometry changed")
	assert(mesh.surface_get_arrays(0)[Mesh.ARRAY_VERTEX] == arrays[Mesh.ARRAY_VERTEX], "original vertices changed")
	var hidden := Node3D.new(); role.add_child(hidden); hidden.visible = false
	var big := MeshInstance3D.new(); big.mesh = BoxMesh.new(); big.position = Vector3(100,100,100); hidden.add_child(big)
	var second := Bounds.new(); second._visit(role); assert(second.box == probe.box, "hidden subtree included")
	var empty := Bounds.new(); empty._visit(Node3D.new()); assert(not empty.has_geometry)
	print("BOUNDS_PASS"); quit()
''')
r=subprocess.run([a.godot,'--headless','--path',str(a.work),'--quit-after','4','--script','res://probe.gd'],capture_output=True,text=True,timeout=30)
(a.work/'native.log').write_text(r.stdout+r.stderr)
assert r.returncode==0 and 'BOUNDS_PASS' in r.stdout and 'SCRIPT ERROR:' not in r.stderr,r.stdout+r.stderr
print('PASS: posed skin+morph, hidden subtree, empty, original resource unchanged')
