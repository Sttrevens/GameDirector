# Foundation hardening and cross-game acceptance — 2026-09-07

Candidate: **0.2.0-alpha.1, local working-tree build**. Baseline commit
`0d8064b49ff8d0f511feaa30feaebb493533a052` does not identify these changes: the
checkout contains prior production work plus this hardening. Candidate file hashes
identify shipped bytes. No commit, tag, upload, deployment or remote CI run was made.

The product is an AI-directed offline filmmaking tool. Its reusable core is the
contract between declared game capabilities, a compiled performance, owned visual
state, captured evidence and an edit. Artistic decisions still require source
understanding, observation and revision. A successful render does not certify those
decisions, or real gameplay/network/provider behavior.

## Findings and resulting behavior

| Prior finding | Root repair | Evidence / boundary |
|---|---|---|
| F1: blanket script removal erased custom visuals | `IDirectorPresentationParticipant` separates scripted visual state from gameplay callbacks. Snapshot → prepare → supplied clock before/after native animation → restore. Inactive clones retain only declared visual participants. Spawn prepares them; despawn restores before destruction. Animation binding is adapter-owned, with native Animator as the default. Failures still release other borrowed state. | Six distributed Unity Editor regressions cover normal/failing leases, clone isolation, actual adapter spawn/despawn and live source identity. Test result below. Participants must not start gameplay in Awake, must snapshot without mutation and must restore partial preparation. |
| F2: only CDREBIRTH/Unity had run | A reusable Three bridge interprets the existing Core-compiled cues. The Sandring adapter imports the game's own arena, models and procedural animation in a separate headless page. No Sandring source, public UI, live session or provider is changed. | 34 seconds, 24 fps, 1280×720, 816 captured frames. Same-machine fresh scouts match 8/8 PNG bytes and event streams. The engine is new code; this does **not** satisfy the old M4 “zero Core/CLI changes” condition because a generic `compile` export was added. |
| F3: knowledge existed only in conversation | A versioned performance catalog separates design intent, observed movement and unknown meaning. Claims require evidence; observations require timecodes; stale manifests and unbound clips reject. `guide`, `catalog-init/check` and corresponding MCP tools expose the workflow. | CD and Sandring catalogs retained. Sandring bow/down have observed references; roar/wave retain readability limitations. Validation checks structure and binding, not the truth of the director's observation. |
| F4: interruption lost take ownership and resumability | Atomic job/derived receipts, exclusive job lock, per-frame hashes, known take identity and immutable source inputs. `resume` encodes complete verified frames offline, or replays an incomplete performance in a new attempt after checking source identity. It preserves old frames and rejects unknown active sessions. | Black-box CLI tests: partial replay, changed source, lost start reply, offline encoding, truncated/missing derived receipt. No old/new pixel splicing. |
| F5: no reproducible install/release lane | Pinned SDK/dependency locks, versioned packages, deterministic build, local candidate builder with per-file hashes, doctor/embedded guide, and Mac/Windows/Linux CI configuration. | Local extracted-candidate smoke below. CI configuration is committed-source work only: no remote run or Windows runtime acceptance is claimed. |

Additional defects exposed and repaired during independent review: stale/wrong
Three frame requests no longer fail another owner's take; equal-time despawn/cut
orders both complete; engine-source edits require bridge restart; source identity
includes live GameObject visibility/layer and borrowed actors under the director
hierarchy. Camera-owned child references do not spuriously invalidate identity.

## Evidence

- Core/client: 32 tests passed in Debug and Release; `src/GameDirector.Core.Tests/TestResults/foundation.trx` and `foundation-release.trx`. Locked restore also passed.
- Take recovery: `captures/recovery-tests/4f1e2b8e50d4` passed all black-box scenarios.
- Edit renderer: `captures/edit/media-tests/bdee552b8f25`: plain/captioned exact EOF,
  frame-rate conversion, and both source/music gain checks passed. The system FFmpeg
  lacks subtitles; it correctly rejected captioning before output. The documented
  isolated `.tools/media/ffmpeg` passed. Keep `GAMEDIRECTOR_FFMPEG` configured.
