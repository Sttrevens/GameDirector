extends RefCounted
const Manifest = preload("manifest.gd")
const Camera = preload("camera.gd")
const Sampler = preload("animation.gd")
const Ownership = preload("ownership.gd")
var viewport: SubViewport
var root: Node3D
var bindings: Dictionary
var graph: Dictionary
var manifest: RefCounted
var rig: RefCounted
var compiled: Dictionary
var time := 0.0
var world_time := 0.0
var scale := 1.0
var index := 0
var moves := {}
var poses := {}
var samplers := {}
var events: Array = []
var dispatch := {}

func begin(parent: Node, packed: PackedScene, contract: Dictionary, names: Dictionary, timeline: Dictionary, width: int, height: int) -> String:
	var audit := Ownership.new()
	var why: String = audit.inspect_source(packed)
	if not why.is_empty(): return why
	var stage: Node = packed.instantiate()
	why = audit.inspect_nodes(stage)
	if not why.is_empty(): stage.free(); return why
	var inspection := Manifest.new()
	inspection.inspect(stage, contract, names)
	if not inspection.error.is_empty(): stage.free(); return inspection.error
	for cue in timeline.orderedCues:
		if cue.type == names.cues.actorAnim and not inspection.players[cue.role].has(cue.clip):
			stage.free(); return "role " + str(cue.role) + ": unbound clip " + str(cue.clip)
	end()
	graph = contract; bindings = names; compiled = timeline; manifest = inspection; root = stage
	viewport = SubViewport.new(); viewport.size = Vector2i(width, height); viewport.own_world_3d = true
	viewport.render_target_update_mode = SubViewport.UPDATE_DISABLED
	viewport.msaa_3d = Viewport.MSAA_DISABLED; viewport.screen_space_aa = Viewport.SCREEN_SPACE_AA_DISABLED; viewport.use_taa = false
	parent.add_child(viewport); root.process_mode = Node.PROCESS_MODE_DISABLED; root.physics_interpolation_mode = Node.PHYSICS_INTERPOLATION_MODE_OFF; viewport.add_child(root)
	for light in root.find_children("*", "Light3D", true, false): light.shadow_enabled = false
	for environment in root.find_children("*", "WorldEnvironment", true, false):
		var env: Environment = environment.environment.duplicate()
		env.glow_enabled = false; env.ssao_enabled = false; env.ssil_enabled = false; env.ssr_enabled = false; env.sdfgi_enabled = false; env.volumetric_fog_enabled = false
		env.tonemap_mode = Environment.TONE_MAPPER_LINEAR; env.tonemap_exposure = 1.0
		environment.environment = env
	time = 0.0; world_time = 0.0; scale = 1.0; index = 0; moves.clear(); poses.clear(); samplers.clear(); events.clear()
	for role in inspection.roles:
		inspection.roles[role].visible = _present(role)
		var sampler := Sampler.new(); sampler.setup(inspection.roles[role], inspection.players[role]); samplers[role] = sampler
	rig = Camera.new(); why = rig.setup(viewport, contract, inspection.roles, inspection.locations)
	if not why.is_empty(): end(); return why
	var c: Dictionary = bindings.cues
	dispatch = {c.cameraShot: _shot, c.actorSpawn: _spawn, c.actorDespawn: _despawn, c.actorAnim: _anim, c.actorMove: _move,
		c.actorFace: _face, c.worldTimescale: _timescale, c.marker: _marker}
	advance_to(0.0)
	# Cold-readiness barrier: settle actual output and pipeline compilation counters, never assume N draws.
	var deadline: int = Time.get_ticks_msec() + int(graph.bridge.budgets.stageReadySeconds * 1000) # engine-local: native monotonic ticks are milliseconds.
	var previous := PackedByteArray(); var stable := false
	while Time.get_ticks_msec() < deadline:
		var before := _pipelines(); var png := draw(); var after := _pipelines()
		if png == previous and before == after: stable = true; break
		previous = png
	if not stable: end(); return "stage rendering did not reach stable cold readiness"
	return ""

func _pipelines() -> int:
	var count := 0
	for name in ClassDB.class_get_integer_constant_list("RenderingServer"):
		if str(name).begins_with("RENDERING_INFO_PIPELINE_COMPILATIONS_"):
			count += RenderingServer.get_rendering_info(ClassDB.class_get_integer_constant("RenderingServer", name))
	return count

