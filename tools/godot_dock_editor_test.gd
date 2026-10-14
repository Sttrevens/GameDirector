@tool
extends EditorPlugin
var dock
var options: Dictionary
var checks := 0
func _enter_tree():
	if not "--import" in OS.get_cmdline_args(): call_deferred("run")
func expect(ok: bool, message: String):
	if not ok:
		push_error(message); get_tree().quit(1)
	checks += 1
func run():
	options = JSON.parse_string(FileAccess.get_file_as_string("res://dock_options.json"))
	EditorInterface.open_scene_from_path("res://stage.tscn")
	for i in range(10): await get_tree().process_frame
	for node in get_tree().root.find_children("*", "EditorPlugin", true, false):
		if node.get_script() != null and node.get_script().resource_path == "res://addons/gamedirector/plugin.gd": dock = node
	expect(dock != null, "real enabled dock plugin missing")
	var mode := OS.get_environment("GAMEDIRECTOR_DOCK_TEST_MODE")
	if mode.begins_with("state-"):
		await load("res://dock_state_editor.gd").run(self, mode); return
	if not mode.is_empty():
		await load("res://dock_crash.gd").run(self, mode); return
	var settings := EditorInterface.get_editor_settings()
	expect(typeof(ProjectSettings.get_setting(dock.PORT_SETTING)) == TYPE_INT, "legacy float port was not migrated")
	expect(settings.get_setting(dock.PATH_SETTING) == options.gd, "legacy project gd_path was not migrated")
	for property in settings.get_property_list():
		if property.name == dock.PATH_SETTING: expect(property.hint == PROPERTY_HINT_GLOBAL_FILE, "gd path picker is not a global file")
	expect(load("res://addons/gamedirector/bridge_child.gd").resolve_executable(str(options.gd).get_file()) == options.gd, "PATH lookup did not resolve the real launcher")
	settings.set_setting(dock.PATH_SETTING, "/missing/gd")
	ProjectSettings.set_setting(dock.PATH_SETTING, "/missing/gd")
	dock._toggle()
	expect(dock.child.pid <= 0 and dock.status.text.contains("not found"), "missing executable looked like a successful start: " + dock.status.text)
	expect(dock.status.text.contains("Editor Settings"), "missing executable remedy must name per-machine Editor Settings")
	dock._copy_mcp()
	expect(not dock.status.text.contains("ready"), "copy config falsely claims readiness")
	settings.set_setting(dock.PATH_SETTING, options.gd)
	ProjectSettings.set_setting(dock.PORT_SETTING, int(options.port))
	expect(dock._gd() == options.gd, "legacy project path overrides machine editor path")
	expect(typeof(ProjectSettings.get_setting(dock.PORT_SETTING)) == TYPE_INT, "port must remain an integer")
	var occupied := TCPServer.new()
	expect(occupied.listen(int(options.port), dock.defaults.loopbackHost) == OK, "test port unavailable")
	dock._toggle()
	expect(dock.child.pid <= 0 and dock.status.text.contains("in use"), "occupied port attempted a bridge launch")
	occupied.stop()
	if options.has("failure"):
		settings.set_setting(dock.PATH_SETTING, options.failure)
		for i in range(3):
			dock._toggle()
			await wait_for("Failed", 15)
			expect(dock.status.text.contains("7") and dock.status.text.contains("intentional startup failure"), "failure must show exit and tail: " + dock.status.text)
		var logs := 0
		for name in DirAccess.get_files_at(ProjectSettings.globalize_path("res://.godot/gamedirector")):
			if name.ends_with(".log"): logs += 1
		expect(logs == 1, "failed launches must retain at most the last diagnostic log: " + str(logs))
	else: print("SKIP: startup-failure shell fixture is POSIX-only.")
	settings.set_setting(dock.PATH_SETTING, options.gd)
	dock._toggle()
	print("DOCK_OWNED_PID:", dock.child.pid)
	expect(dock.status.text.contains("Starting") and dock.status.text.contains("import"), "startup state not visible")
	var starting_text: String = dock.status.text
	dock._copy_mcp()
	expect(dock.status.text == starting_text, "copy config masked starting state")
	await wait_for("Ready", 60)
	expect(dock.status.text.contains("http://"), "ready state omitted endpoint")
	var root := EditorInterface.get_edited_scene_root()
	var before = root.transform
	dock._refresh(); dock._copy_mcp()
	expect(root.transform == before, "dock moved authored scene at edit time")
	root.set_meta("save_probe", true)
	EditorInterface.save_scene_as("res://stage.tscn", false) # No thumbnail renderer in a headless editor.
	for i in range(3): await get_tree().process_frame
	expect(dock.status.text.contains("Scene changed") and dock.button.text.contains("Restart"), "save did not expose source restart remedy")
	await load("res://dock_freshness.gd").check(dock, get_tree())
	dock._restart()
	await wait_for("Ready", 60)
	expect(not dock.status.text.contains("Scene changed"), "restart retained stale status")
	dock._toggle()
	await wait_for("Bridge stopped", 15)
	for i in range(5):
		dock._process(1.0)
		expect(dock.status.text == "Bridge stopped", "normal Stop status regressed on tick " + str(i) + ": " + dock.status.text)
		await get_tree().create_timer(0.05).timeout
	expect(not dock.status.text.contains("startup errors"), "user Stop reported startup errors")
	await recovery()
	await load("res://dock_pid.gd").check(self)
	dock._toggle()
	print("DOCK_OWNED_PID:", dock.child.pid)
	await get_tree().create_timer(0.1).timeout
	var exiting_pid: int = dock.child.pid
	dock._exit_tree()
	expect(not OS.is_process_running(exiting_pid), "_exit_tree orphaned owned child during import")
	print("DOCK_EDITOR_PASS: ", checks, " checks; real _toggle/_process/_copy_mcp/_exit_tree")
	get_tree().quit()
