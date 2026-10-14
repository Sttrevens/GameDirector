# Stardeck timelines and edit plan

`v1.json` and `v2.json` compile against [the real Stardeck manifest](../../../adapters/stardeck/manifest.json). The Godot expectation sidecar describes the two supported preview sizes.

`edit.json` is an authored edit plan over both takes at 24 fps, 960×540. Capture the revisions separately and place each verified take's `picture.mp4` and `take.json` in `takes/v1/` or `takes/v2/` beside the plan, or update its source paths to your actual take directories. No rendered take is included. The plan selects arrival from v1, greeting from v2, and the dance from v1; boundaries are frame-aligned and within the compiled timelines. `SampleAssetTests` strict-loads the plan and validates it against both compiled take durations; rendering still requires the captured media and external FFmpeg tools.

For authored spawn, audio and time-scale compiler coverage (not engine playback), see [the synthetic contract sample](../../contract/stardeck-cue-coverage/README.md).
