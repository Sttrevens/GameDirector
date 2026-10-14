# Stardeck — Godot stage

Open `project.godot` in Godot 4.6 or newer (tested on 4.6.2). The stage declares Bolt/Pip, 17 clips each and 24 markers through metadata. Shared [timelines](../../timelines/stardeck/) and the [Three adapter](../../../adapters/stardeck/) demonstrate the same directing vocabulary. Real GLBs live in each engine's asset directory and are byte-pinned by `tools/test_godot_assets.py`; [one attribution](../../assets/stardeck/ATTRIBUTION.md) records original licenses/URLs.

```sh
gd godot manifest --project samples/godot/stardeck --stage res://stage.tscn --godot <binary> > manifest.json
gd validate samples/timelines/stardeck/v2.json --manifest manifest.json --width 960 --height 540
gd godot serve --project samples/godot/stardeck --stage res://stage.tscn --godot <binary> --port 39779
gd take samples/timelines/stardeck/v2.json --endpoint http://127.0.0.1:39779 --fps 24 --width 960 --height 540 --out stardeck-take
gd review stardeck-take
```

Use a new output directory outside the source project. Change dimensions to 540×960 for portrait. The opening is a simple first-film example; full v2's close/group framing can generate real crop findings. Numeric `v2.godot.expect.json` is engine-specific, not an aesthetic certificate or Three baseline.

Addon and asset files are portable real copies, not symlinks. Keep generated `.godot`/`.import` ignored. Preview uses Compatibility; macOS supervisor capture pins Forward+/Metal. Rendering/skinning/post effects differ from Three; cross-engine camera agreement is not pixel identity. See [Godot installation, MCP and limits](../../../docs/godot.md).
