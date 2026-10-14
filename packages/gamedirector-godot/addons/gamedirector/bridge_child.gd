@tool
extends RefCounted
const Record = preload("bridge_record.gd")
const RECORD_PATH = "res://.godot/gamedirector/bridge.json"
var pid := -1
var stopping := false
var endpoint := ""
var error := ""
var exit_code := -1
var recoverable := false
var record := Record.new()
var pipes: Dictionary = {}
var log_path := ""
var offset := 0
var tail := ""
var adopted := false
var action := ""
var partial := ""
var json_text := ""

static func resolve_executable(gd: String) -> String:
	var candidates: Array[String] = []
	if gd.is_absolute_path(): candidates.append(gd)
	elif not gd.contains("/") and not gd.contains("\\"):
		var separator := ";" if OS.get_name() == "Windows" else ":"
		for folder in OS.get_environment("PATH").split(separator, false):
			candidates.append(folder.path_join(gd))
			if OS.get_name() == "Windows" and gd.get_extension().is_empty():
				for ext in OS.get_environment("PATHEXT").split(";", false): candidates.append(folder.path_join(gd + ext))
	for path in candidates:
		if not FileAccess.file_exists(path): continue
		if OS.get_name() != "Windows":
			var test_binary := "/bin/test" if FileAccess.file_exists("/bin/test") else "/usr/bin/test"
			if OS.execute(test_binary, PackedStringArray(["-x", path])) != 0: continue
		return path
	return ""
func running() -> bool:
	return pid > 0 and (record.alive() if adopted else OS.is_process_running(pid))
func scan():
	record.load_record(RECORD_PATH); recoverable = false; error = ""
	if not record.data.is_empty() and not record.alive(): _discard("Previous bridge is no longer running; record discarded.")
	else: record.probe()
func _discard(message: String):
	forget(); error = message
func recover(next_action: String):
	if record.data.is_empty(): return
	action = next_action; error = ""; record.probe(); recoverable = false
func start(gd: String, project: String, scene: String, godot: String, port: int) -> bool:
	if running(): return false
	if pid > 0: forget()
	if not record.data.is_empty(): error = "Previous bridge is still tracked; reuse or stop it before starting another bridge."; return false
	error = ""; endpoint = ""; tail = ""; partial = ""; json_text = ""; offset = 0; exit_code = -1
	gd = resolve_executable(gd)
	if gd.is_empty(): error = "gd not found. Set the absolute <distribution>/bin/gd path (gd.exe on Windows) in Editor Settings → gamedirector/gd_path."; return false
	var directory := ProjectSettings.globalize_path("res://.godot/gamedirector")
	DirAccess.make_dir_recursive_absolute(directory)
	if OS.get_name() != "Windows": FileAccess.set_unix_permissions(directory, 448) # 0700
	# No active/recoverable record can reach here; prune abandoned logs, retaining one diagnosis.
	for name in DirAccess.get_files_at(directory):
		if name.begins_with("bridge-") and name.ends_with(".log") and name != "bridge-last.log": DirAccess.remove_absolute(directory.path_join(name))
	var token := Crypto.new().generate_random_bytes(32).hex_encode() # engine-local: 256-bit dock secret, not an engine protocol limit.
	log_path = directory.path_join("bridge-" + Crypto.new().generate_random_bytes(8).hex_encode() + ".log") # engine-local: nonsecret random filename suffix.
	var log_file := FileAccess.open(log_path, FileAccess.WRITE)
	if log_file == null: error = "Could not create the private bridge log."; return false
	log_file.close()
	if OS.get_name() != "Windows": FileAccess.set_unix_permissions(log_path, 384) # 0600
	var args := PackedStringArray(["godot", "serve", "--project", Record.normalize_project(project), "--stage", scene, "--godot", godot, "--port", str(port), str(record.defaults.logOption), log_path])
	var environment := str(record.defaults.ownerEnvironment)
	var had_token := OS.has_environment(environment)
	var previous_token := OS.get_environment(environment)
	OS.set_environment(environment, token)
	pipes = OS.execute_with_pipe(gd, args, false)
	if had_token: OS.set_environment(environment, previous_token)
	else: OS.unset_environment(environment)
	pid = int(pipes.get("pid", -1)); stopping = false; adopted = false; action = ""; recoverable = false
	if pid <= 0: error = "Could not launch gd; check executable permissions and Editor Settings."; return false
	record.data = {"pid": pid, "port": port, "token": token, "project": Record.normalize_project(project), "scene": scene, "log": log_path}
	var file := FileAccess.open(RECORD_PATH + ".tmp", FileAccess.WRITE)
	file.store_string(JSON.stringify(record.data)); file.close()
	if OS.get_name() != "Windows": FileAccess.set_unix_permissions(ProjectSettings.globalize_path(RECORD_PATH + ".tmp"), 384)
	DirAccess.rename_absolute(ProjectSettings.globalize_path(RECORD_PATH + ".tmp"), ProjectSettings.globalize_path(RECORD_PATH))
	return true