- Sandring real asset take: `captures/sandring/production-01/picture.mp4` and its
  `take.json`, `job.json`, `receipt.json`, frame journal and contact sheet.
- Same-machine repeat: `captures/sandring/repeat-02/repeat.json`.
- Actual Three bridge regression: `captures/sandring/bridge-regression-01/result.json`:
  two equal-time orders, six malformed/stale/index requests preserving ownership,
  and four byte-identical repeated frame responses.
- Unity: all six focused `PresentationParticipantTests` passed in the live Unity
  2022.3.34f1 Editor. Evidence in CDREBIRTH:
  `Logs/TestResults/agent-guarded/2026-09-06T16-21-25-792Z_PresentationParticipantTests.json`.
  The runner reports 6378 discoverable tests; **only these six were executed**.
  Discovery needed a refreshed test list; test fixture MonoBehaviours were moved to
  a runtime test assembly after Unity correctly rejected Editor-only components.
  Four existing CD regressions also passed individually: nested roles in both orders,
  actual BigGuai inactive clone isolation, and offline scene/network-start ownership.
  Their 2026-09-06T16:10–16:11 UTC records are in the same guarded-results directory.
  Independent bounded adversarial review returned ACCEPT after verifying the fixes.
- Extracted candidate: all 168 initial candidate files matched their index, doctor,
  embedded guide, catalog validation, real 24-frame edit and MCP stdio initialization /
  discovery passed from a fresh temporary directory. `captures/candidate-install-smoke.json`
  records the exact archive and runtime. Final distribution verification is recorded
  beside the archive after packaging, so the report does not need to hash itself.
  `captures/candidate-install-r2-smoke.json` additionally records a clean npm dependency
  install and an actual 8-frame Sandring take through the extracted Three + CLI code.
  Final R3 changes only Unity animation-binding extensibility and the embedded guide /
  documentation relative to that tested Three installation. Unity tests use the same
  C# package sources with the embedded development Core DLL; the archive rebuilds Core
  in Release, whose 32 .NET tests passed. This is not an independent Unity installation
  on a clean machine.

## Visual acceptance and project selection

Sandring is a good second project because it has a different engine and procedural
visuals rather than Unity Animator clips. The adapter exposes five existing cue
kinds (shot, animation, facing, movement, marker); no Sandring-specific cue was added.
The 34-second piece is a **silent portability test**, not a release-quality PV.
Frame review shows recognizable bow/down motion, but the original selected primitive
models have weak limb separation/ground contact and the minotaur lacks readable dark
detail. Some close shots crop too tightly after movement. These are retained review
findings, not concealed as “high aesthetics achieved.” Do not advertise it as real
combat or audience interaction. Continuous listening does not apply to a silent take.

Frostlamp's current repository also uses Three and separates character/world visual
code, so it is a suitable next **adapter reuse** check. Its source was inspected via
GitHub, but it was not cloned, adapted or filmed in this pass. This is an inference
about suitability, not acceptance evidence. Neither game establishes “any game.”

## Recovery limits and remaining release acceptance

- Unity source identity includes saved asset dependencies and live serialized scene,
  material, transform, visibility and lighting inputs. Unity instance identities can
  conservatively reject an incomplete replay after Editor restart. Full verified
  frames still encode without Unity. Unserialized/generated/external visual inputs
  need a pure `AppendSourceIdentity` override before promising cross-restart replay.
- Three source identity covers game `src`, the adapter, engine, Three dependency and
  browser version; deterministic replay is only claimed on the exercised machine and
  settings. Source changes require a fresh take; engine changes require restart.
- Sound remains interchangeable post-production media. No engine sound capture,
  automatic acting semantics, universal aesthetic judgment, live-network direction,
  or unsupervised release certification is implied.
- Formal release remains open for remote CI, independent clean-machine onboarding,
  Windows game/render runtime, additional adapter reuse and human film review.
  These are explicitly unrun acceptance lanes; they are not substitutes for code fixes.

The north-star acceptance is a coherent, attractive film communicating a game's
specific experience. This pass establishes a more reliable production foundation and
one real cross-engine execution proof. It does not manufacture visual or human proof.
