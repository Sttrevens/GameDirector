@tool
extends EditorPlugin
const Manifest = preload("manifest.gd")
const Child = preload("bridge_child.gd")
const DEFAULTS_PATH = "res://addons/gamedirector/bridge_defaults.json"
const PATH_SETTING = "gamedirector/gd_path"
const PORT_SETTING = "gamedirector/bridge_port"
var defaults: Dictionary = JSON.parse_string(FileAccess.get_file_as_string(DEFAULTS_PATH))
var child := Child.new()
var dock: VBoxContainer
var declarations: RichTextLabel
var status: Label
var button: Button
var reuse: Button
var recovered_stop: Button
var elapsed := 0.0
var scene_stale := false
var restarting := false
var settings: EditorSettings

static func scene_listing(root: Node3D) -> Dictionary:
	var inspector := Manifest.new()
	var declared: Dictionary = inspector.declarations(root)
	if not inspector.error.is_empty(): return {"error": inspector.error, "text": inspector.error, "declarations": {}}
	var text := "Roles / clips\n"
	for role in declared.roles: text += str(role.id) + " → " + str(role.defaultActor) + "\n"
	for actor in declared.actors: text += str(actor.id) + ": " + ", ".join(actor.clips) + "\n"
	text += "\nLocations / anchors\n"
	for location in declared.locations: text += str(location.id) + " " + str(location.position) + "\n"
	return {"error": "", "text": text, "declarations": declared}

static func mcp_config(gd: String, port: int, host: String) -> String:
	return JSON.stringify({"mcpServers": {"gamedirector": {"command": gd, "args": ["mcp"],
		"env": {"GAMEDIRECTOR_ENDPOINT": "http://" + host + ":" + str(port)}}}})
func _enter_tree():
	settings = EditorInterface.get_editor_settings()
	if not settings.has_setting(PATH_SETTING):
		settings.set_setting(PATH_SETTING, str(ProjectSettings.get_setting(PATH_SETTING, "gd")))
	settings.set_initial_value(PATH_SETTING, "gd", false)
	settings.add_property_info({"name": PATH_SETTING, "type": TYPE_STRING, "hint": PROPERTY_HINT_GLOBAL_FILE})
	# Remove the legacy machine path only in memory; never rewrite project.godot silently.
	if ProjectSettings.has_setting(PATH_SETTING): ProjectSettings.set_setting(PATH_SETTING, null)
	ProjectSettings.set_setting(PORT_SETTING, int(ProjectSettings.get_setting(PORT_SETTING, defaults.port)))
	ProjectSettings.add_property_info({"name": PORT_SETTING, "type": TYPE_INT, "hint": PROPERTY_HINT_RANGE, "hint_string": "1,65535,1"})
	dock = VBoxContainer.new(); dock.name = "GameDirector"
	status = Label.new(); status.text = "Bridge stopped"; dock.add_child(status)
	declarations = RichTextLabel.new(); declarations.custom_minimum_size = Vector2(250, 220); dock.add_child(declarations)
	var refresh := Button.new(); refresh.text = "Refresh scene declarations"; refresh.pressed.connect(_refresh); dock.add_child(refresh)
	button = Button.new(); button.text = "Start bridge"; button.pressed.connect(_button_action); dock.add_child(button)
	reuse = Button.new(); reuse.text = "Reuse owned bridge"; reuse.pressed.connect(_reuse); dock.add_child(reuse); reuse.hide()
	recovered_stop = Button.new(); recovered_stop.text = "Stop recovered bridge"; recovered_stop.pressed.connect(_stop_recovered); dock.add_child(recovered_stop); recovered_stop.hide()
	var copy := Button.new(); copy.text = "Copy MCP config"; copy.pressed.connect(_copy_mcp); dock.add_child(copy)
	add_control_to_dock(EditorPlugin.DOCK_SLOT_RIGHT_UL, dock)
	scene_changed.connect(_scene_changed); scene_saved.connect(_saved); resource_saved.connect(_saved)
	child.scan(); _refresh(); set_process(true)
func _exit_tree():
	child.close()
	if dock != null: remove_control_from_docks(dock); dock.queue_free(); dock = null
