# EngineContract implementer guide

Start with `gd contract --json`, not a copied defaults table. The complete graph is
`EngineContract.Current`; `engine-contract-1.2.json` is its reviewed golden. The
1.1 golden is frozen: strip `measure`, its capability row and `take.requestDefaults.measure`,
then reset minor to 1. The 1.0 golden is also frozen: from that 1.1 derivation remove
`camera.lens.aspect`, `review.roles` and appended `review.optional[4]`, then reset
minor to 0. Both derivations must deep-equal their goldens. DSL,
manifest, Workbench and MCP versions are separate. Unity reads Core declarations
from its DLL; a non-.NET engine reads the export once at startup.

## 1. Refuse before starting

Check `schema == "gamedirector.engine-contract"`, the implemented major, and
`version.minor >= your minimum minor`. Higher minors still need coverage. Name the
missing version/id/parameter in the refusal; do not open the bridge or renderer.
See `UnityEngineContract.Check` and Three's `contract.mjs::refusal`.

Implement every `camera.shots[].motion`, `camera.motions[].id`,
`camera.eases[].formula`, `camera.progress.id`, `camera.lens.focalToFov.id` and
`camera.rig.autoFrameDistance.id`. Match your parameter claim against
`camera.params[]`: Unity covers the whole built-in table; Three declares its
implemented subset through the manifest's `camera.params` capability. Unknown
formulas must fail, never silently become smoothstep. Unregistered custom shot
types retain the existing hold behavior.

## 2. Camera kernels, not expression evaluation

Dispatch immutable formula/motion ids to engine-native kernels. Row expressions
and meanings explain the math; **samples are the oracle**. Use `camera.scene` for the
fixture; mirror axes if necessary, then measure its camera-relative observables.
Run ease/motion, aim (including roll/order), lens and auto-frame samples at their
specified tolerances. Positive orbit goes toward the camera's own left.

Read `camera.lens`, `camera.rig`, `camera.frames`, `camera.fallbackFrame`,
`camera.currentAnchor` and `camera.params[]` rather than adding literal fallbacks. A parameter has either
`camera.params[].default` or `camera.params[].derivation`; resolve the latter from
`camera.derivations`. Test
omitted versus authored defaults, and that every claimed parameter changes the
pose at a non-default value. Preserve arithmetic order on pixel-sensitive paths.
Changed formula semantics require a new id and samples, not editing an old id.
Initial pose, empty-subject bounds and renderer look are not standardized here.

## 3. Take, review and bridge

Call `gd compile timeline.json --manifest manifest.json --take --fps N --width W
--height H --from-frame I`. Use its admission verdict and **compile-envelope `frameCount`**, not a
local ceiling formula. The admission-envelope `contract` reports Core's full
version (currently `"1.2"`); compare it fully with the startup graph version.
Refuse envelope skew and require a bridge restart. Invalid engine manifests are engine faults,
not caller admission failures.

Read `take.states`, `take.requestDefaults`, `take.silentPictureAudio` and
`capture.idleLeaseSeconds`. Send/accept every timeline index in order; replay-only
indices return empty 204 without rendering. A retry of the last index must not
advance twice. Review only rendered frames satisfying
`i % (fps * review.intervalSeconds) == 0`, counted from timeline frame 0, not from
the capture start. Emit every `review.required` path; optional probes are not
universal artistic acceptance.

Use `bridge.routes`, `bridge.loopbackHost`, `bridge.loopbackAlias`,
`bridge.ports`, `bridge.errors`
(status/code definitions), `bridge.limits` and `bridge.budgets`. Enforce the
inclusive byte ceiling and queue capacity; convert seconds to engine units once.
Health negotiation, route methods/admissibility and per-route deadlines are not
specified by this graph. It does not claim that every engine serves every route.

Three's hermetic takes use a fresh browser/GPU process per take to exclude
prior-take renderer state; this is an engine guarantee, not a new contract field.
Engines must declare how they obtain deterministic pixels (Three pins the declared GL backend and measures its renderer).
Unity still resets its scene/runtime within the running editor/player; an equivalent
fresh-process guarantee is **not yet required or claimed** for Unity.

### Additive 1.1 declarations: aspect and per-role review

`camera.lens.aspect` names `hor-plus` and six generated 9:16 / 16:9 samples in the
existing ViewSample shape. Fov remains vertical; existing focal/coverage formulas
are unchanged. `CameraReference` generates the new samples in double precision;
old aspect-1 literals and auto-frame radii cross-check it independently. These
ViewSample rows narrow to the existing float wire API; libm last-bit drift cannot
change their values (the tests perturb reference results by ±1e-12).

