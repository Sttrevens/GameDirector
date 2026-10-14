extends SceneTree
const Dock = preload("res://addons/gamedirector/plugin.gd")
func _initialize():
	call_deferred("run")
func run():
	var stage: Node3D = load("res://stage.tscn").instantiate()
	var before := snapshots(stage)
	var listing: Dictionary = Dock.scene_listing(stage)
	assert(listing.error.is_empty(), listing.error)
	assert(listing.declarations.roles == [{"id":"bolt", "defaultActor":"robot-yellow", "presentAtStart":true, "kind":"hero"}, {"id":"pip", "defaultActor":"robot-red", "presentAtStart":true, "kind":"npc"}])
	var clips := ["Angry", "Dance", "Death", "Idle", "Jump", "No", "Punch", "Running", "Sad", "Sitting", "Standing", "Surprised", "ThumbsUp", "WalkJump", "Walking", "Wave", "Yes"]
	for actor in listing.declarations.actors:
		assert(actor.clips == clips, str(actor.clips))
	assert(listing.declarations.locations.size() == 24)
	assert(listing.declarations.locations[0] == {"id":"bolt_start", "position":[0.0,0.0,9.0]})
	assert(listing.text.contains("bolt → robot-yellow") and listing.text.contains("cam_fly_end"))
	assert(before == snapshots(stage), "dock changed transforms, animation pose, or metadata")
	var defaults: Dictionary = JSON.parse_string(FileAccess.get_file_as_string(Dock.DEFAULTS_PATH))
	var config: String = Dock.mcp_config("gd", int(defaults.port), defaults.loopbackHost)
	assert(JSON.parse_string(config) == {"mcpServers":{"gamedirector":{"command":"gd","args":["mcp"],"env":{"GAMEDIRECTOR_ENDPOINT":"http://127.0.0.1:39779"}}}})
	assert(JSON.parse_string(Dock.mcp_config("path with spaces/gd", 40123, "127.0.0.1")).mcpServers.gamedirector.env.GAMEDIRECTOR_ENDPOINT == "http://127.0.0.1:40123")
	assert(JSON.parse_string(Dock.mcp_config("path with spaces/gd", 40123, "127.0.0.1")).mcpServers.gamedirector.command == "path with spaces/gd")
	var windows := 'C:\\Program Files\\GameDirector\\"quoted"\\gd.exe'
	assert(JSON.parse_string(Dock.mcp_config(windows, 40123, "127.0.0.1")).mcpServers.gamedirector.command == windows, "MCP config lost quotes or Windows backslashes")
	stage.get_node("pip").set_meta("gamedirector_role", "bolt")
	var invalid: Dictionary = Dock.scene_listing(stage)
	assert(invalid.error.contains("Stardeck/pip") and invalid.error.contains("duplicate"), invalid.error)
	stage.free()
	print("DOCK_PASS: 2 roles, 2 actors × 17 clips, 24 locations; no edits; named error; MCP config")
	quit()
func snapshots(stage: Node) -> Array:
	var result := []
	var nodes: Array[Node] = [stage]
	nodes.append_array(stage.find_children("*", "", true, false))
	for node in nodes:
		var row := {"path": str(stage.get_path_to(node)), "meta": {}}
		for key in node.get_meta_list(): row.meta[key] = node.get_meta(key)
		if node is Node3D: row.transform = node.transform
		if node is AnimationPlayer:
			row.animation = node.current_animation
			if not node.current_animation.is_empty(): row.position = node.current_animation_position
		if node is Skeleton3D:
			row.bones = []
			for i in node.get_bone_count(): row.bones.append(node.get_bone_pose(i))
		result.append(row)
	return result
