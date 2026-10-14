# GameDirector for Godot

Pure-GDScript owned-stage worker and editor addon for Godot 4.6 or newer (tested on 4.6.2). The bundled `gd godot serve` supervisor owns Core admission, receipts, queues and fresh-worker lifetime. `gd godot manifest` exports declarations from scene metadata. No C# project, Node or Python is needed by users.

Install `gamedirector-godot-<version>.zip` at the project root and enable the plugin. The GameDirector dock lists roles/clips/locations, starts/stops an owned bridge and copies MCP config. See [the Godot guide](../../docs/godot.md) for metadata, clocks, exact commands and limits.

Supported: five camera motions/four eases, Three's 14 camera parameters (advertised through `camera.params`, no shake), actor presence/movement/facing/manual pose and morph animation, role-bounds-v1 review, `measure-v1`, expect baselines and replay-only pre-roll. Capture is silent picture, with explicit SubViewport rendering and hardware/build-specific determinism.

Only admitted passive containers/meshes/skeletons/manual pose players/markers/cameras/static lights/environments are allowed. No autoload/gameplay scripts, physics, particles, timers, procedural animation, shader TIME or implicit clocks. Unsupported visual classes are refused, not silently measured. Verified platform: macOS arm64 only, standard and Mono builds of Godot 4.6 or newer (tested on 4.6.2). Windows/Linux are built but not run; other OS/GPU/backend combinations are unverified.

Developer checks accept `--godot <binary>`: editor/dock, startup, takes/pre-roll, camera conformance, manifest parity, negative cases, role review, posed geometry, measure, mutations, MCP and artifact-only installation. Packaging includes stable script `.uid` files and omits generated caches. Core DLL/source are unchanged.
