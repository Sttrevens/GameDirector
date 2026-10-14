extends RefCounted
static func run(probe, mode: String):
	var dock = probe.dock
	dock._toggle(); await probe.wait_for("Ready", 60)
	if mode == "state-stop":
		var log_path := str(dock.child.log_path)
		dock._toggle(); await probe.wait_for("Bridge stopped", 15)
		for i in range(5):
			dock._process(1.0)
			probe.expect(dock.status.text == "Bridge stopped" and not dock.child.stopping, "Stop status regressed on tick " + str(i) + ": " + dock.status.text)
			await probe.get_tree().create_timer(0.05).timeout
		probe.expect(not FileAccess.file_exists(log_path), "normal Stop retained the owned log")
	else:
		var controller = dock.child
		var owned := int(controller.pid)
		print("DOCK_OWNED_PID:", owned)
		dock.child = load("res://addons/gamedirector/bridge_child.gd").new(); dock.child.scan()
		await probe.wait_for("Recovered", 15)
		if mode == "state-orphan":
			var listener := TCPServer.new(); probe.expect(listener.listen(0, "127.0.0.1") == OK, "alternate port unavailable")
			var port := listener.get_local_port(); listener.stop()
			ProjectSettings.set_setting(dock.PORT_SETTING, port)
			dock._toggle()
			probe.expect(dock.child.pid < 0 and int(dock.child.record.data.pid) == owned and dock.child.recoverable, "alternate-port start abandoned the proven orphan")
			probe.expect(dock.status.text.contains("reuse or stop"), "orphan refusal gives no recovery remedy: " + dock.status.text)
		else:
			dock._reuse(); await probe.wait_for("Ready", 15)
			var old_path := OS.get_environment("PATH")
			if mode == "state-signal":
				if not probe.options.has("signal_failure"):
					print("SKIP: signal-failure executable fixture is POSIX-only.")
					dock.child.close(); controller.close(); dock._exit_tree(); probe.get_tree().quit(); return
				OS.set_environment("PATH", str(probe.options.signal_failure) + ":" + old_path)
			else: dock.child.record.data.token = "wrong-token"
			dock.scene_stale = true; dock._restart()
			var deadline := Time.get_ticks_msec() + 10000
			while dock.child.error.is_empty() and Time.get_ticks_msec() < deadline:
				dock._process(1.0); await probe.get_tree().create_timer(0.05).timeout
			OS.set_environment("PATH", old_path)
			if mode == "state-signal": probe.expect(FileAccess.file_exists(str(probe.options.signal_failure).path_join("called")), "signal failure fixture was not executed")
			probe.expect(not dock.child.error.is_empty() and not dock.restarting and not dock.child.stopping, "failed adopted Stop retained restarting/stopping")
			probe.expect(dock.status.text.contains("signal exit") if mode == "state-signal" else dock.status.text.contains("Ownership"), "failed adopted Stop did not show final failure: " + dock.status.text)
		# Always clean up the original owned child, even if the damaged record is refused.
		dock.child.close(); controller.close()
	dock._exit_tree()
	print("DOCK_STATE_EDITOR_PASS: ", mode, " ", probe.checks, " checks")
	probe.get_tree().quit()
