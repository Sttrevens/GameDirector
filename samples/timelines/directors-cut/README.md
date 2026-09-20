# 别停机 / KEEP ROLLING

54-second CDREBIRTH machinima, 1920 × 1080 at 24 fps. A streamer performs for an oversized audience; a speaker joins; performance becomes threat; the camera keeps rolling. All characters and animation come from the game. Twelve shots use restrained titles, a night stage, practical warm/cool lighting, a moving camera and a brief slow-motion climax.

## Reproduce

Build `GameDirector.sln`. In CDREBIRTH open the saved
`Assets/Scenes/GameDirector/GD_DirectorsCut_Sandbox.unity` and enter Play.
The director stage owns offline presentation; the bridge refuses any existing
Fusion Runner. Do not compile or change scenes during a take.

From the GameDirector repository, choose a fresh workspace, copy `edit.json`
and `assets/titles.ass` here into it, keeping the `assets/` subdirectory.
For the following example the workspace is `captures/edit/film-new`:

```bash
dotnet run --project src/GameDirector.Cli -- take samples/timelines/directors-cut/01_the_audience.json --out captures/edit/film-new/take-01_the_audience --fps 24 --width 1920 --height 1080
dotnet run --project src/GameDirector.Cli -- take samples/timelines/directors-cut/02_the_sound.json --out captures/edit/film-new/take-02_the_sound --fps 24 --width 1920 --height 1080
dotnet run --project src/GameDirector.Cli -- take samples/timelines/directors-cut/03_keep_rolling.json --out captures/edit/film-new/take-03_keep_rolling-v2 --fps 24 --width 1920 --height 1080
python3 samples/timelines/directors-cut/build_soundtrack.py ../CDREBIRTH captures/edit/film-new
dotnet run --project src/GameDirector.Cli -- edit captures/edit/film-new/edit.json --out captures/edit/film-new/delivery
```

Captioned edits require FFmpeg with libass. `tools/setup_media_tools.sh` can
install an isolated optional runtime; set `GAMEDIRECTOR_FFMPEG` to its printed
path. The film uses macOS Songti SC and Helvetica Neue. On other platforms,
provide fonts covering the same glyphs and inspect the actual rendered titles.

The soundtrack recipe reads five existing CDREBIRTH audio assets and records
their source paths and the full mix graph. Media stays in the output workspace;
no audio binaries are copied into this sample directory. The edit applies
5 dB of master gain after the mix, preserving its dynamics. Production output
measured approximately −19.6 LUFS / −2.2 dBTP.

Each take and delivery retains frame counts, event receipts, edit reasons and
media hashes. `tools/verify_repeat.py` compares two fresh takes; run against a
short scout before a long recording. `tools/test_media_pipeline.py` exercises
actual source-end cuts and 30→24 fps conversion, with and without captions.

The original Lobby and grimforest sandbox are separate from the film stage.
This is real CDREBIRTH offline capture. A second game/engine, live Host/Client
direction, and human aesthetic preference are separate acceptance lanes.
