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
if needed). For an extracted candidate also pass `--cli /path/to/cli/gd.dll`.
The bridge binds loopback, serializes requests, limits its queue and ends abandoned
captures. Restart it when editing engine code. Procedural animation supports cut-only
transitions (`fade: 0`). Sound is added by the same generic edit engine as Unity.

`performances.json` records design sources and reviewed footage timecodes, including
current readability defects. It is pinned to the checked-in manifest; use a fresh
live manifest/catalog for source-aware production. Read docs/08 for measured evidence.
