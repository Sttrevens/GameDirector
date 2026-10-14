extends Logger
# Godot script errors do not throw to a GDScript caller. Translate actual native/script/shader faults to the wire.
var mutex := Mutex.new()
var failure := ""

func _log_error(function: String, file: String, line: int, code: String, rationale: String, _editor_notify: bool, error_type: int, _script_backtraces: Array[ScriptBacktrace]):
	if error_type == Logger.ERROR_TYPE_WARNING: return
	mutex.lock()
	failure = file + ":" + str(line) + " " + function + ": " + (rationale if not rationale.is_empty() else code)
	mutex.unlock()

func take_failure() -> String:
	mutex.lock()
	var result := failure; failure = ""
	mutex.unlock()
	return result
