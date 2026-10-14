extends SceneTree
const Record = preload("res://addons/gamedirector/bridge_record.gd")
var checks := 0
var failures := 0
func _initialize(): call_deferred("run")
func expect(ok: bool, message: String):
	if not ok:
		failures += 1
		push_error(message)
	checks += 1
func proof(token: String, challenge: String, pid: int, project: String) -> String:
	return Crypto.new().hmac_digest(HashingContext.HASH_SHA256, token.to_utf8_buffer(), (challenge + "\n" + str(pid) + "\n" + project).to_utf8_buffer()).hex_encode()
func reply_case(token: String, pid_delta: int, project_suffix: String, wanted: bool):
	var server := TCPServer.new()
	expect(server.listen(0, "127.0.0.1") == OK, "fixture listener failed")
	var record := Record.new()
	var project := ProjectSettings.globalize_path("res://").trim_suffix("/")
	record.data = {"pid": OS.get_process_id(), "port": server.get_local_port(), "token": "fixture-secret", "project": project}
	record.probe()
	var peer: StreamPeerTCP
	var request := ""
	var deadline := Time.get_ticks_msec() + 6000
	var answered := false
	while record.pending and Time.get_ticks_msec() < deadline:
		record.poll()
		if peer == null and server.is_connection_available(): peer = server.take_connection()
		if peer != null:
			peer.poll()
			if peer.get_status() == StreamPeerTCP.STATUS_CONNECTED and peer.get_available_bytes() > 0: request += peer.get_utf8_string(peer.get_available_bytes())
			if not answered and request.contains("\r\n\r\n"):
				# Same PID/project with a wrong-token HMAC proves the proof clause itself matters.
				var pid := OS.get_process_id() + pid_delta
				var answer_project := project + project_suffix
				var body := JSON.stringify({"pid": pid, "project": answer_project, "proof": proof(token, record.challenge, pid, answer_project)})
				peer.put_data(("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: " + str(body.to_utf8_buffer().size()) + "\r\n\r\n" + body).to_utf8_buffer())
				answered = true
		await process_frame
	expect(answered and not record.pending, "live proof fixture timed out")
	expect(record.verified == wanted, "live owner proof acceptance mismatch: token=" + token + " pid_delta=" + str(pid_delta) + " project_suffix=" + project_suffix)
	if peer != null: peer.disconnect_from_host()
	server.stop()
func run():
	await reply_case("wrong-secret", 0, "", false)
	await reply_case("fixture-secret", 1, "", false)
	await reply_case("fixture-secret", 0, "/other", false)
	await reply_case("fixture-secret", 0, "", true)
	for pair in [["c:\\Games\\Sandbox\\", "C:/Games/Sandbox"], ["C:/Games/Sandbox/", "C:/Games/Sandbox"], ["c:\\", "C:/"], ["/home/player/stage/", "/home/player/stage"], ["/", "/"], ["\\\\server\\share\\stage\\", "//server/share/stage"]]:
		expect(Record.normalize_project(pair[0]) == pair[1], "canonical project path mismatch: " + pair[0])
	if failures == 0: print("DOCK_OWNER_PASS: ", checks, " checks (live wrong token/PID/project rejected; genuine HMAC accepted; Windows/POSIX paths)")
	quit(1 if failures > 0 else 0)
