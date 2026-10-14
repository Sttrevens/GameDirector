extends RefCounted
static func check(dock, tree: SceneTree):
	var request := HTTPRequest.new()
	tree.root.add_child(request)
	var timeline := {"version": "0.1", "id": "saved-scene", "cues": [{"t": 0, "type": "camera.shot", "shot": {"type": "lockoff", "subject": "bolt", "from": "cam_low_side", "durationSeconds": 1}}]}
	var body := JSON.stringify({"timeline": timeline, "frameRate": 24, "width": 960, "height": 540})
	assert(request.request(dock.child.endpoint + "/take/start", ["Content-Type: application/json"], HTTPClient.METHOD_POST, body) == OK)
	var reply = await request.request_completed
	assert(reply[1] == 409, "saving scene did not invalidate old supervisor sources")
	assert(reply[3].get_string_from_utf8().contains("Restart bridge"), "409 did not name the dock user's remedy: " + reply[3].get_string_from_utf8())
	request.queue_free()
