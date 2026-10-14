@tool
extends RefCounted
# Crash recovery uses a per-launch secret, not a PID/name/port guess.
var data: Dictionary = {}
var verified := false
var pending := false
var client := HTTPClient.new()
var challenge := ""
var body := PackedByteArray()
var sent := false
var deadline := 0
const MAX_PROBE_FAILURES := 3
var failed_probes := 0
var rejected := false
var defaults: Dictionary = JSON.parse_string(FileAccess.get_file_as_string("res://addons/gamedirector/bridge_defaults.json"))
static func normalize_project(project: String) -> String:
	var path := project.replace("\\", "/")
	if path.length() >= 2 and path[1] == ":": path = path[0].to_upper() + path.substr(1)
	while path.ends_with("/") and path.length() > 1 and not (path.length() == 3 and path[1] == ":"):
		path = path.trim_suffix("/")
	return path
func proof(token: String, nonce: String, pid: int, project: String) -> PackedByteArray:
	return Crypto.new().hmac_digest(HashingContext.HASH_SHA256, token.to_utf8_buffer(), (nonce + "\n" + str(pid) + "\n" + normalize_project(project)).to_utf8_buffer())
func load_record(path: String):
	clear()
	if not FileAccess.file_exists(path): return
	var parsed = JSON.parse_string(FileAccess.get_file_as_string(path))
	if not parsed is Dictionary: return
	for key in ["pid", "port", "token", "project"]:
		if not parsed.has(key): return
	if normalize_project(str(parsed.project)) != normalize_project(ProjectSettings.globalize_path("res://")): return
	parsed.project = normalize_project(str(parsed.project))
	if int(parsed.pid) <= 0 or int(parsed.port) <= 0 or int(parsed.port) > 65535: return
	if str(parsed.token).is_empty(): return
	data = parsed
func alive() -> bool:
	if data.is_empty(): return false
	var output := []
	if OS.get_name() == "Windows":
		OS.execute("tasklist", PackedStringArray(["/FI", "PID eq " + str(int(data.pid)), "/FO", "CSV", "/NH"]), output, true)
		return str(output).contains('\",\"' + str(int(data.pid)) + '\",')
	# ps handles foreign children and treats zombies as dead; Godot's child API is not enough.
	if OS.execute("ps", PackedStringArray(["-p", str(int(data.pid)), "-o", "stat="]), output, true) != 0: return false
	var state := str(output[0]).strip_edges() if not output.is_empty() else ""
	return not state.is_empty() and not state.begins_with("Z")
func clear():
	data = {}; verified = false; pending = false; rejected = false; failed_probes = 0; client.close()
func exhausted() -> bool: return failed_probes >= MAX_PROBE_FAILURES
func _failed():
	failed_probes += 1; pending = false; verified = false; client.close()
func probe():
	verified = false; pending = false; sent = false; body.clear(); client.close()
	if data.is_empty() or rejected or exhausted(): return
	challenge = Crypto.new().generate_random_bytes(16).hex_encode()
	if client.connect_to_host(defaults.loopbackHost, int(data.port)) != OK: _failed(); return
	deadline = Time.get_ticks_msec() + 5000
	pending = true
func poll():
	if not pending: return
	client.poll()
	var state := client.get_status()
	if Time.get_ticks_msec() >= deadline or state in [HTTPClient.STATUS_CANT_CONNECT, HTTPClient.STATUS_CANT_RESOLVE, HTTPClient.STATUS_CONNECTION_ERROR, HTTPClient.STATUS_DISCONNECTED]:
		if sent and client.has_response(): _finish()
		else: _failed()
		return
	if state == HTTPClient.STATUS_CONNECTED and not sent:
		sent = true
		if client.request(HTTPClient.METHOD_GET, "/" + str(defaults.ownerRoute) + "?challenge=" + challenge + "&proof=" + proof(str(data.token), challenge, int(data.pid), str(data.project)).hex_encode(), PackedStringArray()) != OK:
			_failed()
	elif state == HTTPClient.STATUS_BODY:
		body.append_array(client.read_response_body_chunk())
		if body.size() > 4096: rejected = true; pending = false; client.close()
	elif state == HTTPClient.STATUS_CONNECTED and sent: _finish()
func _finish():
	pending = false
	if client.get_response_code() == 200:
		var parser := JSON.new()
		if parser.parse(body.get_string_from_utf8()) == OK and parser.data is Dictionary:
			var reply: Dictionary = parser.data
			var received := str(reply.get("proof", ""))
			verified = int(reply.get("pid", -1)) == int(data.pid) and normalize_project(str(reply.get("project", ""))) == normalize_project(str(data.project)) and received.length() == 64 and received.is_valid_hex_number(false) and Crypto.new().constant_time_compare(received.hex_decode(), proof(str(data.token), challenge, int(data.pid), str(data.project)))
	rejected = not verified # A responding port without proof is terminal, never re-probe it.
	if verified: failed_probes = 0
	client.close()
