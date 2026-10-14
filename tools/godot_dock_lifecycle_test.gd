extends SceneTree
const Child = preload("res://addons/gamedirector/bridge_child.gd")
var child := Child.new()
var unrelated := -1
func _initialize(): call_deferred("run")
func run():
	var options: Dictionary = JSON.parse_string(FileAccess.get_file_as_string("res://dock_options.json"))
	unrelated = OS.create_process(OS.get_executable_path(), PackedStringArray(["--headless", "--path", ProjectSettings.globalize_path("res://"), "--script", "res://dock_idle.gd"]))
	print("DOCK_OWNED_PID:", unrelated)
	assert(unrelated > 0 and OS.is_process_running(unrelated))
	assert(child.start(options.gd, ProjectSettings.globalize_path("res://"), "res://stage.tscn", OS.get_executable_path(), options.port))
	var owned: int = child.pid
	print("DOCK_OWNED_PID:", owned)
	assert(not child.start(options.gd, ProjectSettings.globalize_path("res://"), "res://stage.tscn", OS.get_executable_path(), options.port))
	assert(child.pid == owned and child.running(), "second start replaced owned child")
	var http := HTTPRequest.new(); root.add_child(http)
	var ready := false
	var deadline := Time.get_ticks_msec() + 45000
	while Time.get_ticks_msec() < deadline and child.running():
		assert(http.request("http://127.0.0.1:" + str(int(options.port)) + "/health") == OK)
		var response = await http.request_completed
		if response[1] == 200:
			ready = JSON.parse_string(response[3].get_string_from_utf8()).engine == "godot"
			if ready: break
		await create_timer(0.1).timeout
	assert(ready, "owned bridge failed to become ready")
	child.stop()
	deadline = Time.get_ticks_msec() + 15000
	while child.running() and Time.get_ticks_msec() < deadline: await process_frame
	assert(not child.running(), "stop left own child running")
	assert(OS.is_process_running(unrelated), "stop killed unrelated child")
	assert(child.start(options.gd, ProjectSettings.globalize_path("res://"), "res://stage.tscn", OS.get_executable_path(), options.port))
	print("DOCK_OWNED_PID:", child.pid)
	await create_timer(0.5).timeout
	child.close() # The exact cleanup invoked by plugin._exit_tree, including startup cancellation.
	assert(not child.running() and OS.is_process_running(unrelated), "editor-exit cleanup violated PID ownership")
	OS.kill(unrelated); unrelated = -1
	http.queue_free()
	print("DOCK_LIFECYCLE_PASS: duplicate start refused; ready child stopped; exit cancels startup; unrelated child survives")
	quit()
func _finalize():
	child.close()
	if unrelated > 0 and OS.is_process_running(unrelated): OS.kill(unrelated)
