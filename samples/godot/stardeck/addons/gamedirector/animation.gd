extends RefCounted
# Absolute AnimationPlayer sampling; all crossfade time belongs to the world clock.
var root: Node3D
var players: Dictionary
var base: Array
var layers: Array = []
var nodes: Array[Node3D] = []
var skeletons: Array[Skeleton3D] = []
var morphs: Array = []

func setup(role: Node3D, clip_players: Dictionary):
	root = role; players = clip_players
	for node in role.find_children("*", "Node3D", true, false):
		if node is Skeleton3D: skeletons.append(node)
		elif not node is BoneAttachment3D: nodes.append(node)
		if node is MeshInstance3D and node.mesh is ArrayMesh:
			for i in range(node.mesh.get_blend_shape_count()): morphs.append([node, i])
	base = _capture()
	var rest: String = role.get_meta("gamedirector_rest_clip", "")
	if not rest.is_empty() and players.has(rest):
		players[rest].play(rest); players[rest].seek(0.0, true, true); base = _capture()

func _capture() -> Array:
	var result: Array = []
	for node in nodes: result.append(node.transform)
	for skeleton in skeletons:
		for i in range(skeleton.get_bone_count()): result.append(skeleton.get_bone_pose(i))
	for item in morphs: result.append(item[0].get_blend_shape_value(item[1]))
	return result

func _restore(values: Array):
	var index := 0
	for node in nodes: node.transform = values[index]; index += 1
	for skeleton in skeletons:
		for i in range(skeleton.get_bone_count()): skeleton.set_bone_pose(i, values[index]); index += 1
	for item in morphs: item[0].set_blend_shape_value(item[1], values[index]); index += 1
	for skeleton in skeletons: skeleton.force_update_all_bone_transforms()

func _mix(a: Array, b: Array, weight: float) -> Array:
	var result: Array = []
	for i in range(a.size()):
		if a[i] is Transform3D: result.append(a[i].interpolate_with(b[i], weight))
		else: result.append(lerpf(a[i], b[i], weight))
	return result

func _sample(clip: String, time: float) -> Array:
	_restore(base)
	if clip.is_empty(): return base.duplicate()
	var player: AnimationPlayer = players[clip]
	var animation: Animation = player.get_animation(clip)
	var length := animation.length
	var sample_time := maxf(0.0, time)
	var loops: Array = root.get_meta("gamedirector_loop_clips", [])
	var looping: bool = animation.loop_mode != Animation.LOOP_NONE or loops.has(clip)
	if looping: sample_time = fposmod(sample_time, length)
	else: sample_time = minf(sample_time, length)
	if animation.has_meta("gamedirector_base_clip"):
		var body: String = animation.get_meta("gamedirector_base_clip")
		players[body].stop(); players[body].play(body); players[body].seek(fposmod(time, players[body].get_animation(body).length), true, true)
	player.stop(); player.play(clip); player.seek(sample_time, true, true)
	var sampled := _capture()
	var rest: String = root.get_meta("gamedirector_return_clip", "")
	var fade: float = root.get_meta("gamedirector_return_fade", 0.0)
	var holds: Array = root.get_meta("gamedirector_hold_clips", [])
	if not looping and not holds.has(clip) and not rest.is_empty() and fade > 0 and time > length - fade:
		_restore(base)
		players[rest].stop(); players[rest].play(rest); players[rest].seek(maxf(0.0, time - length + fade), true, true)
		sampled = _mix(sampled, _capture(), clampf((time - length + fade) / fade, 0.0, 1.0))
	return sampled

func evaluate(pose: Dictionary, world_time: float, moving: bool):
	var motion_clip: String = root.get_meta("gamedirector_move_clip", "")
	var clip: String = pose.get("clip", root.get_meta("gamedirector_rest_clip", ""))
	var start: float = pose.get("start", 0.0)
	var key: String = clip + "@" + str(start)
	if moving and not motion_clip.is_empty(): clip = motion_clip; key = "motion"
	if layers.is_empty() or layers.back().key != key:
		var fade: float = pose.get("fade", 0.0)
		if key == "motion" or (not layers.is_empty() and layers.back().key == "motion"):
			fade = root.get_meta("gamedirector_motion_fade", 0.0)
		layers.append({"key": key, "clip": clip, "start": world_time if key == "motion" else start, "since": world_time, "fade": fade})
	var result := base.duplicate()
	var weights: Array = []; var remaining := 1.0
	for i in range(layers.size() - 1, -1, -1):
		var layer: Dictionary = layers[i]
		var weight := remaining * (clampf((world_time - layer.since) / layer.fade, 0.0, 1.0) if layer.fade > 0 else 1.0)
		weights.push_front(weight); remaining -= weight
	var total := remaining
	for i in range(layers.size()):
		if weights[i] <= 0: continue
		var layer: Dictionary = layers[i]
		var time: float = world_time - layer.start
		if layer.key == "motion": time *= float(root.get_meta("gamedirector_motion_rate", 1.0))
		total += weights[i]; result = _mix(result, _sample(layer.clip, time), weights[i] / total)
	_restore(result)
	if not layers.is_empty() and weights.back() == 1.0: layers = [layers.back()]
