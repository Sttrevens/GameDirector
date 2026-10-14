# Timeline DSL v0.1

A timeline is JSON authored by an agent, reviewed by a person, validated by the
compiler, and executed deterministically by the player. `gd grammar` / MCP
`gd_grammar` print the same Core-generated reference for vocabulary, defaults,
parameters, capabilities and diagnostics. Use that reference rather than copying
its tables into a second implementation.

## Top-level structure

```jsonc
{
  "version": "0.1",           // Must match exactly; otherwise GD1000
  "id": "stardeck_demo",     // Required; otherwise GD1010
  "title": "Human-readable title",
  "metadata": { "note": "Arbitrary metadata; does not affect execution" },
  "cues": [ ... ]
}
```

## Cues (time in seconds)

| type | Fields | Meaning |
|---|---|---|
| `camera.shot` | `shot: ShotSpec, aspect?: {min?,max?}` | Start a shot; `durationSeconds=0` lasts until the next shot cue |
| `actor.spawn` | `role, location, headingTo?` | Spawn a role at a manifest location |
| `actor.despawn` | `role` | Remove a role |
| `actor.anim` | `role, clip, fade?` | Crossfade to a bound clip |
| `actor.move` | `role, to, speed?, headingTo?` | Move toward a location; the adapter owns locomotion |
| `actor.face` | `role, headingTo` | Face a role or location |
| `audio.play` | `audioId, volume? 0..1` | Play manifest audio |
| `audio.stop` | `audioId` | Stop audio |
| `world.timescale` | `scale, duration?` | Scale presentation time; positive duration injects an explicit restore cue |
| `marker` | `label` | Editorial beat or review annotation |

### ShotSpec

```jsonc
{
  "type": "shot vocabulary ID from gd grammar / ShotVocabulary.Shots",
  "subject": "required role ID",
  "with": ["other protected role IDs"], // Optional; review only, does not move the camera
  "frame": "framing ID from gd grammar / ShotVocabulary.Frames",
  "from": "location ID or camera.currentAnchor from gd contract --json",
  "to": "end location ID for a moving shot",
  "durationSeconds": 4.0,
  "fov": 35.0,                 // Optional; mutually exclusive with focalLengthMm
  "focalLengthMm": 35.0,       // Optional; conversion and sensor are in camera.lens
  "ease": "easing ID from gd grammar / ShotVocabulary.Eases",
  "lookAt": "optional alternate role to aim at",
  "params": { "shake": 0.4, "orbitDeg": 120.0 } // Declared numeric parameters only
}
```

`gd grammar` and `gd contract --json` → `camera.params[]` define parameter defaults,
units, derivations and screen-space semantics. An adapter's `camera.params`
capability is its complete authoritative parameter set: it may narrow support or
register custom parameters for custom shots. Built-in names retain their meaning
across engines. Unknown names fail compilation (GD1012), never silently disappear.

The exported `camera` graph is authoritative; Unity and Three camera kernels are
checked against its samples:

- Motion and easing: `camera.shots`, `camera.motions`, `camera.eases`. The `orbitDeg`
  parameter row supplies the default angle and direction.
- `frame` selects automatic subject-height coverage; `camera.frames` and
  `camera.fallbackFrame` define values and fallback. Explicit `from` locations
  take precedence. Lens and automatic framing formulas are in `camera.lens` and
  `camera.rig`.
- Aiming uses the geometric bounds center. Without an enabled non-particle
  Renderer, Unity's `CinematicCameraRig.BoundsOf` substitutes a 1×1×1 box centered
  1m above the subject pivot. Three uses `Box3.setFromObject`; empty geometry has
  an empty box whose `getCenter` is the world origin. These fallback behaviors
  are not unified.

### One timeline, multiple aspect ratios

A `camera.shot` may include `aspect:{min?,max?}`, selected by `width/height` in the
half-open range `[min,max)`. Omitted minimum is zero; omitted maximum is infinity.
Alternatives at the same `t` must be contiguous in authored order, partition all
positive aspect ratios, and have matching duration and cue-level event `role` /
`label`. No unhinted camera cue may share that time. Square output selects
`min:1`. All alternatives validate their shot/location/role presence, including
unselected ones. Only the selected cue contributes to windows, duration and
compiled output; its `aspect` is removed. Violations are GD1013.

```json
{"t":0,"type":"camera.shot","aspect":{"min":1},"shot":{"subject":"legion","with":["minotaur"],"from":"wide","durationSeconds":3}},
{"t":0,"type":"camera.shot","aspect":{"max":1},"shot":{"subject":"legion","from":"near-legion","durationSeconds":3}}
```

Hinted `gd validate` / `gd compile` (including `--take`) require both `--width` and
`--height`; missing either is GD1013, not an implicit landscape choice. MCP
`gd_validate_timeline` likewise requires both dimensions together. `gd take`
resolves against capture dimensions and saves/sends the resolved timeline for
resume. `job.AuthoredTimelineSha256` records original authored bytes only when
resolving hints; `TimelineSha256` hashes the saved resolved file. Unhinted paths
retain their original object and bytes. Dimensionless `gd play` / MCP
`gd_play_timeline` reject hints in the client; Unity `PlayFromJson` also rejects
them through Core.