`review.roles` declares fields, subject/with/lookAt kinds, in-front/behind/absent/empty/
straddles-near/error states (errors carry message), `role-bounds-v1`, unclamped corner
rects and the top-centre crown proxy, with
generated box samples. Role sample aspect/rect/crown are rounded to 9 decimal
places only at publication, not in the reference kernel or any engine. This
absorbs platform libm drift; ±1e-12 perturbation tests guard the fixed fixtures'
rounding midpoint margin. Reference assertions allow 1e-9 (published engine
sample tolerance remains 1e-4). `composition.roles` is optional. Runtime role reports and
Three now claims these ids at startup (MinMinor 1); unknown future fit/review ids
do not prevent startup. Unity stays MinMinor 0 until Unity implements per-role review. No camera pose code
changed in 1.1. **Aim uses each engine's legacy bounds; reviews use
role-bounds-v1**: visible rigid/posed skinned/per-instance instanced/active visible
batched renderables and camera-facing Sprite quads; Line and Points excluded.
No box/sphere cache writes; failed probes report error without failing a take.
See [review policy and limitations](../review/README.md).

Aspect hints are Core authoring policy, not new camera.params or capability rows:
validate every alternate, admit at W/H, keep one and strip aspect. Clients store and
send the resolved file. Clients without size refuse hints (GD1013) before play;
compile/validate require both explicit geometry flags. With is nullable/review-only,
allows presence later in the shot window, and is deep-copied by snapshots. Contain,
thresholds and optional safe-area warnings live in Core's composition evaluator,
not this graph. Contain and FilmPlan variants remain separate steps.

### Additive 1.2 declarations: measure

`measure` declares the raw frame/time/world-camera field paths and refers to
`review.roles` / RoleReviewSpec / role-bounds-v1 rather than copying composition
vocabulary. `horizon-roll-v1` belongs to Core, with independent roll/pitch and
pinned authored-index tests. Samples carry right/up/forward, including combined
pan/tilt/roll; the reference shares one lookAt/local Y(-pan), X(-tilt), Z(roll)
basis. Each `measure.samples[]` row pairs `tolerance: 1e-9` for double-precision
reference/Three assertions with `liveTolerance: 2e-2` for float32-engine
observations, following the camera/role sample precedent. Both are absolute
bounds: dimensionless per basis component and degrees for horizonRoll (world
units if comparing position). The tight reference bound absorbs double/libm
last-bit drift; the live bound allows float32 pose/trigonometry error without
loosening reference checks. These are conformance tolerances, not artistic
thresholds or an assertion of native Unity measurement support. Fixed protocol flag/collection/row names follow review's
precedent: the graph declares the schema and Core/Three tests witness both sides,
not a positional path parser. Engines read poses from just-rendered matrices before
advancing; they compute no horizon roll and apply no thresholds. The optional
FrameRequest flag defaults false in `take.requestDefaults.measure`; flagging an
advance-only frame is a 400 invalid_request. Rows live in a separate `measures`
collection on GET /take and /status, never in cadence `reviews`.

Three now requires minor 2 and stamps the single public `measure=measure-v1`
capability; Unity stays MinMinor 0 and does not stamp it. The row is optional,
AdapterOrEngine-declared and `inspection`-identity: a read-only inspection surface;
never changes what a timeline means or how it renders, so catalogs and decision
evidence stay valid; the take cache still keys on the full manifest. NonIdentity is every non-vocabulary row; MachineLocal remains
unchanged. Only the inspection stamp is excluded from vocabulary identity, not
from the full source/pixel identity. See [measurement and its limits](../review/README.md#exact-frame-measurement).

## 4. Capability stamps and identity

Select stamps from `capabilities[]` rows whose `capabilities[].declarer` is `engine` or
`adapter-or-engine`; honor kind, enum values and list separators. Do not invent a
value or claim unsupported parameters. Rows also define undeclared behavior,
compiler rules, identity and exposure. Unregistered keys remain uninterpreted and
identity-bearing. Keep machine-local stamps out of provider-facing evidence as
required by its boundary. Three hashes the exact export stdout into its source
fingerprint; Unity includes the Core MVID. Engine changes invalidate cached pixels.

## 5. Run the gates

From the repo root:
```sh
dotnet build -c Debug
dotnet test -c Release
npm ci --ignore-scripts --prefix packages/gamedirector-three
node tools/test_three_conformance.mjs
node tools/test_three_measure.mjs
node tools/test_three_contract.mjs
node tools/test_bridge_admission.mjs
python3 tools/test_engine_contract_reads.py --self-test
python3 tools/test_engine_contract_reads.py
```
The literal-read lint rejects unused `// engine-local:` markers, including headers.
A nonempty trailing reason may exempt only a line with an actual rule finding;
remove it when the literal is removed. Interpolation expressions are scanned as
code, not hidden inside string text.

For Unity, add `com.gamedirector.unity` to the project's `testables` and run its
EditMode `ContractConformanceTests` and `UnityEngineContractTests`; the owned
`tools/run_unity_validation.py --editor <Unity> --out <new-dir> --editmode-tests`
runner also checks real film production. Against a live idle bridge, run
`tools/test_camera_conformance.py --endpoint <url>` (Unity also needs `--gd
<gd.dll>`) and the take/retry probes in `tools/test_three_bridge.py`. A new engine
must run the same rows through its actual camera and add fresh-render/replay hash
checks. Passing pose tests alone is not a pixel or human-review claim.
