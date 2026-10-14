extends RefCounted
const RoleBounds = preload("role_bounds.gd")
var camera: Camera3D
var review_graph: Dictionary
var graph: Dictionary
var roles: Dictionary
var locations: Dictionary
var defaults := {}
var eases := {}
var motions := {}
var coverage := {}
var shot := {}
var ease_kernels := {"identity": func(p): return p, "quad-in": func(p): return p * p,
	"quad-out": func(p): return 1 - (1 - p) * (1 - p), "smoothstep": func(p): return p * p * (3 - 2 * p)}
var motion_kernels := {"hold": _hold, "lerp": _lerp, "track-offset": _track, "orbit-up": _orbit}

func setup(parent: Node, contract: Dictionary, bound_roles: Dictionary, bound_locations: Dictionary) -> String:
	graph = contract.camera; review_graph = contract.review.roles; roles = bound_roles; locations = bound_locations
	for row in graph.params:
		if row.has("default"): defaults[row.name] = row.default
	for row in graph.eases:
		if not ease_kernels.has(row.formula): return "unimplemented ease formula: " + str(row.formula)
		eases[row.id] = row.formula
	for row in graph.shots:
		if not motion_kernels.has(row.motion): return "unimplemented motion formula: " + str(row.motion)
		motions[row.id] = row.motion
	for row in graph.frames: coverage[row.id] = row.coverage
	camera = Camera3D.new(); camera.keep_aspect = Camera3D.KEEP_HEIGHT; camera.physics_interpolation_mode = Node.PHYSICS_INTERPOLATION_MODE_OFF
	camera.fov = graph.lens.defaultFov; camera.near = graph.rig.nearClip; camera.far = graph.rig.farClip
	parent.add_child(camera); camera.position = Vector3(0, 4, 10) # engine-local: Three's initial presentation pose, not a camera default.
	camera.current = true
	return ""

func parameter(params: Dictionary, name: String) -> float:
	return float(params[name] if params.has(name) else defaults[name])

func bounds(root: Node3D) -> AABB:
	var result := AABB(root.global_position, Vector3.ZERO)
	var first := true
	for mesh in root.find_children("*", "MeshInstance3D", true, false):
		if not mesh.is_visible_in_tree() or mesh.mesh == null: continue
		var geometry: Mesh = mesh.mesh
		if not mesh.skeleton.is_empty() and mesh.get_node_or_null(mesh.skeleton) is Skeleton3D:
			var baked: ArrayMesh = mesh.bake_mesh_from_current_skeleton_pose()
			if baked != null: geometry = baked
		var box: AABB = mesh.global_transform * geometry.get_aabb()
		result = box if first else result.merge(box); first = false
	return result

func target(id: String) -> Vector3:
	return bounds(roles[id]).get_center()

func start(spec: Dictionary, time: float):
	var params: Dictionary = spec.get("params", {})
	var role: Node3D = roles[spec.subject]
	var box := bounds(role)
	var offset := box.get_center() - role.global_position
	if params.has("targetHeight"): offset = Vector3(0, params.targetHeight, 0)
	var aim := role.global_position + offset
	camera.fov = spec.get("fov", rad_to_deg(2 * atan(graph.lens.sensorSizeMm / (2 * spec.focalLengthMm))) if spec.has("focalLengthMm") else graph.lens.defaultFov)
	var from: Vector3 = camera.position if spec.from == graph.currentAnchor else locations[spec.from].global_position
	if spec.from == graph.currentAnchor and parameter(params, "distance") <= 0:
		var fill: float = params.get("coverage", coverage.get(spec.frame, coverage[graph.fallbackFrame]))
		var distance: float = maxf(graph.rig.minSubjectHeight, box.size.y) / (2 * tan(deg_to_rad(camera.fov) / 2) * fill)
		var back := from - aim
		if back.length_squared() < graph.rig.degenerateSqrMagnitude: back = Vector3(0, graph.rig.autoFrameFallback.up, graph.rig.autoFrameFallback.back)
		from = aim + back.normalized() * distance
	from += Vector3(parameter(params, "offsetX"), parameter(params, "offsetY"), parameter(params, "offsetZ"))
	if parameter(params, "distance") > 0:
		var back := from - aim
		if back.length_squared() < graph.rig.degenerateSqrMagnitude: back = Vector3(0, graph.rig.distanceFallback.up, graph.rig.distanceFallback.back)
		from = aim + back.normalized() * parameter(params, "distance")
	var to := from
	if spec.has("to"): to = camera.position if spec.to == graph.currentAnchor else locations[spec.to].global_position
	to += Vector3(parameter(params, "toOffsetX"), parameter(params, "toOffsetY"), parameter(params, "toOffsetZ"))
	shot = {"spec": spec, "params": params, "from": from, "to": to, "offset": offset, "relative": from - aim, "start": time, "fov": camera.fov,
		"ease": eases[spec.ease], "motion": motions[spec.type]}

