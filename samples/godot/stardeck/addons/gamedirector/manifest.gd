extends RefCounted
var roles := {}
var locations := {}
var players := {}
var presence := {}
var error := ""

func inspect(root: Node3D, graph: Dictionary, bindings: Dictionary) -> Dictionary:
	roles.clear(); locations.clear(); players.clear(); presence.clear(); error = ""
	var manifest := {"game": root.get_meta("gamedirector_game", root.name), "gameVersion": root.get_meta("gamedirector_version", "scene"),
		"manifestVersion": bindings.manifestVersion, "roles": [], "actors": [], "locations": [], "audio": [],
		"shotTypes": [], "frameTypes": [], "capabilities": {}}
	for row in graph.camera.shots: manifest.shotTypes.append(row.id)
	for row in graph.camera.frames: manifest.frameTypes.append(row.id)
	manifest.capabilities[bindings.capabilities.spawn] = bindings.spawnSupported
	manifest.capabilities[bindings.capabilities.fade] = bindings.crossfade
	var actors := {}
	var nodes: Array[Node] = [root]
	nodes.append_array(root.find_children("*", "", true, false))
	for node in nodes:
		var path := str(root.name) + "/" + str(root.get_path_to(node))
		if node.has_meta("gamedirector_role"):
			if not node is Node3D: error = path + ": role must be Node3D"; return {}
			var id = node.get_meta("gamedirector_role")
			if not id is String or id.is_empty() or roles.has(id): error = path + ": invalid/duplicate role id"; return {}
			roles[id] = node
			var actor = node.get_meta("gamedirector_actor", id)
			if not actor is String or actor.is_empty(): error = path + ": invalid actor id"; return {}
			var present = node.get_meta("gamedirector_present_at_start", true)
			if not present is bool: error = path + ": presentAtStart must be boolean"; return {}
			var role := {"id": id, "defaultActor": actor, "presentAtStart": present}
			if node.has_meta("gamedirector_kind"): role.kind = node.get_meta("gamedirector_kind")
			manifest.roles.append(role); presence[id] = present
			var list := node.find_children("*", "AnimationPlayer", true, false)
			var clips: Array = []
			var clip_players := {}
			for player in list:
				for clip in player.get_animation_list():
					if str(clip) == "RESET": continue # Godot import's synthetic rest resource, not an authored clip.
					if clip_players.has(str(clip)): error = path + ": duplicate clip " + str(clip); return {}
					clips.append(str(clip)); clip_players[str(clip)] = player
			clips.sort(); players[id] = clip_players
			if actors.has(actor) and actors[actor] != clips: error = path + ": shared actor has different animation libraries"; return {}
			if not actors.has(actor): manifest.actors.append({"id": actor, "clips": clips}); actors[actor] = clips
		if node.has_meta("gamedirector_location"):
			if not node is Marker3D: error = path + ": location/anchor must be Marker3D"; return {}
			var id = node.get_meta("gamedirector_location")
			if not id is String or id.is_empty() or locations.has(id): error = path + ": invalid/duplicate location id"; return {}
			locations[id] = node
			var position: Vector3 = _world_transform(node).origin
			manifest.locations.append({"id": id, "position": [position.x, position.y, position.z]})
	return manifest

func declarations(root: Node3D) -> Dictionary:
	# Editor read-only projection: the exact metadata/clip validator, no engine claims.
	return inspect(root, {"camera": {"shots": [], "frames": []}}, {"manifestVersion": "", "capabilities": {"spawn": "", "fade": ""}, "spawnSupported": "", "crossfade": ""})

func _world_transform(node: Node3D) -> Transform3D:
	var result := node.transform
	var parent := node.get_parent()
	while parent is Node3D:
		result = parent.transform * result
		parent = parent.get_parent()
	return result