func wait_for(text: String, seconds: int):
	var deadline := Time.get_ticks_msec() + seconds * 1000
	while Time.get_ticks_msec() < deadline:
		dock._process(1.0)
		if dock.status.text.contains(text): return
		await get_tree().create_timer(0.05).timeout
	assert(false, "dock timeout for " + text + ": " + dock.status.text)
func recovery():
	# Simulate losing the controller at a crash, without allowing its orderly exit.
	dock._toggle()
	print("DOCK_OWNED_PID:", dock.child.pid)
	await wait_for("Ready", 60)
	var abandoned = dock.child
	var owned: int = abandoned.pid
	dock.child = load("res://addons/gamedirector/bridge_child.gd").new()
	dock.child.scan()
	await wait_for("Recovered", 15)
	expect(dock.child.pid <= 0 and dock.child.recoverable, "orphan was silently adopted or not proved")
	dock._reuse()
	await wait_for("Ready", 15)
	expect(dock.child.pid == owned, "reuse spawned or selected another process")
	# Saved project resource has the same stale-source UI contract as a scene save.
	dock.resource_saved.emit(load("res://stage.tscn"))
	expect(dock.status.text.contains("Scene changed"), "resource save ignored")
	dock._toggle()
	await wait_for("Bridge stopped", 15)
	abandoned.pid = -1
	var unrelated := OS.create_process(OS.get_executable_path(), PackedStringArray(["--headless", "--path", ProjectSettings.globalize_path("res://"), "--script", "res://dock_idle.gd"]))
	print("DOCK_OWNED_PID:", unrelated)
	var record: Dictionary = {"pid": unrelated, "port": int(options.port), "token": "not-ours", "project": ProjectSettings.globalize_path("res://").trim_suffix("/")}
	var file := FileAccess.open(dock.child.RECORD_PATH, FileAccess.WRITE)
	file.store_string(JSON.stringify(record)); file.close()
	dock.child.scan()
	for i in range(10): dock._process(1.0); await get_tree().create_timer(0.05).timeout
	dock._stop_recovered()
	for i in range(10): dock._process(1.0); await get_tree().create_timer(0.05).timeout
	expect(OS.is_process_running(unrelated) and not dock.child.recoverable, "unproved or reused PID was killed")
	OS.kill(unrelated)
	DirAccess.remove_absolute(ProjectSettings.globalize_path(dock.child.RECORD_PATH))
	# A second orphan is stopped using the recovery action instead of reused.
	dock._toggle(); await wait_for("Ready", 60)
	abandoned = dock.child
	owned = abandoned.pid
	print("DOCK_OWNED_PID:", owned)
	dock.child = load("res://addons/gamedirector/bridge_child.gd").new(); dock.child.scan()
	await wait_for("Recovered", 15)
	dock._stop_recovered()
	await wait_for("Bridge stopped", 15)
	expect(not OS.is_process_running(owned), "recovered Stop left owned child alive")
	abandoned.pid = -1
