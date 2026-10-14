# Godot

GameDirector turns a declared Godot stage and a validated timeline into a frame-stepped film. Godot runs a pure-GDScript worker; the self-contained `gd` application owns admission, receipts, transport and worker lifetime. You do not need Node, Python or a Godot .NET project.

## Install

1. Install Godot 4.6 or newer (tested on 4.6.2) and unpack the full GameDirector release for your platform. Keep the complete shared `bin/` folder and `distribution.json` together: copying only `gd` cannot launch its verified MCP companion. For the dock, use an **explicit absolute executable path** as described below; GUI-launched macOS editors do not inherit your shell PATH. Encoding requires FFmpeg and ffprobe; run `<distribution>/bin/gd doctor` (Windows: `gd.exe`).
2. Extract `gamedirector-godot-<version>.zip` into your project. Its root contains `addons/gamedirector/` and `LICENSE`, the Godot Asset Library layout.
3. Enable **GameDirector** in Project Settings → Plugins. The **GameDirector** dock lists the current scene's roles, clips and locations, with validation errors naming node paths.
4. In **Editor → Editor Settings** (enable Advanced if needed), set `gamedirector/gd_path` to the absolute `<distribution>/bin/gd` executable (`gd.exe` on Windows). Its global file picker and value are per-machine, not committed in `project.godot`. A bare `gd` also works when the editor's own PATH contains it; shell PATH changes alone do not configure an already-open GUI editor. The legacy project-level path migrates only if no editor value exists, then is ignored/removed in memory without silently saving the project. Remove that old entry manually from source control.
5. Optional integer Project Setting `gamedirector/bridge_port` changes the dock's port convention (39779). Legacy floating-point values are normalized to integers. If another process holds the port, the dock refuses to launch and tells you to stop it or choose a different port.

The release archives also contain a complete `samples/godot/stardeck` project, a Three Stardeck adapter, identical original GLBs, and one [asset attribution](../samples/assets/stardeck/ATTRIBUTION.md). These are real files, not checkout links.

## Declare a scene

Use metadata in the Inspector; there is no separate Godot manifest file. Start with a Node3D root:

| Node | Metadata | Meaning |
|---|---|---|
| Stage root | `gamedirector_game` (string), optional `gamedirector_version` | Project identity |
| Actor Node3D | `gamedirector_role` (unique string), `gamedirector_actor` (string) | Role and actor identity |
| Actor Node3D | `gamedirector_present_at_start` (bool, default true) | Initial presence |
| Marker3D | `gamedirector_location` (unique string) | Location and camera anchor |

The actor's child AnimationPlayers supply clip IDs. Set them to **Manual** processing and disable autoplay. Shared actor IDs must have identical clip libraries. Optional actor metadata selects the rest/movement/loop/hold/fade behavior; see the Stardeck scene as an example. Never select behavior by branching on a particular actor or clip name in the addon.

Save the scene, then use **Refresh scene declarations** after metadata edits. **Start bridge** runs one supervisor for this project and the saved current scene. The dock shows **Starting bridge (importing project)**, then **Ready — endpoint** only after the supervisor announces readiness. Startup failure shows its exit code and the last output lines. Logs and the owned PID/port record live under the project's ignored `.godot/gamedirector/` cache; no editor console is required. A user-initiated **Stop bridge** reports **Bridge stopped**, not a startup error. Scene inspection does not sample animations, move actors or change authored nodes at edit time.

Saving a scene or project resource while the owned bridge runs shows **Scene changed — Restart bridge** and changes the button to **Restart bridge**. Restart is explicit, not automatic, so saving cannot silently interrupt a take. Wait for an active take to finish before requesting restart; then validate and record against the fresh manifest. A stale take/start returns HTTP 409 and names this same dock remedy. Equivalent terminal use: stop and restart `gd godot serve`.

Closing the editor normally stops its owned bridge, including during import. After a crash, the next editor checks the cached record against a fresh challenge proof from that particular supervisor and offers **Reuse owned bridge** or **Stop recovered bridge**. PID, process name, and occupied port alone are never proof; malformed, stale, or unverified records cannot authorize a kill. Reused processes are re-proved before Stop. A leftover process without proof is not stopped automatically; resolve it manually. Recovery during startup waits until ownership can be proved.

Equivalent terminal commands:

```sh
gd godot manifest --project <project> --stage res://stage.tscn --godot <Godot-binary> > manifest.json
gd validate timeline.json --manifest manifest.json --width 960 --height 540
gd godot serve --project <project> --stage res://stage.tscn --godot <Godot-binary> --port 39779
```

The supervisor performs one bounded headless import/readiness phase before printing its endpoint or accepting requests. Warm startup rescans/repairs generated imports. Take workers never invoke the editor/importer. Do not edit source/assets while a supervisor is active: source drift requires restart. CLI `--port 0` selects an ephemeral port. `GAMEDIRECTOR_GODOT` or `godot` on PATH can replace `--godot`.

