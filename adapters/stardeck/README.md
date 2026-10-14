# Stardeck — Three adapter

The approved Stardeck presentation adapter and manifest, with its original GLBs. Assets live once here so the existing Three server's adapter-only mount and source fingerprint cover their bytes. Godot references them through the `samples/assets/stardeck` links. Preserve [attribution](../../samples/assets/stardeck/ATTRIBUTION.md).

```sh
node packages/gamedirector-three/server.mjs --game-root <source checkout> \
  --adapter adapters/stardeck --three-root <three package> \
  --cli src/GameDirector.Cli/bin/Release/net8.0/gd.dll --browser chrome --port 39778
# Use the printed endpoint:
gd take samples/timelines/stardeck/v2.json --endpoint http://127.0.0.1:39778 \
  --fps 24 --width 960 --height 540 --out /outside/source/stardeck-three
```

`--game-root` must have a `src/` tree (existing legacy bridge provenance convention). No live game/network/provider calls occur: this adapter loads the local CC0 robot and CC BY 4.0 hallway. Timelines v1/v2 and the Godot version are under `samples/`.
