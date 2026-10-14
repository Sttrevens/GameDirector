extends RefCounted
static func check(probe):
	var dock = probe.dock
	dock._toggle()
	await probe.wait_for("Ready", 60)
	var controller = dock.child
	var owned := int(controller.pid)
	print("DOCK_OWNED_PID:", owned)
	var unrelated := OS.create_process(OS.get_executable_path(), PackedStringArray(["--headless", "--path", ProjectSettings.globalize_path("res://"), "--script", "res://dock_idle.gd"]))
	print("DOCK_OWNED_PID:", unrelated)
	var genuine: Dictionary = controller.record.data.duplicate(true)
	var wrong: Dictionary = genuine.duplicate(true)
	wrong.pid = unrelated # The real endpoint/token must NOT bless a reused PID.
	var file := FileAccess.open(controller.RECORD_PATH, FileAccess.WRITE)
	file.store_string(JSON.stringify(wrong)); file.close()
	dock.child = load("res://addons/gamedirector/bridge_child.gd").new()
	dock.child.scan()
	for i in range(10): dock._process(1.0); await probe.get_tree().create_timer(0.05).timeout
	dock._stop_recovered()
	for i in range(10): dock._process(1.0); await probe.get_tree().create_timer(0.05).timeout
	probe.expect(OS.is_process_running(unrelated) and OS.is_process_running(owned), "real endpoint proved the wrong cached PID")
	probe.expect(not dock.child.recoverable, "reused PID was offered for adoption")
	OS.kill(unrelated)
	file = FileAccess.open(controller.RECORD_PATH, FileAccess.WRITE)
	file.store_string(JSON.stringify(genuine)); file.close()
	dock.child = controller
	dock._toggle()
	await probe.wait_for("Bridge stopped", 15)
