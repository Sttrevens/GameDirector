extends RefCounted
# Deliberately narrow: static 3D visuals plus owned manual pose players. No stage scripts.
const ALLOWED = ["Node", "Node3D", "MeshInstance3D", "Skeleton3D", "BoneAttachment3D", "AnimationPlayer", "Marker3D", "Camera3D", "WorldEnvironment", "DirectionalLight3D", "OmniLight3D", "SpotLight3D"]
var seen := {}

func inspect_path(path: String, visited := {}) -> String:
	if visited.has(path): return ""
	visited[path] = true
	if path.get_extension() in ["scn", "res"]: return path + ": binary scene/resource cannot be audited before loading"
	if path.get_extension() in ["gdshader", "gdshaderinc"]: return _shader(FileAccess.get_file_as_string(path), path, visited)
	if path.get_extension() not in ["tscn", "tres"]: return ""
	var text := FileAccess.get_file_as_string(path)
	var script := RegEx.new(); script.compile("(?m)^\\[(?:ext_resource|sub_resource)[^\\n]*type=\"(?:Script|GDScript|CSharpScript)\"")
	if script.search(text): return path + ": stage script resource is not allowed (refused before ResourceLoader/static initializers)"
	var resources := RegEx.new(); resources.compile("(?m)^\\[ext_resource[^\\n]*path=\"([^\"]+)\"")
	for match_row in resources.search_all(text):
		var why := inspect_path(match_row.get_string(1), visited)
		if not why.is_empty(): return why
	return ""

func _shader(text: String, path: String, visited := {}) -> String:
	var pattern := RegEx.new(); pattern.compile("\\bTIME\\b")
	if pattern.search(text): return path + ": shader TIME is not owned; use a director-owned uniform"
	pattern.compile("(?m)^\\s*#include\\s*\"([^\"]+)\"")
	for row in pattern.search_all(text):
		var why := inspect_path(row.get_string(1), visited)
		if not why.is_empty(): return why
	return ""

func inspect_source(scene: PackedScene) -> String:
	for row in ProjectSettings.get_property_list():
		if str(row.name).begins_with("autoload/"):
			return "project/" + str(row.name) + ": autoload clocks/scripts are not owned"
	seen.clear()
	return _resource(scene, "stage")

func _resource(resource: Resource, path: String) -> String:
	if resource == null or seen.has(resource.get_instance_id()): return ""
	seen[resource.get_instance_id()] = true
	if resource is Script: return path + ": stage scripts are not allowed (including _init/_ready/_process/_physics_process)"
	if resource is AnimatedTexture: return path + ": AnimatedTexture clock is not owned"
	if resource is CameraAttributes and resource.get("auto_exposure_enabled") == true: return path + ": auto-exposure clock is not owned"
	if resource is Shader or resource is ShaderInclude:
		var why := _shader(resource.code, path)
		if not why.is_empty(): return why
	if resource is PackedScene:
		var state: SceneState = resource.get_state()
		for i in range(state.get_node_count()):
			var node_path := path + "/" + str(state.get_node_path(i))
			var instance := state.get_node_instance(i)
			var why := _resource(instance, node_path)
			if not why.is_empty(): return why
			for j in range(state.get_node_property_count(i)):
				why = _value(state.get_node_property_value(i, j), node_path + ":" + str(state.get_node_property_name(i, j)))
				if not why.is_empty(): return why
	for property in resource.get_property_list():
		if property.usage & PROPERTY_USAGE_STORAGE:
			var why := _value(resource.get(property.name), path + ":" + str(property.name))
			if not why.is_empty(): return why
	return ""

func _value(value, path: String) -> String:
	if value is Resource: return _resource(value, path)
	if value is Array or value is Dictionary:
		for key in value:
			var why := _value(value[key] if value is Dictionary else key, path)
			if not why.is_empty(): return why
	return ""

func inspect_nodes(root: Node) -> String:
	var nodes: Array[Node] = [root]
	nodes.append_array(root.find_children("*", "", true, false))
	for node in nodes:
		var path := str(root.name) + "/" + str(root.get_path_to(node))
		if not ALLOWED.has(node.get_class()): return path + ": " + node.get_class() + " is not an owned static/pose node"
		if node.get_script() != null: return path + ": stage scripts are not allowed"
		if node.process_mode == Node.PROCESS_MODE_ALWAYS: return path + ": process-through-pause is not owned"
		if node.physics_interpolation_mode == Node.PHYSICS_INTERPOLATION_MODE_ON: return path + ": physics interpolation is not owned"
		if node is BoneAttachment3D and (node.use_external_skeleton or node.override_pose or not node.get_parent() is Skeleton3D):
			return path + ": only passive attachments to the parent owned skeleton are allowed"
		if node is AnimationPlayer:
			if node.callback_mode_process != AnimationMixer.ANIMATION_CALLBACK_MODE_PROCESS_MANUAL:
				return path + ": AnimationPlayer must declare MANUAL in the scene"
			if not node.autoplay.is_empty(): return path + ": AnimationPlayer autoplay is not owned"
			for clip in node.get_animation_list():
				var animation: Animation = node.get_animation(clip)
				for i in range(animation.get_track_count()):
					if animation.track_get_type(i) not in [Animation.TYPE_POSITION_3D, Animation.TYPE_ROTATION_3D, Animation.TYPE_SCALE_3D, Animation.TYPE_BLEND_SHAPE]:
						return path + ": " + str(clip) + " track " + str(i) + " is not an owned pose/morph track"
		if node is MeshInstance3D:
			var resources: Array = [node.material_override, node.material_overlay, node.mesh]
			for material in resources:
				var why := _value(material, path)
				if not why.is_empty(): return why
	return ""
