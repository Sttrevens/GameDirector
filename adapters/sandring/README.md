# Sandring offline director adapter

Imports the real `sandring-live/src/{arena,fighters,catalog-roster}.js` visual code.
Runs in its own headless page; it does not run sim.js, main.js, live providers,
audience actions, combat settlement or the public viewer UI. The game repo is read-only.

From the GameDirector repo, after `dotnet build`:

```sh
npm ci --prefix packages/gamedirector-three
npx --prefix packages/gamedirector-three playwright install chromium
node packages/gamedirector-three/server.mjs --game-root /path/to/sandring-live --adapter adapters/sandring
# Alternatively use an installed browser with --browser chrome.
dotnet src/GameDirector.Cli/bin/Debug/net8.0/gd.dll doctor --endpoint http://127.0.0.1:39778
dotnet src/GameDirector.Cli/bin/Debug/net8.0/gd.dll take samples/timelines/sandring/timeline.json --endpoint http://127.0.0.1:39778 --out captures/sandring/new-take
```

The game checkout must already have its Three dependency (`npm ci` in that checkout
if needed). For an extracted candidate also pass `--cli /path/to/bin/gd.dll`.
The bridge binds loopback (exact Host header required), serializes requests, limits
its queue and ends abandoned captures. Schema/version/coverage are checked before
browser startup; queue/body limits, time budgets and the idle lease come from
`gd contract --json` (`bridge`, `capture`). Restart after changing engine code or
the CLI contract; the exact export digest is part of source identity. Take
admission is the compiler's decision (`gd compile --take`): this adapter's manifest
declares `actor.spawn: unsupported` and `actor.animation.fade: cut-only`, and the
bridge adds the exact `camera.params` set its runtime implements, so unsupported
spawns, fades and camera params fail at validate time, not at capture. Roles without
an anim cue rest in the adapter's `idle` pose. Sound is added by the same generic
edit engine as Unity.

After a take, `GET /take` includes composition reviews on the contract's
`review.intervalSeconds` cadence, counted from timeline frame 0 and only for
rendered frames. `--from-frame` replays earlier frames without rendering them.
Verified takes retain frame hashes, not PNGs unless `--keep-frames` is set.
Three orbit now matches Unity's camera-relative left direction; orbit footage made
with earlier releases is mirrored relative to current output. The checked-in Sandring
timeline has no orbit shots. To verify the
camera contract screen-space against this adapter, run
`python3 tools/test_camera_conformance.py --endpoint http://127.0.0.1:39778`.

`performances.json` records design sources and reviewed footage timecodes, including
current readability defects. It is pinned to the checked-in manifest; use a fresh
live manifest/catalog for source-aware production. See the [engine contract guide](../../docs/contract/README.md) for engine integration.
