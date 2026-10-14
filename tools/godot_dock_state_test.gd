extends SceneTree
const Child = preload("res://addons/gamedirector/bridge_child.gd")
var checks := 0
var failures := 0
func _initialize(): call_deferred("run")
func expect(ok: bool, message: String):
	if not ok:
		failures += 1
		push_error(message)
	checks += 1
func cached(child, pid: int, port: int, name := "bridge-stale.log"):
	var folder := ProjectSettings.globalize_path("res://.godot/gamedirector")
	DirAccess.make_dir_recursive_absolute(folder)
	var log := folder.path_join(name)
	var file := FileAccess.open(log, FileAccess.WRITE); file.store_string("old launch"); file.close()
	file = FileAccess.open(child.RECORD_PATH, FileAccess.WRITE)
	file.store_string(JSON.stringify({"pid": pid, "port": port, "token": "stale-secret", "project": ProjectSettings.globalize_path("res://").trim_suffix("/"), "log": log})); file.close()
	return log
func log_limit():
	for i in range(3):
		var controller := Child.new()
		cached(controller, OS.get_process_id(), 39779, "bridge-forgotten-" + str(i) + ".log")
		controller.record.load_record(controller.RECORD_PATH)
		controller.forget()
	var logs := 0
	for name in DirAccess.get_files_at(ProjectSettings.globalize_path("res://.godot/gamedirector")):
		if name.ends_with(".log"): logs += 1
	expect(logs <= 1, "forgotten launches retained more than the last log: " + str(logs))
func run():
	if OS.get_environment("GAMEDIRECTOR_DOCK_LOG_LIMIT_TEST") == "1":
		log_limit()
		if failures == 0: print("DOCK_LOG_LIMIT_PASS: ", checks, " checks")
		quit(1 if failures > 0 else 0); return
	var child := Child.new()
	var log: String = cached(child, 2147483647, 39779)
	child.scan()
	for i in range(5): child.poll(); await process_frame
	expect(child.record.data.is_empty() and not child.record.pending, "dead PID record remains pending")
	expect(not FileAccess.file_exists(child.RECORD_PATH) and not FileAccess.file_exists(log), "dead record/log not discarded")
	expect(not child.error.is_empty(), "dead record has no final state")
	child.forget()
	var server := TCPServer.new(); expect(server.listen(0, "127.0.0.1") == OK, "fixture port failed")
	var port := server.get_local_port(); server.stop()
	child = Child.new(); log = cached(child, OS.get_process_id(), port); child.scan()
	var deadline := Time.get_ticks_msec() + 18000
	while not child.record.data.is_empty() and Time.get_ticks_msec() < deadline:
		child.poll(); await create_timer(0.01).timeout
	expect(child.record.data.is_empty() and not child.record.pending, "failed probes are unbounded")
	expect(not FileAccess.file_exists(child.RECORD_PATH) and not FileAccess.file_exists(log), "exhausted record/log retained")
	child.forget()
	expect(server.listen(port, "127.0.0.1") == OK, "unrelated listener failed")
	child = Child.new(); log = cached(child, OS.get_process_id(), port); child.scan()
	var peers: Array[StreamPeerTCP] = []
	var hits := 0
	deadline = Time.get_ticks_msec() + 2500
	while Time.get_ticks_msec() < deadline:
		child.poll()
		if server.is_connection_available(): peers.append(server.take_connection())
		for peer in peers:
			peer.poll()
			if peer.get_status() == StreamPeerTCP.STATUS_CONNECTED and peer.get_available_bytes() > 0:
				peer.get_utf8_string(peer.get_available_bytes()); hits += 1
				peer.put_data("HTTP/1.1 200 OK\r\nContent-Length: 2\r\n\r\n{}".to_utf8_buffer())
		await create_timer(0.01).timeout
	expect(hits == 1, "unrelated service was repeatedly probed: " + str(hits))
	expect(child.record.data.is_empty() and not child.record.pending, "invalid-proof record still tracked")
	expect(not FileAccess.file_exists(child.RECORD_PATH) and not FileAccess.file_exists(log), "invalid-proof record/log retained")
	expect(not child.error.is_empty(), "invalid-proof record has no final state")
	for peer in peers: peer.disconnect_from_host()
	server.stop(); child.forget(); log_limit()
	if failures == 0: print("DOCK_STATE_PASS: ", checks, " checks; dead/exhausted/invalid-proof records and logs discarded; unrelated service hit once")
	quit(1 if failures > 0 else 0)