After a hint group, a camera cue reading `current` in `from` or `to` (including
omitted `from`) needs an earlier unhinted shot with explicit start and any authored
end location. It must precede the reader by more than
`FrameClock.CueEpsilonSeconds`. A same-time cut or subject/lookAt despawn does not
remove that dependency; GD1013 prevents the previous aspect's camera position from
leaking. Alternatives preserve duration, frame count, opening-shot and overlap
verdicts. The event-order guarantee concerns Three's `{t,type,role,label}`, not
Unity's index/shot summaries.

`shot.with` IDs must be known, distinct and different from the subject (unknown:
GD1001; duplicate/subject: GD1009). Under vertical/Hor+ framing they are review-only.
A protected role may spawn or despawn within the shot window; only never being
present in that window is GD1011. The half-open window ends at the next cut or a
positive duration; zero duration lasts to the next cut/timeline end. Presence at
the opening is not required by this release. Do not add an over-the-shoulder
foreground role to `with` unless it should be protected.

`camera.lens.aspect: hor-plus` means `fov` is always vertical:
`tan(hfov/2)=tan(fov/2)*width/height`. It does not automatically pull back or fit.
EngineContract 1.1 added this member, `review.roles` vocabulary/reference samples,
and optional `composition.roles` review. Three reports per-role findings.
`gd review <take-directory> [--safe-area tiktok]` reviews only the newest Verified
attempt. Lost roles and protected-group side/near-plane cuts are errors; head cuts
are warnings (without targetHeight, coverage>1 is annotated framed-by-centre).
Single-subject side cuts are not errors; safe-area findings are warnings. The
TikTok preset and custom l,r,t,b values use Core policy; thresholds, tolerances and
sampling cadence are in `gd grammar` and [review policy](review/README.md).
Legacy center-only reports warn; transient cuts between samples may be missed.
Aiming retains each engine's legacy bounds; review uses role-bounds-v1. Contain
fitting and FilmPlan variants remain planned.

The 1.0 golden contract is frozen. The `compile --take` admission envelope's
`contract` is the full current Core version; bridges compare it to their startup
export and require restart after a mismatching rebuild. Compiled content bytes
remain unchanged; only that envelope version field may advance.

## Compiler rules

**Errors block playback:** version mismatch GD1000; unknown role GD1001; unknown
clip GD1002; unknown location GD1003; unknown shot GD1004; invalid time GD1005;
unknown cue GD1006; unknown framing GD1007; unknown audio GD1008; invalid numbers
GD1009; missing ID GD1010; operations on absent roles GD1011; unsupported adapter
capabilities/parameters GD1012; aspect grouping/dimensions/current dependency GD1013.

**Capabilities:** `gd grammar` / `gd contract --json` → `capabilities[]` define types,
values, declaring owners, behavior when omitted and compiler rules. Omission does
not universally mean unrestricted. Invalid declared values are manifest errors.
Unregistered extension keys remain accepted and identity-bearing but uninterpreted.

**Warnings permit playback with review:** overlapping shots (later one takes over)
GD2001; empty marker GD2003. GD2002 is reserved/retired; role-presence failures are
errors (GD1011), not acceptable warnings.

**Injection:** a timed `world.timescale` inserts an `injected=true`, scale=1 restore
at `t+duration`. Compiled output is the complete event list.

**Ordering:** stable by `t`, authored order for ties; injected restores come last
at equal time.

## Compatibility

- DSL v0.1 is additive: new semantics use new cue types or optional ShotSpec fields,
  not changed meanings for existing fields.
- Breaking changes require a `version` bump and matching compiler `SupportedVersion`.
- Manifest and sample timeline assets are contract fixtures: `SampleAssetTests`
  ensure they remain parseable and compile without errors.

## Production semantics

- Compilation snapshots nested payloads without modifying authored data. Manifest
  IDs are unique and lists are closed vocabularies. Explicit null/empty endpoints,
  nonfinite numbers and undeclared clips fail validation; playback has the same verdict.
- Timescale is a presentation channel. New authored scales cancel pending earlier
  restores, including equal-time boundaries. Motion/animation share that scale;
  camera and edit beats remain in timeline seconds.
- Optional session adapters receive begin / boundary-split advance / end.
  Exceptions produce `Failed`; successful dispatch records authored `Time`
  separately from `DispatchedAt`.
- Explicit anchors override `frame`. With `from:current`, subject bounds and lens
  derive distance; `params.distance` overrides it. `focalLengthMm` uses
  `camera.lens.sensorSizeMm` and is mutually exclusive with `fov`. These parameters
  are shot-relative artistic controls, not additional scene truth.
- PNG capture is silent picture. Soundtrack/effects belong on the edit timeline;
  frame-stepped takes reject live audio cues. Interactive playback supports bound
  AudioClips by ID.

Frame-stepped takes sample frame i at `FrameClock.TimeOf(i,fps)`, never accumulated
`1/fps` steps. Exported `frameClock.alignmentToleranceFrames` and
`frameClock.cueEpsilonSeconds` govern validated shot boundaries before cue/edit
generation. Unity's stage must be static outside leased role/participant trees,
the rig-owned camera or explicit adapter exemptions. Preflight lists and refuses
autonomous clocks through `GameDirectorAdapterBase.ClockProbes`; see
[architecture](architecture.md). Optional Video/VFX modules use type-name probes.
`IsStageClockExempt(Component)` declares owned/static clocks;
`IsAutonomousClock(Component)` extends the table. Custom unscaled-time scripts
and shader `_Time` visuals require explicit ownership, not merely frozen
`timeScale`. Save borrowed Animators disabled; the lease enables them.
Explicit particle seeds remain scene truth; only automatic seeds become stable
path-derived seeds.
