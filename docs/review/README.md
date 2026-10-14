# Composition reviews

`gd review <take-directory> [--safe-area tiktok]` prints JSON. `--safe-area` accepts
any name in Core's `SafeArea.Presets`, or custom numeric l,r,t,b. Unknown names
exit 2 and list the known presets; `gd grammar` generates their names and values. It follows
`job.NextAttempt` exactly as resume does (a pointer without a child job is unused),
checks stored input hashes and the receipt against the latest job, and judges only
`Verified`. It is read-only. Exit 0 means judged with no errors; exit 1 means an
error or a non-Verified job; invalid flags/insets exit 2. A rejected or stale
receipt is never substituted for the latest attempt.

Three reports one `composition.roles` row per subject, `with` role and distinct
lookAt. `rect` is the unclamped viewport AABB of the eight world-bounds corners;
`crown` projects the bounds' top-centre, not the highest projected corner. Bounds
use **role-bounds-v1**: union the visible subtree's world AABBs; invisible ancestors
hide descendants. Rigid Mesh uses local geometry bounds; SkinnedMesh uses every
posed `getVertexPosition`; InstancedMesh uses per-instance matrices; BatchedMesh
uses active, visible instances and geometry vertex ranges (sparse ids included).
All missing bounds are computed into locals, never into box/sphere caches. Sprite
uses its camera-facing world quad, including scale, centre, rotation and size
attenuation. **Line and Points are excluded.** No renderable is `empty`; a missing
or hidden role is `absent`. Computation exceptions yield `error` with `message`,
never a failed take. `behind` means every corner depth <= 0; otherwise a corner
at/before nearClip gives `straddles-near`, with no rect. A crown on the camera
plane has no finite projection and is omitted.

**Aim uses each engine's legacy bounds; reviews use role-bounds-v1.** In particular,
Three's skinned aim can use a cached box and hidden children, while Unity's legacy
aim bakes posed vertices. This divergence is deliberately not fixed here: changing
aim changes pixels. Unity remains centre-only until Unity implements per-role review. Reviews run after render
and write neither geometry nor object boundingBox/boundingSphere caches.

Core's public `CompositionPolicy.Default` (not EngineContract) owns the thresholds;
`CompositionReview.Evaluate` accepts an optional policy:
- `lost`: subject/with behind or entirely outside the frame is an error; lost lookAt
  is a warning. Absent/despawned roles do not fail reviews.
- `side-cut`: when `with` exists, protected subject/with sides beyond 0.005 are
  errors, as are protected bounds straddling nearClip. A single subject's lateral
  crop is never an error: warn if pan/tilt/roll is authored or overflow exceeds
  25% of rect width; ordinary closeup shoulder crop is silent.
- `head-cut`: crown beyond the frame by more than 0.005 is **warning only** before
  contain. With no targetHeight and coverage > 1, annotate `framed-by-centre`;
  a default medium auto-frame is intentionally in this category.
- `unsafe`: optional safe-area crown violations or group sides beyond its edges
  by more than 0.01 are **warnings only**, never errors. The **Core-owned** `SafeArea.Presets["tiktok"]` entry has left,
  right, top, bottom insets `.1111,.1111,.0682,.1911`; use `--safe-area tiktok`, not
  a copied tuple.
- Legacy reports without roles give warnings only, judged from the subject centre.
  Bounds errors also warn; a review cannot certify geometry it could not measure.
- An undecodable/non-finite sample gives `malformed-sample` warning (frame index
  when known); other samples are still judged. Newtonsoft's "NaN" strings are not
  valid geometric evidence.

Cadence remains **1 second**, counted from timeline frame zero and only for
rendered frames. Crops between samples and occlusion can be missed. Inspect actual
footage/contact sheets. This is a checker, not automatic fitting: contain is
planned. Author/inspect both 960x540 and 540x960; camera conformance runs once per
stage because its rows already own their aspect.

## Exact-frame measurement

`gd measure timeline.json --frames 27-31,36 --out <new-directory> --endpoint <url>
--fps 6 --width 960 --height 540` prints the saved `measure.json` as JSON.
Frame indices are zero-based timeline indices; N, inclusive A-B and comma lists
are sorted and deduplicated. Malformed syntax exits 2 before file/bridge I/O.
Range checks need a reachable, stamped bridge and an admitted duration; an index
past the end exits 2 and names the last valid index. Zero/oversize durations use
the take admission refusal, not a frame-list error. Frames before an opening
camera shot are refused with the first frame that actually shows that cue. An undeclared
`measure=measure-v1` capability prints `measured:false` / `cannot measure here:`
and exits 1 before starting any take. Unity does not declare this surface yet.

Measurement reuses a take: replay every index, render from the minimum requested
frame through the end, and inspect only the flagged rendered frames, after render
and before advance. It keeps only requested PNGs as `frames/NNNNNN.png` (timeline
indices), plus resolved `timeline.json`, `manifest.json`, raw `receipt.json` and
`measure.json`; no encoder is involved. The report contains version/contract,
authored and resolved timeline SHA-256, manifest SHA-256, fps/width/height,
normalized frames and rows. Each row carries canonical frame time, raw world
camera position/right/up/forward, vertical fov/aspect, Core-computed horizonRoll,
the unchanged review composition (role-bounds-v1), and governing-cue provenance:
resolvedCue, authoredCue, t, type, subject and from. Aspect resolution supplies the
index mapping without adding an authored DSL field. Missing/extra/duplicate rows,
non-finite values and engine/FrameClock time disagreements are protocol errors.
Measure reuses Core composition shape/finite/vocabulary validation, but unlike
legacy centre-only review, an active composition must supply subject and roles.
Its subject must equal the governing cue's subject or the receipt is rejected.
The wire composition must carry its active boolean. When composition.active is
false, the engine has dropped its shot (subject/lookAt despawned), the camera
holds its last pose, and `shot` still names the governing cue for that frozen pose.
Failure/cancellation after a returned start receipt uses the existing take/stop
cleanup route and writes failed.json; progress goes to stderr, report JSON to stdout.

