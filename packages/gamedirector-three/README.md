# GameDirector Three engine

This is an offline presentation interpreter, not a game runtime. The .NET Core owns
DSL validation, cue ordering, take admission and the exported capture/camera
semantics; this engine owns the frame clock, camera kernels, role motion, render target and
capture transaction. A small game adapter supplies real visuals.

An adapter directory contains `manifest.json` and `adapter.js`. The default export has
`create(ctx) -> {scene, roles: Map<string,Object3D>}`, `animate(actor, {clip,time,worldTime,
speed,delta,fade,moved,arrivedAt})`, and optional `evaluate(world,worldTime)`, `spawn(world,role,ctx)` and
`dispose(world)`. Construction must be deterministic and independent of live game
services; animations must use supplied time and restore pose axes before sampling.
A role with no anim cue is passed `clip: null` — the adapter then applies its own rest
pose. Implement `spawn` only when the adapter supports it and declare the
`actor.spawn` capability accordingly; its undeclared behavior is defined by the
contract row, not a blanket restriction. Advertise only supplied roles, clips and
camera capabilities. See adapters/sandring for a real
procedural example.

At startup the bridge runs `gd contract --json` once, validates schema/version and
implementation coverage before launching Playwright or listening, and serves the exact
stdout bytes at `GET /contract`. It implements EngineContract 1.2: the major must match
and the minor must be at least 2; check the claimed hor-plus, role-bounds-v1 and
bounds-top-center ids, not unknown future fit/review ids. The stage reads
graph paths (`camera`, `review`, `measure`, `bridge`, `take`, `frameClock`, `capture`);
the CLI no longer exports the retired flat keys. Camera defaults, rig values, cadence, bridge limits,
budgets, routes, states and engine capability stamps come from this graph. Local
renderer/initial-pose choices and immutable kernels remain engine-owned.
Take admission is likewise the compiler's: the bridge calls
`gd compile --take --fps --width --height --from-frame` and executes its verdict and
frameCount. It also checks the live source fingerprint and the envelope's contract
version; version skew uses `bridge.errors.internal`, naming a bridge restart. The bridge injects
`camera.params` (shot-params.mjs) into the manifest it serves, so an authored param
this runtime does not implement (e.g. `shake`) is a compile error, not a silent no-op.
Camera semantics (pan/tilt/roll signs, offsets, distance, coverage, endFov) mirror the
canonical Unity rig and are pinned by the Node characterization and live camera tests.
Unregistered shot types still hold. Unknown ease ids/formulas throw when a shot is
applied. Positive `orbitDeg` now carries the camera toward its own left, matching
Unity and the contract. **Orbit footage made with earlier releases is
mirrored relative to the new behavior.** The 98 earlier characterization rows and
six Hor+ rows remain covered; the other camera arithmetic is unchanged.
Static assets are served through a general MIME table (js, json, images, gltf/glb,
ktx2, wasm, bin).

