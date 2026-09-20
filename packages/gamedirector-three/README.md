# GameDirector Three engine

This is an offline presentation interpreter, not a game runtime. The .NET Core owns
DSL validation and cue ordering; this engine owns the frame clock, camera, role motion,
render target and capture transaction. A small game adapter supplies real visuals.

An adapter directory contains `manifest.json` and `adapter.js`. The default export has
`create() -> {scene, roles: Map<string,Object3D>}`, `animate(actor, {clip,time,worldTime,
speed,delta,fade})`, and optional `evaluate(world,worldTime)`, `spawn(world,role)` and
`dispose(world)`. Construction must be deterministic and independent of live game
services; animations must use supplied time and restore pose axes before sampling.
`spawn` requires explicit capability `actor.spawn: supported`. Advertise only supplied
roles, clips and camera capabilities. Unknown bindings and unsupported audio/fades
fail before capture. See adapters/sandring for a real procedural example.

Run `npm ci` and `npx playwright install chromium`, then `node server.mjs --game-root
<game> --adapter <adapter> --cli <gd.dll>`. Three defaults to <game>/node_modules/three;
--three-root and --browser can override it. Loopback endpoint defaults to port 39778.
Source identities include assets/dependencies and the browser version. Restart after
engine source changes. A new take creates a fresh page; no live game is borrowed.

Run tools/test_three_bridge.py and tools/verify_repeat.py against an idle offline
bridge to verify identity isolation, same-time cuts, retry bytes and fresh performances.
