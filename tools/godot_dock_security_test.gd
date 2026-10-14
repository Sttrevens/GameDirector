extends SceneTree
const Child = preload("res://addons/gamedirector/bridge_child.gd")
var child := Child.new()
var checks := 0
var failures := 0
func _initialize(): call_deferred("run")
func expect(ok: bool, message: String):
	if not ok:
		failures += 1
		push_error(message)
	checks += 1
func run():
	var options: Dictionary = JSON.parse_string(FileAccess.get_file_as_string("res://dock_options.json"))
	if not options.has("capture"):
		print("SKIP: dock secret-capture fixture requires a POSIX executable script; private-file modes are Unix-only.")
		quit(); return
	var environment := str(child.record.defaults.get("ownerEnvironment", "GAMEDIRECTOR_DOCK_OWNER"))
	var had_token := OS.has_environment(environment)
	var old_token := OS.get_environment(environment)
	OS.set_environment(environment, "parent-sentinel")
	expect(child.start(options.capture, ProjectSettings.globalize_path("res://"), "res://stage.tscn", OS.get_executable_path(), options.port), "capture child did not start")
	print("DOCK_OWNED_PID:", child.pid)
	expect(OS.get_environment(environment) == "parent-sentinel", "owner environment was not restored after spawn")
	if had_token: OS.set_environment(environment, old_token)
	else: OS.unset_environment(environment)
	var record: Dictionary = child.record.data
	expect(not child.log_path.contains(str(record.token)), "owner secret appears in the log filename")
	if OS.get_name() != "Windows":
		expect(FileAccess.get_unix_permissions(ProjectSettings.globalize_path(child.RECORD_PATH)) == 384, "record is not 0600")
		expect(FileAccess.get_unix_permissions(child.log_path) == 384, "log is not 0600")
		expect(FileAccess.get_unix_permissions(child.log_path.get_base_dir()) == 448, "owner directory is not 0700")
	var deadline := Time.get_ticks_msec() + 5000
	while not child.tail.contains("captured_token") and Time.get_ticks_msec() < deadline:
		child.poll(); await process_frame
	var captured = JSON.parse_string(child.tail.strip_edges())
	expect(captured is Dictionary, "capture output missing")
	if captured is Dictionary:
		expect(captured.captured_token == record.token, "owner token not inherited through environment")
		expect(not str(captured.argv).contains(str(record.token)), "owner token appears on argv")
	child.close()
	if failures == 0: print("DOCK_SECURITY_PASS: ", checks, " checks (environment-only token, private record/log/directory, nonsecret log name)")
	quit(1 if failures > 0 else 0)
func _finalize(): child.close()
