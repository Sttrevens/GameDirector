# Changelog

## 0.1.0 — first public release

GameDirector lets an AI agent direct your game. The agent writes a timeline from the game's own manifest, the engine shoots it frame by frame, and plain-language notes produce a revised cut. Every take comes with receipts: the timeline, the manifest, a hash for every frame, and the encoded video.

### Engines
- **Unity 2022.3+:** a package with a bundled runtime. Use **Create Independent Example** to try it in seconds, the **Director Workbench** to direct and shoot, and **Copy MCP Config** to hand the set to your agent. It covers offline takes and edits, interactive play/stop/capture, and the Film Inspector.
- **Godot 4.6+ (tested on 4.6.2, standard and .NET):** a pure-GDScript addon with an editor dock. The dock lists the scene's roles, clips and locations, starts and stops the bridge, and copies the MCP config. Takes run in fresh frame-stepped workers, with per-character framing review, exact-frame measurement and expectation checks. No C# project is needed.
- **Three.js:** a Node bridge plus a small adapter. You get fresh-browser takes, seeded hooks, declared GL backends, per-character framing review, exact-frame measurement and expectation sidecars.

### Tools
- **One self-contained download per platform** (Windows x64, macOS Apple silicon and Intel, Linux x64). `bin/` holds `gd`, the MCP server and the Workbench, sharing one runtime. You do not need .NET, Node or Python.
- **`gd` commands:** validate, take, review, measure, edit, expect-record / expect-check, doctor, and engine contract export.
- **MCP server** for Claude Code, Cursor, Codex and other hosts. It refuses capabilities an engine does not have, quickly and clearly.
- **Workbench:** a local production UI with film documents, notes and revisions, sound and captions, and resumable takes. It is in English, with Simplified Chinese chosen automatically on Chinese systems.

### Samples
Stardeck (Three.js and Godot, open-licensed assets), Sandring (procedural Three.js), and contract-coverage timelines.

### Coming next
Unreal Engine, one-line installs (npm, Godot Asset Library, OpenUPM), frame measurement for Unity, and voice and lip sync.

See [installation](docs/install.md), [Godot](docs/godot.md), [Three.js](packages/gamedirector-three/README.md), [directing](docs/directing-playbook.md) and [review](docs/review/README.md).
