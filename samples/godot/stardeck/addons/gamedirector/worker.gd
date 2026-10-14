extends Node
const LENGTH_BYTES = 4 # engine-local: big-endian u32 transport prefix width, not a bridge budget.
const Runtime = preload("runtime.gd")
const Manifest = preload("manifest.gd")
const Ownership = preload("ownership.gd")
const ErrorLog = preload("error_log.gd")
var errors: Logger
var peer := StreamPeerTCP.new()
var token := ""
var max_bytes := 0
var buffer := PackedByteArray()
var greeted := false
var authenticated := false
var graph := {}
var bindings := {}
var packed: PackedScene
var runtime: RefCounted
var next_frame := 0
var after_replay := false

func _ready():
	errors = ErrorLog.new(); OS.add_logger(errors)
	process_mode = Node.PROCESS_MODE_ALWAYS; get_tree().paused = true
	var args := OS.get_cmdline_user_args()
	var options := {}
	for i in range(0, args.size() - 1, 2): options[args[i]] = args[i + 1]
	if not options.has("--gd-token") or not options.has("--gd-host") or not options.has("--gd-port") or not options.has("--gd-max-bytes"):
		get_tree().quit(1); return
	token = options["--gd-token"]; max_bytes = int(options["--gd-max-bytes"])
	peer.big_endian = true
	if peer.connect_to_host(options["--gd-host"], int(options["--gd-port"])) != OK: get_tree().quit(1)

func _process(_delta: float):
	peer.poll()
	if peer.get_status() == StreamPeerTCP.STATUS_ERROR or (greeted and peer.get_status() == StreamPeerTCP.STATUS_NONE):
		if runtime != null: runtime.end()
		get_tree().quit(); return
	if peer.get_status() != StreamPeerTCP.STATUS_CONNECTED: return
	if not greeted:
		peer.set_no_delay(true); _send({"token": token}); greeted = true
	var available := peer.get_available_bytes()
	if available > 0:
		var packet := peer.get_data(available)
		if packet[0] != OK: get_tree().quit(1); return
		buffer.append_array(packet[1])
	while buffer.size() >= LENGTH_BYTES:
		var size := 0
		for byte in buffer.slice(0, LENGTH_BYTES): size = (size << 8) | int(byte) # engine-local: bits per transport byte.
		if size <= 0 or size > max_bytes: get_tree().quit(1); return
		if buffer.size() < size + LENGTH_BYTES: return
		var message = JSON.parse_string(buffer.slice(LENGTH_BYTES, size + LENGTH_BYTES).get_string_from_utf8())
		buffer = buffer.slice(size + LENGTH_BYTES)
		if not message is Dictionary: get_tree().quit(1); return
		if not authenticated: _configure(message)
		else: _command(message)

func _configure(message: Dictionary):
	# Refuse an unauthenticated supervisor before loading any project scene.
	if message.get("token") != token: get_tree().quit(1); return
	graph = message.contract; bindings = message.bindings
	if graph.bridge.limits.maxRequestBytes != max_bytes: get_tree().quit(1); return
	authenticated = true
	var why: String = Ownership.new().inspect_path(message.stage)
	if not why.is_empty(): _error(why); return
	packed = load(message.stage)
	if packed == null: _error("missing PackedScene: " + str(message.stage)); return
	_send({"facts": {"engineVersion": Engine.get_version_info().string,
		"renderer": RenderingServer.get_current_rendering_method(), "driver": RenderingServer.get_current_rendering_driver_name(),
		"device": RenderingServer.get_video_adapter_name(), "vendor": RenderingServer.get_video_adapter_vendor()}})

func _error(why: String):
	_send({"error": why, "code": graph.bridge.errors.stateConflict.code})

func _command(message: Dictionary):
	if message.op == "inspect":
		var audit := Ownership.new()
		var why: String = audit.inspect_source(packed)
		if not why.is_empty(): _error(why); return
		var stage: Node = packed.instantiate()
		# Metadata inspection never enters the scene tree. Clock admission belongs to begin, after Core admission.
		var inspection := Manifest.new()
		var manifest: Dictionary = inspection.inspect(stage, graph, bindings)
		stage.free()
		if not inspection.error.is_empty(): _error(inspection.error); return
		_send({"manifest": manifest}); return
	if message.op == "begin":
		if runtime == null: runtime = Runtime.new()
		var why: String = runtime.begin(self, packed, graph, bindings, message.compiled, int(message.width), int(message.height))
		if not why.is_empty(): _error(why); return
		next_frame = 0; after_replay = false; _send({"ok": true}); return
	if message.op == "frame":
		if runtime == null or int(message.index) != next_frame: _error("worker frame index mismatch"); return
		var reply := {}; var png := PackedByteArray()
		if message.advanceOnly: after_replay = true
		else:
			png = runtime.draw(after_replay); after_replay = false
			reply.pose = runtime.rig.pose(next_frame, runtime.time)
			var review_due: bool = next_frame % (int(message.fps) * int(graph.review.intervalSeconds)) == 0
			if review_due or message.get(graph.measure.flag, false):
				var composition: Dictionary = runtime.rig.composition()
				if review_due: reply.review = {"frame": next_frame, "time": runtime.time, "composition": composition}
				if message.get(graph.measure.flag, false): reply.measure = runtime.rig.measure(next_frame, runtime.time, composition)
		runtime.advance_to(minf(runtime.compiled.duration, message.nextTime))
		next_frame += 1; _send(reply, png); return
	if message.op == "finish":
		runtime.advance_to(runtime.compiled.duration); _send({"events": runtime.events}); return
	_error("unknown worker operation")

func _send(message: Dictionary, png := PackedByteArray()):
	if not graph.is_empty():
		var failure: String = errors.take_failure()
		if not failure.is_empty(): message = {"error": failure, "code": graph.bridge.errors.internal.code}; png = PackedByteArray()
	message.pngBytes = png.size()
	var bytes := JSON.stringify(message).to_utf8_buffer()
	peer.put_u32(bytes.size())
	if peer.put_data(bytes) != OK or (not png.is_empty() and peer.put_data(png) != OK): get_tree().quit(1)
