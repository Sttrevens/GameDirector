# Architecture

For Core contributors, engine/adapter implementers, and agents directing a project.

## Three separate responsibilities

1. **Expression:** turn creative intent into a machine-checkable timeline — the DSL.
2. **Validation:** establish that the game can execute it before rendering — the compiler.
3. **Execution:** replay the same timeline and time steps as the same event sequence — the deterministic player and adapter.

Reproducibility, not realtime control, is the goal. An agent authors an offline
timeline; the engine executes it precisely rather than accepting per-frame model
commands with unpredictable latency.

## Layers and repository layout

```
L0 Transport/bridge   packages/com.gamedirector.unity (HTTP, main-thread dispatch)
                      src/GameDirector.Mcp (stdio MCP), src/GameDirector.Cli (gd)
L1 Director DSL       src/GameDirector.Core (no engine or game dependencies)
                      Dsl/         Timeline and manifest POCOs
                      Compilation/ TimelineCompiler
                      Playback/    TimelinePlayer
                      Adapters/    IGameDirectorAdapter
L2 Capabilities       adapters/<game>/manifests/*.json (offline validation)
                      GET /manifest (runtime vocabulary)
L3 Game adapters      Unity: GameDirectorAdapterBase and GenericSceneAdapter
                      Three.js: adapters/stardeck/adapter.js
```

Core targets netstandard2.1 with no external dependencies. The same DLL runs in
the .NET tools and Unity's `Plugins/`; engine and game bindings remain outside it.

## Adapters

- **Unity:** `GenericSceneAdapter`, role/location markers, and the Workbench's
  asset discovery need no code. Subclass `GameDirectorAdapterBase` only for
  special runtime systems such as networking, custom spawning, or face rigs.
- **Godot:** metadata only; tag scene nodes as described in [Godot](godot.md).
- **Three.js:** a small `adapter.js`; [Stardeck](../adapters/stardeck/README.md)
  is the public example. Bind locations from the manifest rather than keeping
  a second coordinate table.

## Directing loop

```
Agent -> GET /manifest -> executable role/clip/location vocabulary
Agent -> timeline.json -> gd validate -> TimelineCompiler (no rendering)
Agent -> bridge -> engine-side compilation -> TimelinePlayer -> adapter -> engine
Agent -> gd take -> picture and receipts -> review -> revised timeline
Agent -> gd edit -> selected takes, audio and captions
```

The engine compiles again at the bridge boundary rather than trusting an external
process. Frame-indexed take identities and receipts make capture verifiable;
watching the picture and revising the timeline remain separate creative steps.

## Invariants

- **Determinism:** the same timeline and time-step sequence produce the same event sequence. Timeline time is unscaled; slow-motion cues do not move cue boundaries.
- **Explicit restoration:** a timed `world.timescale` cue injects a restore cue. Compiled output is the complete event list.
- **Compile-time presence:** ordered spawn/despawn simulation rejects operations on absent roles (GD1011). A role with `PresentAtStart=false` must be spawned first.
- **Engine-neutral identity:** Core sees role/location IDs, not scene object names. Adapters resolve and check concrete scene bindings before execution.
- **Local presentation only:** bridges bind loopback. Unity's bridge is an Editor play-mode tool, not a release-build gameplay service.

## Separate from an in-game AI director

Run director mode in an offline presentation sandbox, separate from any in-game
AI director or gameplay simulation. GameDirector owns the recorded presentation,
not gameplay authority; directing a live multiplayer session is outside this
release's supported workflow.

## Deliberately unsupported

- Timeline seeking or reverse playback.
- Open-ended cue extension fields; supported shot parameters are declared in the contract.
- Live multiplayer filming, realtime engine-audio capture, and simultaneous multi-camera rendering.

## Foundation contracts

The .NET Core owns capability validation and compiled cue order. Unity and Three own
engine-specific rendering and visual lifecycles. Game adapters own role/animation
bindings and custom source identity. `IsAnimationBound` and `PlayAnimation` form the
Unity extension pair for procedural clips; the default implementation checks native
Animator state. `IDirectorPresentationParticipant` supplies snapshot, preparation,
clocked evaluation and restoration without running gameplay callbacks.

The stage must be static outside director ownership. Unity Preflight uses one clock
probe table: enabled Animators; playing/play-on-awake ParticleSystems or PlayableDirectors;
enabled legacy Animation with playAutomatically/isPlaying; enabled VideoPlayer with
playOnAwake/isPlaying; and conservatively all VisualEffects. Optional Video/VFX modules
are probed by type name/reflection, not package dependencies. It lists each full hierarchy
path before leasing anything. Adapters extend `IsAutonomousClock(Component)` (calling
base) to register custom clocks such as unscaled-time scripts; `IsStageClockExempt(Component)`
declares a specific clock owned or made static. Present-at-start role trees,
presentation-participant trees (also leased outside the cast) and the rig's own camera
are exempt. Participants/adapters must own any custom, unscaled-time or shader `_Time`
animation; freezing `timeScale` does not certify it. Borrowed stage Animators must be saved disabled. Frame i is rendered
at `FrameClock.TimeOf(i,fps)`; particle leases preserve explicit author seeds and derive
a stable UTF-8 hierarchy-path seed only for automatic-seed systems.

