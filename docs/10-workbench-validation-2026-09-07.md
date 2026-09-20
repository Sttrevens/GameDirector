# Workbench Alpha validation — 2026-09-07

Historical R1/R2 record. Later sound, source ownership, successful Unity onboarding
and R3 installed-product acceptance: [current validation](11-source-ownership-and-sound-validation-2026-09-07.md).

## Objective and boundary

Build a reusable local director workstation: Unity dock + standalone window,
asset discovery, external MCP or user-supplied model API, immutable film jobs,
preview/revision/recovery. The smallest complete vertical slice is an asset-bound
picture first cut with a targeted revision. Engine adapters own safe presentation;
the local service owns production state; the model proposes a typed film plan.

Baseline HEAD: `0d8064b49ff8d0f511feaa30feaebb493533a052`. The worktree already
contained substantial uncommitted film/foundation work. This change did not commit,
push, publish, discard earlier work, or claim GA. Candidate hashes identify actual
packaged bytes; the baseline alone is not the implementation identity.

## Implemented

* Shared Workbench service and browser UI: projects, live capabilities, discovered
  inventory, saved film draft, editable shots, API connection, production queue,
  video preview/download, cancellation and resume.
* Scene performance and camera coverage separated by FilmPlan. Each camera replays
  the same scene prefix; output cuts use explicit scene-time ranges. Pixel cache
  includes source/plan/render identity and conservatively invalidates affected shots.
* Unity native EditorWindow, GUID/local-ID asset index, explicit new offline stage
  creation and automatic generic manifest discovery from actual controller/scene
  bindings. Discovered assets are not labelled as proven performances.
* MCP studio tools share the same HTTP operations and persisted jobs. Built-in API
  plans get strict field/binding validation and a maximum of two generation attempts.
* Local-only HTTP boundary, no redirected credential requests, memory-only model
  keys, persisted immutable requests, idempotency conflicts, exact-source recovery.

## Findings repaired

1. **Wrong project after delayed API reply:** a project switch could save project A's
   generated film into B. Fixed immutable request ownership and stale draft-response
   guards. Independent reviewer reproduced the old bug and checked the fix.
2. **Later-spawned camera subject:** synthetic pre-roll originally referenced the
   later subject too early. It now applies t=0 spawn/despawn order to choose an
   available subject, clears unavailable look-at targets, and explicitly rejects an
   empty-stage prefix the capture protocol cannot render.
3. **Disabled Unity registries advertised as ready:** generic discovery now matches
   the active registry rule used by runtime resolution and excludes cross-scene
   targets. Both role and location discovery follow this boundary.
4. **Incomplete inventory replacing a complete scan:** a reopened/cancelled scanning
   window no longer overwrites stored inventory with empty or partial results.
5. **Hidden file input breaking layout:** browser review found CSS overriding the
   hidden attribute. Corrected and rechecked with actual video loaded.
6. **Transport timeout mistaken for cancellation:** installation smoke under host
   load hit the bridge HTTP timeout. The worker originally classified any
   OperationCanceledException as user cancellation. It now checks its own
   cancellation token; transport timeouts remain Failed, with preserved recovery
   data. Partial replay is also no longer counted as reused verified shots.

7. **Untitled Unity scene prevents additive authoring:** reject before any folder or
   asset writes with a clear recovery message. Named dirty scenes remain allowed;
   stage activation is checked before any object creation.

## Evidence actually exercised

| Lane | Evidence / result |
| --- | --- |
| .NET core/client | 37 tests passed, `src/GameDirector.Core.Tests/TestResults/workbench-final.trx` |
| Workbench black-box contracts | 13 checks passed, `captures/workbench-contract-tests/89e1b9a60fd8/result.json` |
| Real Sandring initial production | Browser-submitted 3-shot, 3-second, 640×360, 12 fps film; job `701c1609d48245cd821a98b406f7fc7f` Verified |
| Real Sandring targeted revision | Changed second camera only; job `4becce2b74e143c797585b54267191f3` Verified, 2 of 3 shots reused |
| Browser visual/media | `output/playwright/workbench-verified.png`; video readyState 4, duration 3 seconds, no horizontal overflow at 1440px |
| Independent product review | ACCEPT after two supported P2 fixes; reviewer independently ran delayed-response UI simulations |
| Independent Unity review | Code-level ACCEPT after registry, untitled-scene and test fixture safety fixes; no independent Unity runtime run |

