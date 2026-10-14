extends RefCounted
static func run(probe, mode: String):
	var dock = probe.dock
	EditorInterface.get_editor_settings().set_setting(dock.PATH_SETTING, probe.options.gd)
	if mode == "crash":
		dock._toggle()
		await probe.wait_for("Ready", 60)
		print("DOCK_OWNED_PID:", dock.child.pid)
		print("DOCK_CRASH_READY:", dock.child.pid)
		# The Python controller kills this editor without _exit_tree cleanup.
		while true: await probe.get_tree().process_frame
	else:
		await probe.wait_for("Recovered", 15)
		probe.expect(dock.child.recoverable and dock.child.pid < 0, "fresh editor did not prove the orphan before offering recovery")
		var pid := int(dock.child.record.data.pid)
		print("DOCK_OWNED_PID:", pid)
		dock._reuse()
		await probe.wait_for("Ready", 15)
		probe.expect(dock.child.pid == pid, "fresh editor reused a different process")
		dock._toggle()
		await probe.wait_for("Bridge stopped", 15)
		await probe.get_tree().create_timer(0.1).timeout
		var record = load("res://addons/gamedirector/bridge_record.gd").new()
		record.data = {"pid": pid}
		probe.expect(not record.alive(), "fresh editor Stop leaked the orphan")
		dock._exit_tree()
		print("DOCK_RECOVERY_PASS: actual killed editor, fresh editor, proven reuse and Stop")
		probe.get_tree().quit()