func _present(id: String) -> bool:
	return manifest.presence[id]

func _marker(_cue: Dictionary): pass
func _timescale(cue: Dictionary): scale = cue.scale
func _anim(cue: Dictionary): poses[cue.role] = {"clip": cue.clip, "start": world_time, "fade": cue.fade}
func _move(cue: Dictionary): moves[cue.role] = {"to": manifest.locations[cue.to].global_position, "speed": cue.speed, "heading": cue.get("headingTo", "")}
func _face(cue: Dictionary): _heading(manifest.roles[cue.role], cue.headingTo)
func _shot(cue: Dictionary): rig.start(cue.shot, time)
func _spawn(cue: Dictionary):
	var role: Node3D = manifest.roles[cue.role]
	role.visible = true; role.global_position = manifest.locations[cue.location].global_position
	if cue.has("headingTo"): _heading(role, cue.headingTo)
func _despawn(cue: Dictionary):
	manifest.roles[cue.role].visible = false; moves.erase(cue.role)
	if not rig.shot.is_empty() and (rig.shot.spec.subject == cue.role or rig.shot.spec.get("lookAt") == cue.role): rig.shot.clear()
func _heading(role: Node3D, id: String):
	var point: Vector3 = manifest.roles[id].global_position if manifest.roles.has(id) else manifest.locations[id].global_position
	point.y = role.global_position.y
	if (point - role.global_position).length_squared() >= graph.camera.rig.aimEpsilonSqrMagnitude: role.look_at(point, Vector3.UP, true)

func evaluate(dt: float, with_camera := true):
	world_time += dt * scale
	var moving := {}
	for id in moves.keys():
		var move: Dictionary = moves[id]; var role: Node3D = manifest.roles[id]
		var delta: Vector3 = move.to - role.global_position; var distance := delta.length(); var step: float = move.speed * dt * scale
		moving[id] = step > 0
		var point: Vector3 = move.to; point.y = role.global_position.y
		if (point - role.global_position).length_squared() >= graph.camera.rig.aimEpsilonSqrMagnitude: role.look_at(point, Vector3.UP, true)
		if step >= distance:
			role.global_position = move.to; moves.erase(id)
			if not move.heading.is_empty(): _heading(role, move.heading)
		elif distance > 0: role.global_position += delta * (step / distance)
	for id in samplers:
		if manifest.roles[id].visible: samplers[id].evaluate(poses.get(id, {}), world_time, moving.get(id, false) or moves.has(id))
	if with_camera: rig.evaluate(time)
	RenderingServer.force_sync()

func advance_to(target: float):
	while index < compiled.orderedCues.size() and compiled.orderedCues[index].t <= target + graph.frameClock.cueEpsilonSeconds:
		var cue: Dictionary = compiled.orderedCues[index]; index += 1
		var boundary: float = minf(target, maxf(time, cue.t)); var dt := boundary - time; time = boundary
		if dt > 0: evaluate(dt)
		events.append({"t": cue.t, "type": cue.type, "role": cue.get("role"), "label": cue.get("label")})
		dispatch[cue.type].call(cue); evaluate(0.0, false)
	var dt := target - time; time = target; evaluate(maxf(0.0, dt))

func draw(settle := false) -> PackedByteArray:
	var png := _draw_once()
	if not settle: return png
	# Replay never draws. Its first rendered pose can have deferred native upload/pipeline
	# work; settle that exact pose without advancing any owned clock or replaying a cue.
	var deadline: int = Time.get_ticks_msec() + int(graph.bridge.budgets.engineWaitSeconds * 1000) # engine-local: monotonic ticks are milliseconds.
	while Time.get_ticks_msec() < deadline:
		var before := _pipelines(); var again := _draw_once(); var after := _pipelines()
		if again == png and before == after: return again
		png = again
	push_error("rendered replay pose did not settle within the Core engine wait budget")
	return PackedByteArray()

func _draw_once() -> PackedByteArray:
	viewport.render_target_update_mode = SubViewport.UPDATE_ONCE
	RenderingServer.force_draw(false, 0.0)
	return viewport.get_texture().get_image().save_png_to_buffer()

func end():
	if viewport != null: viewport.free(); viewport = null