The black-box tests use a synthetic engine and model API while running the real
Workbench, recorder and FFmpeg. They prove host/origin isolation, endpoint checks,
invalid-binding admission, request deduplication/conflicts, one-camera cache
invalidation, bounded API plan repair, proposal reuse, killed-process restart,
source-drift refusal, partial replay, explicit cancellation, timeout failure classification and key non-persistence. They
are not a real provider or another-engine acceptance claim.

The Sandring sample is a **pipeline exercise**, not an aesthetic acceptance film.
The original procedural models have limited limb/readability detail. No original
Sandring gameplay source or provider service was changed or contacted.

## Release lanes not closed

* Full animated film sound, voice performance, lipsync and musical timing in the
  Workbench. Existing low-level edit tools remain available separately.
* Real model-provider compatibility/quality; only the compatible HTTP contract was
  exercised with a local fixture. No user API credential was requested or used.
* UE/Godot connectors, clean-machine installation, Windows runtime and remote CI.
* Automatic rig/clip retargeting and inferred semantic suitability of every asset.
* Human acceptance of visual quality, story comprehension and final film polish.

Unity import, onboarding tests and packaged-install observations are recorded in
the final supplemental evidence below; no unrun test is implied by a compiled DLL.

## Supplemental installation / Unity observations

The Unity EditorWindow was actually opened through the imported package.
`captures/workbench/unity-window.txt` records `windowOpen=true`, the original
`Assets/Scenes/Game Scenes/[Game]Lobby.unity`, `dirty=false`, `play=false`.
The new Editor/runtime/test assemblies imported successfully.

**Unity onboarding tests executed: one passed, two failed before repair.**
The guarded run is CDREBIRTH `Logs/TestResults/agent-guarded/
2026-09-06T17-38-58-178Z_WorkbenchOnboardingTests.json`. The disabled-registry test
passed. The inventory test relied on a main asset's display name, which Unity
changes on save; the corrected test looks up exact GUID/local ID. Stage creation
hit Unity's unsupported additive-new-scene case with an untitled scene loaded.
The factory now rejects that condition before any writes, while allowing named
scenes with unsaved edits. It checks stage activation before object creation. The
regression covers refusal, a named dirty fixture, original disk bytes and bindings.

After that run, Unity stopped responding to the guarded bridge and macOS app
inspection. A read-only process sample at 01:51 found the main thread waiting in
Mono GC_lock. MCP service recovery did not restore Editor readiness. No user scene
was saved, no Unity force-quit was attempted, and no direct TestRunnerApi/batchmode
bypass was used. The corrected onboarding tests require a successful follow-up run;
source review and .NET tests are not substituted for that result.

Candidate r1 (203 indexed files) passed fresh extraction hashes, CLI doctor/guide/
catalog, real one-second edit and stdio MCP initialization/listing (18 tools).
Its first full installed MCP → Workbench → Sandring attempt hit a 30-second bridge
start timeout while the host was under load, exposing finding 6 above. The failed
job was retained at `captures/workbench/installed-r1/local-data/jobs/
7002dca4976247d880d1ebada223f137/job.json`. Later installed-candidate verification
is recorded in the candidate's supplemental validation JSON.

## Final R2 candidate acceptance

`dist/0.2.0-alpha.1-workbench-20260907-r2.zip`

SHA-256: `6287462d09d57144518f1c632267d82995619e79a3359dca243a7ce1147b963d`.
This report section was appended after packaging; the archive contains the
pre-install report and points to supplemental validation evidence.

Fresh extraction verified all 203 indexed files. Its own MCP executable registered
Sandring, validated the film, submitted a real three-shot production, deduplicated
an identical request and polled it to Verified. The served video bytes matched the
recorded hash. Evidence: `captures/workbench/installed-r2/installed-validation.json`;
job `078928c81d8c4235b67c516dc8e0fe4f`.

R2 CLI and MCP files are byte-for-byte identical to those exercised by R1's doctor,
guide, catalog, one-second edit and MCP smoke. R2 includes the corrected Workbench
worker and the exact independently reviewed Unity factory/test files. Those Unity
files were also synced into CDREBIRTH. The final Default import request returned
HTTP 500 / Response data is null: post-fix Unity import and test acceptance remain
open, and skipped fixture tests must never count as passes.

The active local Workbench at port 39800 now runs R2 and reopened the existing store;
both earlier Sandring jobs remained Verified, with the revision retaining its two
reused shots. No real API provider, new clean machine, UE/Godot runtime or human
creative acceptance was exercised by this installed-package run.