func _hold(_aim: Vector3, _e: float) -> Vector3: return shot.from
func _lerp(_aim: Vector3, e: float) -> Vector3: return shot.from.lerp(shot.to, e)
func _track(aim: Vector3, _e: float) -> Vector3: return aim + shot.relative
func _orbit(aim: Vector3, e: float) -> Vector3: return aim + shot.relative.rotated(Vector3.UP, -deg_to_rad(parameter(shot.params, "orbitDeg")) * e)

func evaluate(time: float):
	if shot.is_empty(): return
	var spec: Dictionary = shot.spec
	var p: float = clampf((time - shot.start) / spec.durationSeconds, 0.0, 1.0) if spec.durationSeconds > 0 else 0.0
	var e: float = ease_kernels[shot.ease].call(p)
	var aim: Vector3 = roles[spec.subject].global_position + shot.offset
	var position: Vector3 = motion_kernels[shot.motion].call(aim, e)
	if spec.has("lookAt"): aim = target(spec.lookAt)
	if shot.params.has("endFov"): camera.fov = lerpf(shot.fov, shot.params.endFov, e)
	camera.transform = Transform3D(Basis.IDENTITY, position)
	if (aim - position).length_squared() >= graph.rig.aimEpsilonSqrMagnitude: camera.look_at(aim, Vector3.UP)
	camera.rotate_object_local(Vector3.UP, deg_to_rad(-parameter(shot.params, "pan")))
	camera.rotate_object_local(Vector3.RIGHT, deg_to_rad(-parameter(shot.params, "tilt")))
	camera.rotate_object_local(Vector3.BACK, deg_to_rad(parameter(shot.params, "roll")))

func project(point: Vector3) -> Array:
	var pixel := camera.unproject_position(point)
	var size := camera.get_viewport().get_visible_rect().size
	return [pixel.x / size.x, 1 - pixel.y / size.y, (point - camera.global_position).dot(-camera.global_basis.z)]

func composition() -> Dictionary:
	if shot.is_empty(): return {"active": false}
	var entries: Array = []; var seen: Array = []
	var spec: Dictionary = shot.spec
	_append_role(entries, seen, spec.subject, review_graph.kinds[0])
	for id in spec.get("with", []): _append_role(entries, seen, id, review_graph.kinds[1])
	if spec.has("lookAt"): _append_role(entries, seen, spec.lookAt, review_graph.kinds[2])
	return {"active": true, "subject": spec.subject, "center": project(target(spec.subject)), "roles": entries}

func _append_role(entries: Array, seen: Array, id: String, kind: String):
	if seen.has(id): return
	seen.append(id)
	var entry: Dictionary = RoleBounds.new().composition(roles.get(id), camera, review_graph)
	entry.role = id; entry.kind = kind; entries.append(entry)

func measure(index: int, time: float, composition: Dictionary) -> Dictionary:
	# Only called after explicit viewport readback, before runtime.advance_to. No pose update.
	var world := camera.global_transform
	var size := camera.get_viewport().get_visible_rect().size
	return {"frame": index, "time": time, "camera": {"position": _array(world.origin),
		"right": _array(world.basis.x.normalized()), "up": _array(world.basis.y.normalized()),
		"forward": _array(-world.basis.z.normalized()), "fov": camera.fov, "aspect": size.x / size.y}, "composition": composition}

func pose(index: int, time: float) -> Dictionary:
	var q := camera.quaternion
	var centres := {}
	for id in roles:
		if roles[id].visible: centres[id] = project(target(id))
	var aim: Vector3 = roles[shot.spec.subject].global_position + shot.offset if not shot.is_empty() else Vector3.ZERO
	return {"frame": index, "time": time, "position": _array(camera.position), "rotation": [q.x, q.y, q.z, q.w], "fov": camera.fov,
		"right": _array(camera.basis.x), "target": _array(aim), "roleCentres": centres}

func _array(v: Vector3) -> Array: return [v.x, v.y, v.z]