func _scene_changed(_root: Node): _refresh()
func _saved(_resource):
	if child.running():
		scene_stale = true; status.text = "Scene changed — Restart bridge"; button.text = "Restart bridge"
func _refresh():
	var root := EditorInterface.get_edited_scene_root()
	declarations.text = scene_listing(root).text if root is Node3D else "Open a Node3D stage scene."
func _gd() -> String: return str(settings.get_setting(PATH_SETTING))
func _port() -> int: return int(ProjectSettings.get_setting(PORT_SETTING, defaults.port))
func _button_action():
	if scene_stale and child.running(): _restart()
	else: _toggle()
func _restart():
	restarting = true; _toggle()
func _reuse(): child.recover("reuse")
func _stop_recovered(): child.recover("stop")
func _toggle():
	if child.running():
		child.stop(); status.text = "Stopping owned bridge…"; button.disabled = true; return
	restarting = false
	if not child.record.data.is_empty():
		status.text = "Previous bridge is still tracked; reuse or stop it before starting another bridge (even on a different port)."; return
	var root := EditorInterface.get_edited_scene_root()
	if root == null or root.scene_file_path.is_empty(): status.text = "Save the current stage scene first."; return
	if not root is Node3D: status.text = "Stage root must be Node3D."; return
	var listing := scene_listing(root)
	if not listing.error.is_empty(): status.text = listing.error; return
	if _port() <= 0 or _port() > 65535: status.text = "Set a bridge port in 1..65535."; return
	var gd := Child.resolve_executable(_gd())
	if gd.is_empty(): status.text = "gd not found. Set the absolute <distribution>/bin/gd path (gd.exe on Windows) in Editor Settings → " + PATH_SETTING + "."; return
	var listener := TCPServer.new()
	if listener.listen(_port(), defaults.loopbackHost) != OK:
		status.text = "Port " + str(_port()) + " is in use by another bridge or process; stop it or change " + PORT_SETTING + "."; return
	listener.stop()
	if not child.start(gd, ProjectSettings.globalize_path("res://"), root.scene_file_path, OS.get_executable_path(), _port()):
		status.text = child.error; return
	scene_stale = false; button.text = "Stop bridge"; button.disabled = false
	status.text = "Starting bridge (importing project)…"
func _process(delta: float):
	if dock == null: return
	elapsed += delta
	if elapsed < 1.0: return
	elapsed = 0.0; child.poll()
	if not child.error.is_empty(): restarting = false
	reuse.visible = child.recoverable; recovered_stop.visible = child.recoverable
	if child.pid > 0 and not child.running():
		var stopped := child.stopping
		var failure := "Failed (exit " + str(child.exit_code) + ")\n" + child.tail
		child.forget(not stopped); button.text = "Start bridge"; button.disabled = false; scene_stale = false
		if restarting:
			restarting = false; _toggle(); return
		status.text = "Bridge stopped" if stopped else failure
	elif not child.error.is_empty():
		status.text = child.error; button.disabled = false
	elif child.stopping:
		status.text = "Stopping owned bridge…"
	elif child.running():
		button.disabled = false
		if scene_stale: status.text = "Scene changed — Restart bridge"; button.text = "Restart bridge"
		elif not child.endpoint.is_empty(): status.text = "Ready — " + child.endpoint; button.text = "Stop bridge"
	elif child.recoverable:
		status.text = "Recovered owned bridge on port " + str(int(child.record.data.port)) + " — reuse or stop it."
	elif not child.record.data.is_empty():
		status.text = "Previous bridge record found — checking ownership; no process will be stopped without proof."
func _copy_mcp():
	if _port() <= 0 or _port() > 65535: status.text = "Set a bridge port in 1..65535."; return
	var port := int(child.record.data.get("port", _port())) if child.running() else _port()
	DisplayServer.clipboard_set(mcp_config(_gd(), port, defaults.loopbackHost))
	# Clipboard actions never mask readiness, failures, or source freshness.
	status.tooltip_text = "MCP JSON copied. Codex: map command/args/env to mcp_servers.gamedirector."