`horizon-roll-v1` is degrees(asin(clamp(camera.right dot (0,1,0), -1, 1))).
HorizonRoll measures the horizon tilt the viewer sees and **can differ** from
authored roll—for example, roll combined with tilt, or a pitched base aim combined
with pan. Pitch or pan alone does not force a difference; the Core reference
formula decides. For a level base aim and zero pan, the closed form is
`degrees(asin(cos(radians(tilt))*sin(radians(roll))))`.
It is positive in the direction of authored +roll: pan 0 / pitch 0 / roll +10
measures +10; pitch 30 / roll +10 measures +8.64916510528758, not +10.

**What it does not do yet:** no solver, automatic restaging/fitting, artistic
thresholds, notes, occlusion/segmentation proof, named-part/head measurements or
MCP measurement tool. Figure bounds and crowns are not visible-pixel counts.

`node tools/test_three_measure.mjs` witnesses the declared whole-basis samples,
fixed wire schema, render-time on/off equivalence and subject-despawn semantics.

`node tools/test_three_roles.mjs [--three <three-directory>]` (or `GD_THREE_MODULE`)
uses one selected THREE instance; it defaults to the package pin. It covers morph
bounds independently of cache state, sparse batches with strict deleted-id accessors,
never-aimed per-geometry lazy caches, renamed contract vocabulary, reference crowns/rects, current skins,
hidden/instanced/batched/sprite bounds, box/sphere cache purity and non-throwing
probes. `node tools/test_format_stage.mjs --out /tmp/<new-report>` runs the live
box/skin stage at 640x360,360x640,1080x1920,960x540,540x960, checks the portrait
bottom row, evaluates single/pair verdicts and removes media after recording
hashes/reports. Neither engine's camera.shot pose path changes.

## Measured regression baseline

`gd expect-record timeline.json --out timeline.expect.json --endpoint URL --fps 6
--sizes 960x540,540x960 --beats label=frame,... --work <new-directory>` records a
version-1 sidecar via the same measure transaction, once per size.
Only the beat list is human-authored. Omitted beats reuse an existing output's
beats; for a new output they are empty. Omitted sizes are 960x540,540x960; fps
uses the existing film default, so pass `--fps 6` for the 6 fps sample films.
Sidecars are versioned data and may live next to timelines; media workdirs still
obey the existing output policy and must be new/outside game source.

Core selects first/middle/last showing frames for every resolved governing shot
interval (not the shot's authored duration), plus beats, deduplicated and sorted.
Middle is floor((first+last)/2). Coincident `auto` and `beat` labels are lists so
none are lost; before-first-shot beats refuse. `shot` retains authored cue/from.
Metrics are declared only in Core's `RegressionMetrics`, not the engine contract:
role rect height/centre, unclamped minimum margin, crown headroom, exact state,
world camera position/vertical fov and Core horizon roll. Figure height includes
native pixels, rounded to nearest with midpoint away from zero, as information;
comparison uses the fraction, not its rounded pixel display. Headroom is
`1-crown.y`: y increases from the viewport bottom, and the crown is the existing
RoleReviewSpec.Crown / RoleReviewContract.cs bounds-top-center, not rect top or a
named anatomical head. A role without a rect records state only.

Fractions, degrees and world units use absolute **regression** tolerance 1e-6;
state is exact. Position residual is the maximum absolute component delta;
other numeric residuals are absolute deltas. SwiftShader's output is byte-
deterministic; these tolerances absorb only last-bit libm/platform variation,
not aesthetic latitude. Missing/new roles or metrics are breaches, not skipped.
The sidecar stores stable metric/role order and formatting, no paths/timestamps
from the workdir, making an unchanged re-record byte-identical.

`gd expect-check timeline.json --expect timeline.expect.json --endpoint URL
--work <new-directory>` emits `{passed,timelineChanged,breaches}`; each breach
names size/frame, beat/auto labels, recorded authored cue/from, role where
applicable, metric, expected/actual, residual and tolerance. Source hash changes
are informational and still compared. A changed fps, size set or derived frame
list is a usage refusal (exit 2), not an artistic breach. Exit 0 means no numeric/
state breaches; 1 means breaches or inability to measure. Unsupported bridges
use the same `cannot measure here` refusal as the measure transaction. This is CLI-only, no MCP tool.

After an **intended** edit, inspect the measured breaches and review the changed
picture first; then re-run expect-record with the original fps/sizes and approved
beats and review the sidecar diff before accepting it. Do not blindly re-record
away a regression. A changed shot timing may require that intentional re-record
because it changes the auto frame list. This baseline is **not an aesthetic
check**, solver, note/intent vocabulary, occlusion test or replacement for sign-off.
