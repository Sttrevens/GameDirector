extends RefCounted
# Read-only role-bounds-v1 for the owned visual allow-list (MeshInstance3D).
# Lights/markers/skeletons are passive containers; unsupported visual classes cannot
# enter a take. Never write an original mesh/AABB, transform, bone or blend value.
# Protocol list indices, not independently declared state strings or policy numbers.
enum State { IN_FRONT, BEHIND, ABSENT, EMPTY, STRADDLES_NEAR, ERROR }
var vocabulary: Dictionary
var box := AABB()
var has_geometry := false
var failure := ""

func composition(root: Node3D, camera: Camera3D, spec: Dictionary) -> Dictionary:
	vocabulary = spec
	var states: Array = vocabulary.states
	if root == null or not root.is_visible_in_tree(): return {"state": states[State.ABSENT]}
	box = AABB(); has_geometry = false; failure = ""
	_visit(root)
	if not failure.is_empty(): return {"state": states[State.ERROR], "message": failure}
	if not has_geometry: return {"state": states[State.EMPTY]}
	var corners: Array = []; var beyond := true; var behind := true
	for i in range(8): # engine-local: AABB has eight corners.
		var p := _project(box.get_endpoint(i), camera)
		if not _finite(p): return {"state": states[State.ERROR], "message": str(root.get_path()) + ": non-finite bounds projection"}
		corners.append(p); beyond = beyond and p[2] > camera.near; behind = behind and p[2] <= 0
	var top := box.get_center(); top.y = box.end.y
	var result := {"state": states[State.BEHIND] if behind else states[State.IN_FRONT] if beyond else states[State.STRADDLES_NEAR], "crown": _project(top, camera)}
	if beyond:
		var x0 := INF; var y0 := INF; var x1 := -INF; var y1 := -INF
		for p in corners:
			x0 = minf(x0, p[0]); y0 = minf(y0, p[1]); x1 = maxf(x1, p[0]); y1 = maxf(y1, p[1])
		result.rect = [x0, y0, x1, y1]
	return result

func _visit(node: Node):
	if node is Node3D and not node.is_visible_in_tree(): return
	if node is MeshInstance3D and node.mesh != null:
		var mesh: Mesh = node.mesh
		var local := AABB(); var found := false
		for surface in range(mesh.get_surface_count()):
			var arrays: Array = mesh.surface_get_arrays(surface)
			if arrays.is_empty() or arrays[Mesh.ARRAY_VERTEX] == null: continue
			var vertices: PackedVector3Array = arrays[Mesh.ARRAY_VERTEX]
			var blends: Array = mesh.surface_get_blend_shape_arrays(surface) if mesh is ArrayMesh else []
			var skeleton: Skeleton3D = node.get_node_or_null(node.skeleton) as Skeleton3D
			for i in range(vertices.size()):
				var vertex: Vector3 = vertices[i]
				if not blends.is_empty():
					var weight := 0.0; var mixed := Vector3.ZERO
					for blend in range(blends.size()):
						var value: float = node.get_blend_shape_value(blend)
						weight += value; mixed += blends[blend][Mesh.ARRAY_VERTEX][i] * value
					vertex = vertex * (1 - weight) + mixed if mesh.blend_shape_mode == Mesh.BLEND_SHAPE_MODE_NORMALIZED else vertex + mixed
				if node.skin != null and skeleton != null and arrays[Mesh.ARRAY_BONES] != null:
					var bones: PackedInt32Array = arrays[Mesh.ARRAY_BONES]; var weights: PackedFloat32Array = arrays[Mesh.ARRAY_WEIGHTS]
					var count := weights.size() / vertices.size(); var posed := Vector3.ZERO
					for influence in range(int(count)):
						var index := i * int(count) + influence; var weight: float = weights[index]
						if weight == 0: continue
						var bind: int = bones[index]
						if bind < 0 or bind >= node.skin.get_bind_count(): failure = str(node.get_path()) + ": invalid skin bind"; return
						var bone: int = node.skin.get_bind_bone(bind)
						if bone < 0: bone = skeleton.find_bone(node.skin.get_bind_name(bind))
						if bone < 0 or bone >= skeleton.get_bone_count(): failure = str(node.get_path()) + ": missing skin bone"; return
						posed += (skeleton.get_bone_global_pose(bone) * node.skin.get_bind_pose(bind) * vertex) * weight
					vertex = posed
				if not vertex.is_finite(): failure = str(node.get_path()) + ": non-finite posed vertex"; return
				local = local.expand(vertex) if found else AABB(vertex, Vector3.ZERO); found = true
		if found:
			var world: AABB = node.global_transform * local
			box = box.merge(world) if has_geometry else world; has_geometry = true
	for child in node.get_children(): _visit(child)

func _project(point: Vector3, camera: Camera3D) -> Array:
	var pixel := camera.unproject_position(point)
	var size := camera.get_viewport().get_visible_rect().size
	return [pixel.x / size.x, 1 - pixel.y / size.y, (point - camera.global_position).dot(-camera.global_basis.z)]

func _finite(values: Array) -> bool:
	for value in values:
		if not is_finite(value): return false
	return true