Failure classification answers "whose fault is this?" once. The executable
status/code table is `gd contract --json` → `bridge.errors` (projected from
`BridgeErrors`); readers select definitions by key, not by copied HTTP numbers.
A request uses `invalidRequest` when it fails parsing, compilation
or Core admission — its validity is judged against a **valid** manifest.
`TimelineCompiler.ValidateManifest` is a separate public verdict; admission keeps
it outside caller errors. Manifest rejection belongs to the engine, not the
film author, but client exception types depend on the boundary: bridge manifest
reads are tolerant (an omitted `manifestVersion` takes the model default), while
unparseable bridge JSON produces a rejected `BridgeException`. A strict
`--manifest` file parse failure is `AuthoredInputException(Manifest)`; only a
semantic manifest failure in `RequireValidManifest` throws
`EngineProtocolException`. Rejected manifest files produce no CLI admission
envelope; both engines report `bridge.errors.internal` for their own invalid
manifest. Preflight runs after admission, so
every Preflight refusal (undeclared mode, unbound
present-at-start role, unowned stage clocks, a manifest location, audio or
animation binding the scene does not resolve, an adapter stage refusal via
`RefuseStage`) means a manifest-valid request this stage cannot execute right
now: `bridge.errors.stateConflict`, never invalid input and never an engine fault.
Unclassified failures use `internal`; `busy` is retryable; the main-thread
deadline uses `timeout`. Runtime execution failures inside adapter methods
after Preflight stay unclassified (`internal`). Registry ambiguity uses a dedicated
binding exception, translated only inside Preflight; animation discovery defers
ambiguous bindings to that gate instead of pruning the caller's vocabulary.

The client owns durable take jobs, not engine state. It accepts frames only under a
known take identity, journals their hashes and verifies the authoritative completion
receipt before encoding. Recovery never combines two performances' pixels. The edit
engine consumes verified media from either engine. Performance catalogs retain
source-backed directing observations alongside the workflow; their structural validity
cannot certify artistic meaning or the truth of a cited observation.

EngineContract 1.0 also supplies camera kernels' formula ids/samples, capabilities,
review cadence, bridge limits/budgets and take states. Unity reads Core declarations;
Three reads the export; both refuse uncovered versions/formulas/parameters. See the
[engine implementer guide](contract/README.md).
This is not health/route-method negotiation; that remains a separate protocol step.

See [installation](install.md) and the [engine implementer guide](contract/README.md)
for supported setup and integration requirements. Engine extensibility, a successful
capture and human visual acceptance remain distinct claims.

## Content shape: everything is a declared row

GameDirector's content is cues, shot vocabulary, capabilities, review findings,
manifests and bindings, and adapters. Every name or fact is declared ONCE, in a
table or contract row; every consumer reads the row. A second copy is not a
shortcut but a source of drift: a stage built from a separate location table
can disagree with `gd_get_manifest` and AI planning. Build locations from the
manifest, or derive the manifest from scene markers, so both describe the same
geometry.

Behaviour comes from ONE evaluator per kind of judgment, never from per-call-site
restatement:

- Capability admission: `CapabilitySpecs` rows plus the compiler's single
  `ApplyCueRules` pass (`src/GameDirector.Core/Dsl/CapabilitySpecs.cs`,
  `src/GameDirector.Core/Compilation/TimelineCompiler.cs`).
- Camera vocabulary: `ShotVocabulary`/`CameraContract` (`src/GameDirector.Core/Dsl/`),
  exported to engines through `EngineContract` (`gd contract --json`); engines
  refuse uncovered versions instead of re-deriving semantics.
- Frame review: `CompositionPolicy` rows plus the single `CompositionReview.Evaluate`
  (`src/GameDirector.Core/Capture/`).
- GL backend selection: the `GlBackends` table
  (`packages/gamedirector-three/gl-backends.mjs`).

Engines implement effect bodies — camera kernels, spawn/animation calls — and those
stay code. Everything else derives from the rows.

Not everything transfers from game architecture, by design:

- No ECS simulation: entity simulation lives in the games and engines; the
  director composes declared capabilities, it does not simulate a world.
- No runtime triggers: rules run at compile and review time, before or after a
  take, never inside the frame loop.
- No event-sourcing retrofit: the timeline already is a command log, replayed
  deterministically by `TimelinePlayer`.

The question every new feature must answer first: "is this a few data rows?"
If the honest answer is a new evaluator, that is a design discussion, not a PR.

Known gaps:

- The cue type table: adding a cue type still touches about a dozen files in 4 languages.
- Error kinds, engine family names, audio buses and decision task ids are not yet
  single declarations; generating MCP tools from the CLI specs is planned.
- Manifest 0.2 with single-source locations is planned; Stardeck's Three adapter
  already places its stage from the manifest.

The guard-test direction follows the same shape: today's contract-reads lint
(`tools/test_engine_contract_reads.py`) grows into a name-literal guard generated
from the golden contract, so an undeclared name fails CI instead of drifting.