func _output(text: String):
	tail = (tail + text).right(4096)
	var lines := (partial + text).split("\n")
	partial = lines[-1]; lines.remove_at(lines.size() - 1)
	for line in lines:
		if line.begins_with("{"): json_text = line + "\n"
		elif not json_text.is_empty(): json_text += line + "\n"
		if json_text.is_empty(): continue
		var parser := JSON.new()
		if parser.parse(json_text) == OK:
			if parser.data is Dictionary and parser.data.has("endpoint"): endpoint = str(parser.data.endpoint)
			json_text = ""
		elif json_text.length() > 8192: json_text = ""
func poll():
	record.poll()
	if not record.pending and not record.data.is_empty() and (pid <= 0 or not action.is_empty()):
		if not record.alive(): _discard("Previous bridge is no longer running; record discarded.")
		elif record.rejected: _discard("Ownership proof was rejected; record discarded. No process was stopped.")
		elif record.exhausted(): _discard("Ownership could not be verified after bounded probes; record discarded. No process was stopped.")
		else:
			recoverable = record.verified
			if not record.verified and action.is_empty() and error.is_empty(): record.probe()
	if not action.is_empty() and not record.pending:
		var requested := action; action = ""
		if record.verified:
			pid = int(record.data.pid); adopted = true; log_path = str(record.data.get("log", "")); offset = 0
			endpoint = "http://" + str(record.defaults.loopbackHost) + ":" + str(int(record.data.port)); recoverable = false
			if requested == "stop": _signal()
		else: error = "Ownership could not be verified; no process was stopped."; stopping = false
	if FileAccess.file_exists(log_path):
		var file := FileAccess.open(log_path, FileAccess.READ)
		if file.get_length() > offset:
			file.seek(offset); _output(file.get_buffer(mini(file.get_length() - offset, 16384)).get_string_from_utf8()); offset = file.get_position()
	for key in ["stdio", "stderr"]:
		if pipes.has(key): _output(pipes[key].get_buffer(4096).get_string_from_utf8())
	if pid > 0 and not running(): exit_code = -1 if adopted else OS.get_process_exit_code(pid)
func stop():
	if not running(): return
	if adopted: stopping = true; recover("stop")
	else: _signal()
func _signal():
	if not running(): stopping = false; return
	stopping = true
	var command := resolve_executable("taskkill" if OS.get_name() == "Windows" else "kill")
	var arguments := PackedStringArray(["/PID", str(pid), "/T", "/F"]) if OS.get_name() == "Windows" else PackedStringArray(["-TERM", str(pid)])
	var result := -1 if command.is_empty() else OS.execute(command, arguments)
	if result != 0:
		stopping = false; error = "Could not stop owned bridge (signal exit " + str(result) + "); ownership remains tracked."
func forget(keep_diagnosis := false):
	# Compare the stored launch secret before removing any cache record (including stale records).
	if FileAccess.file_exists(RECORD_PATH):
		var data = JSON.parse_string(FileAccess.get_file_as_string(RECORD_PATH))
		if data is Dictionary and data.get("token") == record.data.get("token"):
			DirAccess.remove_absolute(ProjectSettings.globalize_path(RECORD_PATH))
	for pipe in pipes.values():
		if pipe is FileAccess: pipe.close()
	var owned_log := str(record.data.get("log", log_path))
	var directory := ProjectSettings.globalize_path("res://.godot/gamedirector")
	# A malformed cache must never delete an arbitrary project/external file.
	if Record.normalize_project(owned_log.get_base_dir()) == Record.normalize_project(directory) and owned_log.get_file().begins_with("bridge-") and owned_log.ends_with(".log") and FileAccess.file_exists(owned_log):
		if keep_diagnosis:
			var last := directory.path_join("bridge-last.log")
			DirAccess.remove_absolute(last)
			DirAccess.rename_absolute(owned_log, last)
		else: DirAccess.remove_absolute(owned_log)
	pipes = {}; pid = -1; stopping = false; adopted = false; action = ""; recoverable = false; log_path = ""; endpoint = ""; record.clear()
func close():
	stop()
	var deadline := Time.get_ticks_msec() + 15000 # engine-local: editor-exit grace for owned supervisor cleanup.
	while (running() or not action.is_empty()) and Time.get_ticks_msec() < deadline:
		poll(); OS.delay_msec(10)
	# Never escalate an adopted PID without a fresh successful ownership proof.
	if running() and (not adopted or record.verified): OS.kill(pid)
	poll()
	if pid > 0 and not running(): forget()