Run `npm ci` and `npx playwright install chromium`, then `node server.mjs --game-root
<game> --adapter <adapter> --cli <gd.dll>`; `--browser chrome` uses an installed
browser instead of the bundled chromium. Three defaults to <game>/node_modules/three;
--three-root can override it. The loopback host/alias and default port come from
`bridge.loopbackHost`, `bridge.loopbackAlias` and `bridge.ports.three`; requests need
the exact loopback Host header, non-GET browser origins are rejected, and POST bodies are application/json counted in bytes
with an inclusive `bridge.limits.maxRequestBytes` ceiling. The pending-request limit
is `bridge.limits.maxQueuedRequests`. Compile, stage-load and
stage-ready budgets also come from `bridge`. Source identities include
assets/dependencies, engine sources, the browser version and SHA-256 of the exact
contract stdout. Restart after engine source changes or a rebuilt CLI contract. A
new take launches a fresh browser process, loads the stage and creates a fresh world;
no live game or earlier take's GPU process is borrowed.
`GET /take` returns the receipt, events and composition reviews (legacy subject
centre plus per-role viewport rects/crowns) on `review.intervalSeconds` cadence
from timeline frame 0. Reviews follow render and never write box/sphere caches.
Aim uses each engine's legacy bounds; reviews use role-bounds-v1. See the
[review README](../../docs/review/README.md) for bounds kinds, warning/error policy,
`gd review`, safe-area warnings and cadence limits.
`captureFromFrame` and `advanceOnly` defaults come from `take.requestDefaults`;
the receipt echoes captureFromFrame alongside the total timeline `frameCount`. Send every
index in order: before capture, `advanceOnly: true` returns an empty 204 and steps
without rendering; captured requests return a PNG and then step. Only captured
frames get composition reviews. Retrying the last index returns the same result
without stepping again. See [the replay-only contract](../../docs/production.md#replay-only-pre-roll).

Contract 1.2 adds the public inspection stamp `measure=measure-v1` and
`gd measure timeline.json --frames N,A-B,... --out <new-directory>`. On flagged
rendered take frames, Three reports raw world camera pose/fov/aspect and exactly
the review composition, after render and before advance. GET /take and /status
keep these in `measures`, separate from cadence reviews. Core owns horizon roll
and authored/resolved shot provenance; only requested PNGs are retained, without
encoding. The flag defaults false and is invalid on advance-only frames. This
read-only stamp does not change manifest vocabulary identity or rendered pixels:
catalogs and decision evidence stay valid; the take cache still keys on the full
manifest. Wire names follow the review precedent, witnessed against the declared
schema by Core and Three tests. `node tools/test_three_measure.mjs` also checks
whole-basis samples, on/off render-time state equality and frozen poses on despawn;
see [measurement limits](../../docs/review/README.md#exact-frame-measurement).

Run tools/test_three_bridge.py and tools/verify_repeat.py against an idle offline
bridge to verify identity isolation, same-time cuts, retry bytes and fresh
performances, and tools/test_camera_conformance.py to verify the camera signs.
Run tools/test_three_conformance.mjs and tools/test_three_contract.mjs after a Debug
CLI build to check contract characterization, graph mutations and startup refusal;
tools/test_bridge_admission.mjs checks the real server with Chromium stubbed.
`python3 tools/test_engine_contract_reads.py` prevents retired engine literals;
its `--self-test` probes every rule and the reasoned line exemptions. New engines
start with [the implementer guide](../../docs/contract/README.md), not copied defaults.

## Hermetic takes

A fresh document/context is not a fresh GPU process. Bridge restarts on a dev Mac
reproduced a TypeScript stage's cold-start digest, and a different warm second-take
digest (960×540, 6 fps). Disabling GPU program caching changed nothing;
Sandring's 204 baseline frames were unaffected. Prior-take GPU state can leak.

Startup probes the browser version and declared GL renderer, then closes that browser before listening.
Each admitted take owns a **fresh browser process/profile**, with the same
channel/args, DPR/touch/motion pins and console/page-error handlers. Version skew
refuses with the existing internal-error kind, naming a bridge restart. Stderr
logs `take <id> browser pid=<pid> version=<v>` immediately after the CDP PID read,
before page creation, so failed starts retain their audit witness. Once measured,
`take <id> gl=<renderer>` records the GL renderer separately.

Completion, frame/finish failure, cancellation, lease expiry, partial start
failure and SIGINT/SIGTERM close the take browser in `finally`. `gd.end()` is a
renderer hint, never a gate: Playwright's evaluate has no timeout, so it races a
2 s engine-local bound, a failed start closes its process directly without it,
and a completed take keeps its final PNG and Completed state even if disposal
fails (logged as `take cleanup:`). CDP session/process-info and page creation
share the launch/navigation load deadline. Cleanup/lease/request ownership is
serialized; shutdown prevents queued launches and waits for in-flight start
ownership. At most one take browser lives. Playwright removes its profile. SDK
close rejection retains ownership and blocks replacement; failed shutdown
reports cleanup and can be retried with another signal, never intentionally
orphaning the owned process. Cached last PNG/events/reviews still support
completed retries and later stops without a live page.

Playwright's own SIGINT/SIGTERM/SIGHUP handlers are disabled on every launch
(probe and takes); the bridge alone owns signal-driven shutdown, so a signal
cannot target-close a browser out from under an in-flight start. Shutdown keeps
the HTTP listener open until the serialized queue drains — an in-flight start's
stage and module fetches still resolve — while new POSTs are refused busy by the
shutdown guards (before admission, after admission, immediately before launch,
and before the take is published); only then does the listener close and the
process exit 0. CDP session detach is hygiene: bounded on success, fire-and-forget
on a failed read, so a stalled CDP channel can never wedge take/start.

This excludes earlier takes' browser/GPU-process state, not arbitrary adapter
nondeterminism or cross-machine differences. Cost: cold launch/profile/shaders
per take, with no prewarming or look change. Measured median take/start rose
0.231→0.656 s (Sandring), three samples.
Launch/navigation share `bridge.budgets.pageLoadSeconds`; compile, readiness and
the client's take/start budgets stay unchanged. Re-measure on the target machine.

`node tools/test_three_hermetic.mjs` uses the real bridge/Core/queue and existing
TS adapter with a GPU-free Playwright-boundary fixture. Actual child PIDs/profile
dirs prove distinct processes, every terminal cleanup path, version refusal,
shutdown/start races and cached retries. It runs in CI without Chrome/GPU.

## Declared GL backend

A stage may declare `"gl": "default"` or `"gl": "swiftshader"` in `three.json`.
Omission means `default`: the platform's normal WebGL path. Legacy raw-JS
adapters (including Sandring) stay on that path. Unknown values refuse before
launch/listen. The single engine-owned table in `gl-backends.mjs` defines names,
Chromium args and expectations; this is not a Core/contract-JSON setting.

Hermetic processes alone cannot eliminate GPU arithmetic noise. Probes show that
on some platform GPUs a TypeScript stage can differ by **1/255** in rare frames,
despite a fresh process every time. Visually imperceptible noise still breaks exact
identity. Declare `gl: swiftshader` for pixel-hash baselines that need to remove
platform GPU dependence; do not switch stable stages just for uniformity.
Browser/software versions must still be pinned and adapters must be deterministic.
Pixels are not promised byte-equal across backends. No rounding or tolerance
relaxation is used for exact identity.

`--use-angle=swiftshader` selects SwiftShader. The backend table also spells out
`--enable-unsafe-swiftshader`, but that flag is **not a SwiftShader-only opt-in**:
pinned playwright-core **1.62.1** already adds it unconditionally to Chromium's
default args in `packages/gamedirector-three/node_modules/playwright-core/lib/coreBundle.js:43066`
(`_innerDefaultArgs`, before appending caller args). A Playwright supplied via
`GAMEDIRECTOR_NODE_MODULES` does the same.
Thus **every launch**, including `gl: default` and the startup probe, inherits
permission for software WebGL without Chromium's untrusted-content safety guarantee.

Every stage must remain audited, **local game code only, no remote content**.
Opted-in bundles enforce closed external imports plus the `forbid` closure and
resolved `alias` boundary to exclude live game/services/UI code; legacy raw-JS
adapters must respect the same offline/trusted-content policy. Do not load
remote/untrusted content or reuse this flag for general browsing. These bundle
boundaries are not a security sandbox for malicious local JS; runtime network
allow-lists remain deferred.

Before listening, a page in the probe browser creates WebGL2 and reads
`WEBGL_debug_renderer_info.UNMASKED_RENDERER_WEBGL`. SwiftShader must report a
string matching `/SwiftShader/`, or startup refuses naming backend/actual renderer.
The probe closes in `finally`; launch, page creation and renderer read share one
`pageLoadSeconds` deadline. Every take launches with the same declared args.
`waitForFunction`, `begin()` and the actual GL read share one `stageReadySeconds`
deadline, using only its remaining time rather than starting a new window; the
compiler/load/readiness phases stay within the existing 30+30+30 s budgets and
100 s client start timeout. After `begin()` the read measures **the actual Three
renderer's context**. A string
different from startup refuses with internal error, naming **restart the bridge**,
and closes the failed take browser. Startup, per-take and presenter logs record
that renderer. Bundled fingerprints include both the resolved declaration and
startup renderer string. Legacy fingerprint inputs gain no explicit GL fields;
the changed engine-source digest still changes their value. Only the existing
fingerprint capability changes in `/manifest`, so masking it preserves the bytes.

`test_three_hermetic.mjs` covers args on probe/every take, bad declarations,
startup expectation mismatch, renderer-skew/fault/block cleanup and shared-deadline
exhaustion (including startup launch). Faulted starts retain their early PID log. The live fixture
`--variant swiftshader` uses an owned copy of `look` with `gl` declared and proves
actual SwiftShader selection and two fresh-process takes with identical PNG hashes.

## Opt-in TypeScript games (`three.json`)

Without `three.json`, raw `adapter.js` serving and the existing source-hash
composition have no new GL inputs; the resolved Playwright version joins the
fingerprint and engine-source changes still alter its value. The TS path dynamically loads `bundle.mjs` only when the stage's
adapter directory has `three.json`. esbuild **0.25.12** is installed/pinned in this
engine package, never resolved through `GAMEDIRECTOR_NODE_MODULES`; Playwright may
still resolve there. Use `npm ci --ignore-scripts --prefix packages/gamedirector-three`.
The lock includes optional native esbuild packages for Darwin arm64 and Linux x64.

A stage keeps `manifest.json` and remains the `/adapter/` static root. A family
holds shared TS code/shims and contains its stages. Example (stage `family/stage`, from `tools/fixtures/three-ts-adapter/stage/three.json`):

```json
{"family":"..","entry":"adapter.ts","gameSha":"1111111111111111111111111111111111111111","three":"0.170.0","stage":{"query":"fixture=ts"},"alias":{"src/device.ts":"shims/device.ts"},"define":{"import.meta.env.DEV":"false"},"forbid":["src/device.ts","src/forbidden.ts"],"options":{"side":1}}
```

| Field | Meaning |
| --- | --- |
| `family` | Relative to the stage; default `.`. Must contain the stage. This is the adapter identity scope. |
| `entry` | Required, family-relative entry inside that root. |
| `gameSha` | Required, exactly `.gamedirector-provenance.json`'s `sha`. Provision with `tools/provision_three_game.py`; snapshots live in `~/.gamedirector-dev/games/<name>/<sha>`. The bridge never provisions or reads the game's lockfile. |
| `three` | Required, exactly `threeRoot/package.json`'s version; `--three-root` still overrides the game-local root. Engine-used exports, including `PCFSoftShadowMap`, must exist. No fallback export is substituted. |
| `gl` | Optional `default` (platform WebGL) or `swiftshader` (deterministic software GL). Default `default`; measured renderer joins bundled identity. See [Declared GL backend](#declared-gl-backend). |
| `stage.query` | Optional string, default empty. Included in identity and appended to `/stage?<query>` for each fresh take. |
| `alias` | Optional map from resolved game-relative files to family-relative shims. Handles extensionless/relative imports too. Only the shim is a bundle input; the original need not pass `forbid`. Paths must stay inside their roots. |
| `define` | Optional esbuild expression-string map. |
| `forbid` | Optional list of root-relative file/directory prefixes or globs (`*`, `**`, `?`); optionally `game:`/`family:` labelled. Leading `!` makes an exception; last matching rule wins. `**` must be a complete path segment. Absolute/traversing paths, empty segments, unsupported bracket/brace syntax and control characters refuse as malformed patterns. Applied after aliases. |
| `options` | Optional JSON object delivered unchanged in adapter ctx; default `{}`. |

Top-level keys are closed to the fields in the table; `stage` accepts only
`query`. Typos (e.g. `forbids` or `qurey`) refuse naming the key.

`game:src/...` imports resolve inside the snapshot. Esbuild explicitly uses
`bundle:true`, ESM, browser, es2023, no splitting, a JSON loader, `define`,
`tsconfigRaw` and a metafile. The only externals are `three`,
`three/addons/…` and `three/examples/jsm/…` (no path traversal/query/fragment).
Resolver externals such as `https://`, `http://` and `//` refuse; a second
post-build guard walks **every metafile input's imports**, including unused ones.
Native realpaths, containment inside the selected threeRoot, SHA-256 matches
against its build files, and nearest package name `three` detect second THREE
copies (including vendored copies/case variants). Inputs outside the
snapshot/family, forbidden inputs, provenance/version/export mismatches and an
esbuild/pin mismatch refuse **after contract admission and before any browser
launch or listener**. Each refusal names its cause.

### Compilation config and byte snapshots

The explicit config is a **flat family `tsconfig.json`**, if present; otherwise
it is the **snapshot's `tsconfig.json`**. Without either, explicit empty compiler
options disable implicit TS config discovery. JSONC comments/trailing commas are
supported. Flatten an `extends` chain into a family config; an unflattened chain
refuses rather than silently ignoring inherited options. `baseUrl` must be
root-relative, is resolved against the selected config's root, and is labelled
in identity. No new `three.json` field is needed.

Package/tsconfig/jsconfig metadata inside the roots and on their ancestor chains
is snapshotted before resolution, hashed and re-statted; the metadata set is
re-scanned after the build. Ancestor metadata is included rather than refused
because real checkouts commonly have a `package.json` above the family and snapshot roots.
Outside rows use `metadata-game` / `metadata-family` labels and ordered existing
ancestor contexts (`ancestor-N/<name>`), not absolute paths or empty-directory
depths. Parent `browser`, `imports` or `sideEffects` edits change identity and
require a restart. No ambient TS config overrides the explicit config.

Each input is read once by the byte-snapshot/onLoad pipeline with its proper
JS/JSX/TS/TSX/JSON loader. Identity hashes the **exact bytes supplied to esbuild**,
not a later disk read. Family/Three/config/provenance files are statted before
reading and again after the build; read-time or build-time changes refuse with
the source name. Digests/stat caches therefore cannot describe newer bytes than
the compiled bundle. Retained take-time caches contain stats, not source buffers.
The bundle stays in memory at `/adapter/adapter.js`. The opted-in import map
maps both `three/examples/jsm/` and `three/addons/` to the same vendor directory;
`begin()` requires `world.scene instanceof THREE.Scene` (one THREE instance).
On this path `/game/src/` is **not mounted**: non-literal dynamic imports cannot
load unchecked source from disk. Asset mounts wait for a later release's identity-carrying
mounts. The raw-JS/default path keeps its existing `/game/src/` mount.

### Additive adapter ctx and move reports

`create(ctx)` and `spawn(world, role, ctx)` receive the same
`ctx = {manifest, options}`. Use `manifest.roles[].bindingHint` for casting and
`manifest.locations[].space` for stage knowledge; existing JS adapters can ignore
ctx. `animate` additionally receives `moved` (metres travelled in this call, zero
when idle) and `arrivedAt` (world time of arrival within this call, otherwise
`null`). Arrival is `worldTime - delta + remainingDistance / moveSpeed`, not the
frame boundary. It is reported once, including zero-distance arrivals; cue splits
and timescale use world delta. The existing `speed` becomes zero on arrival as
before; adapters needing substeps use the new fields to apportion motion exactly.

### Opted-in identity

| Included in SHA-256 | Excluded |
| --- | --- |
| Label-relative engine file digest and exact contract stdout digest | Absolute root paths |
| Browser and resolved Playwright versions; declared GL backend and startup unmasked renderer | File mtimes |
| Three version and label-relative file digest | Raw bundle bytes (including path comments) |
| All family files, including stages/manifests/config/shims | Provenance `repo`, node/npm audit metadata |
| Metafile inputs as `(root label, relative path, sha256)`, including JSON outside `src` | Unused snapshot code/assets |
| Explicit config and consulted local/ancestor resolution metadata rows | Absolute ancestor paths/empty ancestor directory depths |
| Esbuild version, explicit build options, label-relative entry/alias/baseUrl paths | Other repositories |
| Stage query and provenance `{sha, tree}` | |

Digests are computed once at startup. At take/start only cached source files are
re-statted (including size, nanosecond mtime/ctime and inode/device). Every visited
family/Three/engine directory is cached too: added/deleted files change its stat
and the next take/start returns a named directory conflict, e.g.
`family:stage/` or `three:build/`. Directory mtimes are not fingerprint data.
A change returns the contract's state-conflict status (409):
`bundle input changed since startup: <label:path>; restart the bridge`.
There is no rebuild inside take/start. `bundle.mjs` exposes `buildCount()`,
incremented at every real esbuild invocation (including failed builds). Opted-in
`GET /health` includes `bundleBuildCount`; raw-JS health bytes are unchanged.
The edit tests change an emitted string literal and assert the named 409,
unchanged served bytes **and unchanged actual invocation count**, rather than
counting a startup log message. Stderr records startup provenance/build and
`bundle stat <ms> ms; rebuild=false` per check. Moving either root or touching a
file before a restart does not change identity; touching during a session still
requires a restart because the stat cache cannot distinguish it from an edit.

`node tools/test_three_bundle.mjs` covers bundling, refusals, portable identity,
ctx/spawn/Scene and exact arrival at 6/24 fps. `test_bridge_admission.mjs` proves
refusals launch no browser/open no port and input edits return a named 409 without
navigation/rebuild. The synthetic live fixture is `tools/fixtures/three-ts-game`
with `tools/fixtures/three-ts-adapter/stage`; no real game is required.

## Deterministic hook streams and rig helpers

The first module script installs `envelope.mjs` synchronously (no top-level await,
no `addInitScript`) before adapter/THREE modules execute. Each document owns a
page stream seeded with FNV-1a(`"page"`) and persistent xorshift32 streams seeded
with FNV-1a of each hook key: `create`, `spawn:<id>`, `role:<id>` (animate),
`evaluate`, and `look`. A zero hash becomes 1. There is **no global/per-take seed**;
the same take shows the same cosmetic blinks. Other keys, their order and render
allocations cannot advance a role's stream. `scoped(key, fn)` restores the caller's
`Math.random` in `finally`, including nested scopes and throws. Unscoped module
and render-time draws use the page stream. Hooks are synchronous; async readiness,
clock virtualisation, timer refusal and network allow-lists remain deferred.
Without an installed envelope (Node consumers/tests), hooks call through directly.
`window.gd.pageStreamDrawCount` exposes the unscoped draw count for conformance.

The browser is explicitly pinned to DPR 1, no touch, non-mobile, and
`reducedMotion: 'no-preference'`. Console warnings/errors go to bridge stderr.

`/engine/helpers.mjs` exports `fixedSteps(state, worldTime, hz, eps, fn)`:
`state.step` starts at 0; each newly reached integer tick calls `fn(1/hz, tick/hz)`.
`eps` is in seconds. Repeating a time does not repeat a step; rewind throws, so
use fresh state for each rig/performance. `segmentAt(segments, t)` selects a
half-open `{start,end,...}` segment; an omitted `end` holds, and gaps/before-first
return null. Raw JS adapters can import this module. Bundled adapters use
`window.gd.helpers` without introducing a non-THREE external import; the closed
external policy and absent `/game/src/` mount stay intact.

## Optional presenter

After renderer creation the engine calls `look(renderer, {width,height,world})`
inside the `look` RNG scope. Return null/undefined for direct rendering, or
`{render(scene,camera), setSize(width,height), dispose()}`. The engine sizes the
presenter before frame 0. `setAnimationLoop` throws during `look`, and its original
method is restored even on failure. The presenter owns its post targets/passes;
it must restore the default (null) render target after **every** render. The
runtime refuses a bound target or `shadowMap.autoUpdate !== true`, including on
the direct path. After presenter setup and every rendered frame it measures
`getDrawingBufferSize`, canvas backing width/height (when present), and
`getPixelRatio`; every available size must equal the requested take size at pixel
ratio 1. A mismatch fails the take naming expected/measured sizes and ratio.
Presenter disposal precedes adapter/world/renderer disposal. Custom Node renderer
seams may omit WebGL state inspection; unavailable measurements stay undefined,
never fall back to requested values. Real browser renderers always supply it. There is no engine-owned post chain, reference page or look-parity tool.

`begin()` returns facts; bridge stderr logs once per take:
`presenter post=<on|off> size=W×H rtNull=true shadowAuto=true exposure=<actual exposure> pixelRatio=1 canvas=W×H gl=<actual renderer>`.
Size, canvas backing size and pixel ratio are measured, not echoed from `begin()`.
`post` means a non-null presenter. Game adapters must refuse unsupported requested
post and assert their own expected exposure against game look data; the engine
reports the renderer's actual exposure rather than restating game values.

`picture_diff.mjs <a.png> <b.png>` decodes RGBA through ffmpeg and prints share of
pixels with any channel |Δ| > 2, mean absolute channel delta and max delta.
`test_fps_agreement.mjs --endpoint … --timeline … --fps 6 --fps 24 --width W --height H`
captures fresh takes and checks every shared timestamp: **share ≤ 0.005 AND mean
≤ 0.5**. Failure exits 1 and names the first failing time; `--out /tmp/<report>`
retains metrics only, never take/media directories. Sandring results are informational.

Run `node tools/test_three_envelope.mjs`, `node tools/test_three_presentation.mjs`,
`node tools/test_picture_diff.mjs` and `node tools/test_fps_agreement_unit.mjs` for
Node/self-check gates. `node tools/test_three_fixtures.mjs --variant rand|look|walk|swiftshader
--out /tmp/<new-report>` runs a single Chrome bridge on 39880 and deletes its
media after recording hashes/metrics. `rand` proves full/from7 tail equality and
unequal pre-frame7 page draw counts with a real shadow allocation; `look` uses a
HalfFloat target/fullscreen quad and checks stage query; `walk` blends visibly
from walk to idle at the exact off-grid 2.1 s arrival using 60 Hz helper steps.
`--variant look --probe resize` copies the fixture family into its owned report
directory and selects `resize.ts`, whose look hook resizes the renderer to
1280×720. A 320×180 take must fail start/frame with a named HTTP 500 size mismatch.
The original TypeScript fixture remains unchanged.
