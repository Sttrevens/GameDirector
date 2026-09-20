# CAM DOWN! — animated cinematic PV

The user requested an animated promotional film to test GameDirector's directing
ability. The gameplay-footage edit proposal was explicitly rejected. This film
uses CDREBIRTH's actual models and humanoid/monster animation assets, staged and
captured through the ordinary GameDirector timeline, take and edit tools.

## Dramatic contract

The first shot establishes a physical camera. One friend films while the other
approaches BigGuai. A compliment earns a pose; the partner then insults it.
Irritation becomes rage and a hurricane. The cameraman wants the spectacular shot
before both friends flee. The final exchange confirms they kept the recording.
The game title closes the 48-second film. No invented invisible-camera premise.

This is an authored animation, including synthetic robot dialogue. It illustrates
the game's voice/monster/filming premise; it is not an acceptance test of live
Voice Talk, multiplayer authority, scoring, capture UI or runtime audio recording.

## Reproduce

1. Sync both packages to CDREBIRTH using its `tools/dev/sync_gamedirector.sh`.
2. Open the saved `Assets/Scenes/GameDirector/GD_CAMDOWN_PV_Sandbox.unity`.
   Its cast is two humanoid performers plus `WieldableCamera/camera_new` attached
   to the right hand. The copied controller adds Idle/Walk/Run/IpadIdle/IpadRun
   animation clips. `PvStageBuilder.Configure()` validates committed stages and
   safely replays; it refuses incomplete or occupied output paths.
3. Enter Play mode, validate `timeline.json` against the live manifest.
4. `gd take timeline.json --out <workspace>/take-final --fps 24 --width 1920 --height 1080`
5. `python3 build_soundtrack.py <CDREBIRTH> <workspace>` (macOS stock Chinese voices,
   game music/foley, original electronic camera sounds).
6. Copy `edit.json` and `titles.ass` to the workspace and its `assets/` directory.
7. Use the local full FFmpeg build for libass, then `gd edit edit.json --out <new delivery>`.
8. Inspect rendered cuts and the 33–38 second two-person escape before delivery.

The scene builder is an Editor-only production recipe scoped to this dedicated
scene. It changes copied assets only, preflights output occupancy, validates the
finished bindings, and saves a versioned completion marker with the scene. A failed
build restores this initially clean scene and removes only new recipe outputs.
The saved first stage was inspected and migrated to the completion marker after
its successful initial authoring; no failure rollback was exercised on that scene.

Nested registered props use local attachment snapshots taken before any animation
reset. Only disjoint actor hierarchies are leased; native presentation components
are advanced once. This enables stable repeated takes without a camera prop's
pose drifting when its parent is restored.

Production outputs and receipts are local under `captures/edit/pv/cinematic/`.
The earlier `captures/edit/pv/scout/` gameplay survey is not a source for this film.

Final local output: `captures/edit/pv/cinematic/delivery-v2/final.mp4`,48s,1080p24.
Audio mastering is a two-pass step in `master_soundtrack.py`; the final EDL uses
`assets/mastered.wav` at unity gain. Source takes and prior exports remain intact.
