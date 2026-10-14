extends SceneTree
# Test-only scene builder: the same nodes/metadata a developer adds in the Inspector.
func _initialize(): call_deferred("build")
func build():
	var stage := Node3D.new(); stage.name = "FirstFilm"
	stage.set_meta("gamedirector_game", "FirstFilm")
	stage.set_meta("gamedirector_version", "1")
	var robot: PackedScene = load("res://assets/RobotExpressive.glb")
	var roles := [{"id":"bolt", "actor":"robot-yellow", "position":Vector3.ZERO},
		{"id":"pip", "actor":"robot-red", "position":Vector3(3,0,0)}]
	for row in roles:
		var actor := Node3D.new(); actor.name = row.id; actor.position = row.position
		actor.rotation.y = PI
		actor.set_meta("gamedirector_role", row.id); actor.set_meta("gamedirector_actor", row.actor)
		actor.set_meta("gamedirector_present_at_start", true)
		actor.set_meta("gamedirector_rest_clip", "Idle")
		actor.set_meta("gamedirector_move_clip", "Walking")
		actor.set_meta("gamedirector_loop_clips", ["Idle", "Walking"])
		var model := robot.instantiate(PackedScene.GEN_EDIT_STATE_INSTANCE); model.name = "Model"; model.scale = Vector3.ONE * 0.4
		actor.add_child(model); stage.add_child(actor)
		stage.set_editable_instance(model, true)
		for player in model.find_children("*", "AnimationPlayer", true, false):
			player.callback_mode_process = AnimationMixer.ANIMATION_CALLBACK_MODE_PROCESS_MANUAL
			player.autoplay = ""
	var hallway: Node3D = load("res://assets/space_ship_hallway.glb").instantiate()
	hallway.name = "Hallway"; hallway.position = Vector3(-20.115,3.08,11); stage.add_child(hallway)
	var marker := Marker3D.new(); marker.name = "CameraFront"; marker.position = Vector3(0,1.5,-6)
	marker.set_meta("gamedirector_location", "camera_front"); stage.add_child(marker)
	var environment := Environment.new(); environment.background_mode = Environment.BG_COLOR
	environment.background_color = Color(0.02,0.03,0.05)
	environment.ambient_light_source = Environment.AMBIENT_SOURCE_COLOR
	environment.ambient_light_color = Color(0.86,0.91,1); environment.ambient_light_energy = 0.65
	var world := WorldEnvironment.new(); world.name = "Environment"; world.environment = environment; stage.add_child(world)
	var key := DirectionalLight3D.new(); key.name = "Key"; key.rotation = Vector3(-0.7,-0.52,0); key.light_energy = 1.2; stage.add_child(key)
	for node in stage.find_children("*", "", true, false):
		# Keep imported subtree ownership: flattening it duplicates inherited players.
		if node.owner == null: node.owner = stage
	var packed := PackedScene.new(); assert(packed.pack(stage) == OK)
	assert(ResourceSaver.save(packed, "res://stage.tscn") == OK)
	ProjectSettings.set_setting("application/run/main_scene", "res://stage.tscn")
	assert(ProjectSettings.save() == OK)
	stage.free()
	print("SCENE_BUILT: 2 roles; imported manual animation clips; camera_front location")
	quit()