## Connect an MCP agent

Use **Copy MCP config** at any time, or the same documented `gd mcp` configuration. Copying does not start the bridge, prove readiness, or clear a failure/stale-scene message. With a running owned bridge the copy uses its actual port; otherwise it uses the project setting. Wait for **Ready** and resolve any restart notice before filming:

```json
{
  "mcpServers": {
    "gamedirector": {
      "command": "<distribution>/bin/gd",
      "args": ["mcp"],
      "env": { "GAMEDIRECTOR_ENDPOINT": "http://127.0.0.1:39779" }
    }
  }
}
```

Claude Code and Cursor use `mcpServers`; for Codex map the same command, args and env into `mcp_servers.gamedirector` in TOML. The endpoint is the bridge, not a Workbench address. No invented MCP endpoint CLI flag is needed. Explicit tool `endpoint` arguments override `GAMEDIRECTOR_ENDPOINT`; without either, the legacy Unity endpoint remains the default.

Ask the agent to call `gd_get_manifest`, then `gd_validate_timeline` against that vocabulary before `gd_take`. `gd_status` works. Interactive `gd_play_timeline`, `gd_capture_frame`, and `gd_stop` are unsupported on Godot and return a clear frame-stepped-only error. Managed `gd_studio_*` tools address a separately running authenticated Workbench, not the engine bridge. CLI review/measure/expect work with Godot; they are not standalone MCP tools in this release.

## Make the first film

For the bundled Stardeck project, save this as `timeline.json` outside the project. Its IDs come from the scene declarations:

```json
{
  "version": "0.1", "id": "first-film", "title": "First Godot film",
  "cues": [
    { "t": 0, "type": "actor.anim", "role": "bolt", "clip": "Idle", "fade": 0 },
    { "t": 0, "type": "camera.shot", "shot": {
      "type": "tracking", "subject": "bolt", "frame": "full",
      "from": "cam_low_side", "durationSeconds": 2, "fov": 34,
      "ease": "linear", "params": { "targetHeight": 1.15 }
    } }
  ]
}
```

Fetch the manifest and validate first, then capture and inspect:

```sh
gd take timeline.json --endpoint http://127.0.0.1:39779 --fps 24 --width 960 --height 540 --out first-take
gd review first-take
gd measure timeline.json --endpoint http://127.0.0.1:39779 --frames 0,12,24 --width 960 --height 540 --out first-measure
gd expect-record timeline.json --endpoint http://127.0.0.1:39779 --sizes 960x540,540x960 --out timeline.godot.expect.json
gd expect-check timeline.json --endpoint http://127.0.0.1:39779 --expect timeline.godot.expect.json
```

Output directories must be new and outside project source. Film capture is silent picture; mix audio later with `gd edit`. Pre-roll `--from-frame N` advances earlier timeline indices without drawing/encoding; only captured frame filenames are capture-relative. Reviews and measure frame indices remain timeline-relative.

Review uses read-only posed-mesh role bounds, near-plane states, unclamped rectangle and top-centre crown at the Core cadence. `measure=measure-v1` records world camera pose after render and before advance. Numerical expect baselines do not judge artistic quality. Stardeck's full v2 reports real crop findings (three side-cut errors and two head-cut warnings in the measured Mono run); its numeric baseline is not a clean editorial verdict. Revise framing rather than weakening review policy.

## Limits and clocks

This is an owned stage, not an arbitrary live gameplay adapter. Allowed: Node/Node3D containers, MeshInstance3D, Skeleton3D, passive parent-skeleton BoneAttachment3D, manual AnimationPlayer with pose/morph tracks, Marker3D, Camera3D, WorldEnvironment and static directional/omni/spot lights. Other classes are refused, including particles, physics bodies, Timers, AnimationTree, IK/modifiers, instanced/batched meshes and sprites. Hidden subtrees do not contribute review geometry.

No stage scripts/static initializers, autoloads, shader TIME/includes using TIME, animated textures, auto exposure, autoplay/non-manual animation, physics interpolation or process-through-pause clocks. The addon owns actor animation, camera and timescale. The render preset disables temporal/post effects, shadows and AA. Each take gets a fresh authenticated loopback worker, an explicit SubViewport and bounded first-pose readiness.

Camera parameters are advertised through `camera.params`, matching Three's 14 supported parameters. **Shake is not advertised**, and Unity-specific Perlin shake parity is not claimed. Determinism is engine/build/hardware/renderer-specific, not cross-engine pixel equivalence. The verified platform is **macOS arm64 only**, using standard and Mono builds of Godot 4.6 or newer (tested on 4.6.2), with supervisor captures on Forward+/Metal. Windows and Linux distributions are built but not run. Other Godot versions, GPUs and backends are unverified. The addon has no C# dependency; the standard build is the primary user target.
