# Production contract and first-principles review

Baseline: `0d8064b`; review started 2026-09-05.

The deliverable is a watchable film made from repeatable, identifiable game takes. A successful HTTP call, compiler pass, or single beautiful still is insufficient. Engine-neutral authoring stays separate from engine presentation and from media assembly. Adapters declare only executable capabilities. Art direction remains a deliberate editorial choice, supported by images rather than a made-up aesthetic score.

Acceptance: reject malformed plans before mutation; stop/failure/replacement restores owned state; capture uses one frame clock; retries cannot double-advance; every output has source, timeline and frame-count provenance; three CDREBIRTH sequences supply a 30–60 second film, with review frames and edit reasons. Test the actual open Editor and view rendered output. A second engine remains a separate generality gate.

Constraints: preserve existing scenes and gameplay truth, work only in the dedicated offline sandbox, no live Fusion direction, no hidden network/provider dependencies, no authored absolute package references. Weakest assumptions: the old sandbox is still usable and its declared clips still exist. Falsifiers include unresolved actors, silent audio, occluded subjects, repeated-take drift, missing frames, or inability to compile in Unity.

Findings recorded before repair:

| Priority | Root cause / consequence | Repair contract |
| --- | --- | --- |
| P1 | Compiler mutates input; empty vocabularies permit arbitrary clips/shots; NaN and malformed manifests escape validation. | Detached compilation, closed vocabulary, finite values, manifest integrity. |
| P1 | Adapter calls log-and-skip yet playback reports success. HTTP 200 can contain `ok:false` and CLI exits 0. | Exceptions become terminal failures; clients check application receipts. |
| P1 | Stop/replacement/end lack ownership cleanup; duplicate spawns, movers and slow motion survive. | Explicit session begin/advance/end, symmetric snapshot restoration. |
| P1 | Camera wall time, unscaled movement, scaled Animator and real-time capture disagree. | Split advancement at cue boundaries; one manually evaluated presentation clock. |
| P1 | Capture is a still endpoint; no take identity, frame count, retry semantics, provenance or video. | Frame-indexed take transaction, idempotent current-frame retry, verified encoding. |
| P1 | Bridge timeouts leave executable mutations queued; unbounded queue/body and browser-origin writes. | Bounded intake, cancellation before execution, origin rejection, shutdown draining. |
| P2 | Global marker registries cross scenes; camera is unowned; shake accumulates; focal length ignored. | Scene-scoped resolution, camera lifetime, absolute motion and composition. |
| P2 | Audio is advertised but never played; one source cannot stop by identity. | Actual clip bindings and per-id audio ownership; capture explicitly declares its audio lane. |
| P1 | Known building occlusion accepted as M1 completion. | Scout and inspect each shot; repair staging before film assembly. |

Implementation and runtime evidence will be appended after verification, with open evidence lanes named explicitly.


## Independent review convergence

Independent reviewer found and verified repairs for particle phase/restoration, camera baseline leakage, framing fallback, null endpoints and role/heading presence, transient 503/504 frame retries, short-edit review sheets, actual prefab Animator state preflight and renamed layer paths. Final amended static verdict: ACCEPT. This is not runtime or visual acceptance.

Live discovery: the legacy two-class `SceneMarkers.cs` caused wrong MonoScript identity after scene roundtrip; class-matching script files and a separate freshly bound stage now pass save/reload identity checks. Existing duplicate sanctuary roots were removed only from the new film stage. An active auto-created `Prototype Runner(Clone)` was detected; capture refused. A scene-purpose marker now prevents CDREBIRTH's Editor automatic game launcher from creating that session; the capture guard still rejects a present Runner.


Additional live findings and closures (2026-09-06):

- Owned prefab activation previously allowed gameplay Awake/OnEnable and native navigation/autoplay to run before isolation. Owned actors are now cloned inactive, gameplay scripts removed in dependency order, and navigation/cameras/physics/autoplay suppressed before activation. Borrowed actors restore their lease; owned actors are destroyed without restoring game behavior. Animator events are muted throughout presentation and resampling. Independent amendment review: ACCEPT.
- Live controllers disproved the old hero Aim/Aiming and speaker Attack vocabulary. The canonical manifest now uses current state names (IKAim and BiteAttack); live vocabulary is intersected with actual bound layer-0 controller states. Preflight separately checks those bindings. Independent amendment review: ACCEPT.
- .NET solution tests: 30 passed after manifest correction. Three focused guarded Unity EditMode checks passed: scene ownership, distinct registry script identity, and the actual Big Guai V2 prefab's pre-activation isolation. The first ownership fixture used two untitled additive scenes and failed as a fixture setup error; it now uses isolated preview scenes. The first prefab check pointed at obsolete Big Guai instead of the bound V2 asset; the corrected actual-asset test passed.
- Real capture receipts: scout v2/v3/v4 each captured 20/20 frames and produced a decoded, verified video. Live bounds inspection confirmed zero Runners. v4 shows all three real game actors on the night stage. These scouts establish picture capture, not final film acceptance.

## Final production discoveries — 2026-09-06

| Finding | Root repair | Evidence |
| --- | --- | --- |
| Every unrelated scene/prefab import could trigger CDREBIRTH's expensive full production validation. | Automatic scheduling intersects changed paths with the cached transitive dependency closure of production roots, retaining the old graph until validation completes; cold, explicit Play/build and repair gates remain conservative. | Guarded `ProductionValidationTracksDependenciesInsteadOfEverySceneImport` passed; independent review ACCEPT. This fixes a reload-work amplifier, not a proven initiator of earlier scene saves. |
| Repeated hero framing drifted in the first four frames, despite equal timeline events. | `Renderer.bounds` could still describe the previous rendered skin pose when the camera latched its subject center. `BoundsOf` now bakes current skinned geometry and transforms its vertices once, disposing the temporary mesh. | Before: 16/20 PNGs identical. After: 20/20 byte-identical frames and equal events, same machine/settings. `captures/edit/film/repeat-corrected/repeat.json`; independent review ACCEPT. |
| Final assembly lost one frame at each source EOF; short edits also exposed timestamp quantization. | Pace frames at the output with explicit rate/count; use H.264/PCM MOV intermediates with rational video timescale and authored concat durations. Verify every segment before assembly and the final video afterward. | Rejected v1:1293/1296 frames. Corrected v2:1296/1296. `tools/test_media_pipeline.py` passes plain/captioned source-end cuts and 30→24 fps conversion. |
| Installed FFmpeg lacked caption support; a later font rendered Chinese as missing glyphs. | Preflight required codecs/filters and support isolated executable selection. Film captions use a verified CJK font; actual rendered captions were inspected. | Optional pinned local runtime; v2 full-resolution 7/25/48/52s titles and all 11 adjacent cut boundaries independently reviewed. |
| Speaker animation ended before its static insert, leaving a two-second dead hold. | Revise the authored shot to a slow dolly and record a new take through the same pipeline. | v2 motion analysis identified 41–43s; third sequence re-recorded as `take-03_keep_rolling-v2`, 432/432 frames. |

Final solution build: zero warnings/errors. Final .NET suite:30 passed.
Four guarded Unity checks passed (scene ownership, registry identity, actual
prefab isolation, production dependency scheduling). The subsequent bound
sampling amendment compiled in the live Editor and passed the actual repeated
capture check. This is not a broad regression claim for every CDREBIRTH system.

Three 18-second official takes supply the 54-second film. Each has432 decoded
frames at1920×1080/24fps; the last take was replaced after visual review. The
sample folder retains all timelines, edit reasons, subtitles and sound recipe.
Source scene and embedded per-file package hashes are retained in
`captures/edit/film/provenance.json`; source commits alone do not represent this
uncommitted implementation. See the [film recipe](../samples/timelines/directors-cut/README.md).

Scope remains explicit: Unity/CDREBIRTH offline presentation is exercised;
second-engine adaptation and live Host/Client direction are unverified. Same
machine frame equality is not a promise of identical pixels across GPUs or
shader implementations. Audio is composed from game assets in postproduction;
the picture take does not capture live engine sound. Human aesthetic preference
and listening are separate from technical media and rendered-frame review.

Workspace hygiene: original Lobby and grimforest sandbox have no file diff.
Three unrelated whitespace-only prefab rewrites were identified and restored.
The unrelated UIPlayer prefab has substantive changes of uncertain ownership;
it is preserved and excluded from the director acceptance claim. A backup patch
is in `captures/edit/film/unrelated-editor-changes.patch`.


## Delivered film

[《别停机 / KEEP ROLLING》 — final MP4](../captures/edit/film/delivery-v3/final.mp4)

Final delivery v3 is54.000seconds,1920×1080,24fps,1296decoded frames,
with stereo48kHz AAC. Video SHA-256:
`b6061a4434b34138e704a09e5f399538c4a4805f8b6f33ef5b84717e994a8ec2`.
Audio measures−19.64LUFS,−2.23dBTP and9.2LU loudness range. Whole-film
black detection found no events of≥0.1seconds at98% picture/0.02pixel
threshold; freeze detection found none of≥1second at−50dB. The previously
frozen speaker insert now moves continuously. These are configured detector
results, not a claim that all possible visual defects are measurable.

Rendered-frame self-review and independent review accepted the revised shot,
all cut boundaries and CJK typography. The closing title occupies the left
negative space, clear of the cast. The scoped independent verdict is ACCEPT;
audio listening and user aesthetic preference are not claimed.

- [Technical delivery receipt](../captures/edit/film/delivery-v3/delivery.json)
- [Rendered-frame review and measurements](../captures/edit/film/delivery-v3/review.json)
- [Independent review](../captures/edit/film/delivery-v3/independent-review.md)
- [Full contact sheet](../captures/edit/film/delivery-v3/contact-sheet.jpg)
- [Repeat evidence](../captures/edit/film/repeat-corrected/repeat.json)

Unity exited Play and restored the original Lobby, clean; the last10-minute
Console Error query returned no entries. Changes remain uncommitted in both
repositories. Media is local under ignored `captures/`; source recipes and the
separate CDREBIRTH director scene are reviewable project changes.

## Animated PV production — 2026-09-06

User feedback superseded the earlier invisible-camera fiction and explicitly
rejected a proposed gameplay-footage montage. The next acceptance artifact is an
animated CAM DOWN! trailer built through the same director/take/edit path. Its
recipe is [camdown-pv](../samples/timelines/camdown-pv/README.md); production records
are in `captures/edit/pv/cinematic/`.

The camera is a registered prop attached to the humanoid right hand. A second
performer uses actual Idle/Walk/Run animation clips; the cameraman uses IpadIdle
and IpadRun. BigGuai's real authored states supply the pose, irritation, rage,
hurricane and chase. Dialogue is an authored synthetic robot performance.

New root fixes: snapshot every borrowed attachment before resetting any Animator;
lease only disjoint actor hierarchies; deduplicate particle ownership. Separate
EditMode regressions pass for both parent-first and child-first role declarations.
A repeated five-second running/camera take yields 20/20 identical PNGs and equal
event streams. These are same-machine/render-setting results, not portability claims.

The PV stage builder rejects occupied outputs, preflights inputs, validates all
bindings before saving a versioned completion marker, returns the same validated
receipt on replay, and restores only its previously clean stage/new assets on a
failed build. Source review accepts the fixes. Successful saved-stage validation
and replay ran live; destructive failure injection on the actual stage was not run.

Final animation delivery: `captures/edit/pv/cinematic/delivery-v2/final.mp4`,
48 seconds,1920×1080/24fps,1152 frames. SHA-256:
`6b14f78fa5ae760101f1aa4ec8b7161ff4d6d0a30653bd701b111e89c3166d78`.
The audio revision measures−18.61LUFS/−2.00dBTP/LRA7.90. Its encoded picture
stream is byte-identical to the first reviewed edit. Opening prop, two-person
escape, all dialogue subtitles, title and13 cut neighborhoods passed sampled
visual review; no configured black/freeze events were found. Continuous human
audiovisual review is not claimed. Final checks and limits are in `review.json`.
Unity returned to clean Lobby, Edit mode, not compiling/updating, with no fresh
Error/Exception entries. The builder's real Configure call correctly rejected
Lobby and left it clean. Original Lobby and grimforest scene files have no diff.


## Live audience cinematic revision — 2026-09-06

The user accepted the 48-second PV's structure, then rejected its roughness and
weak differentiation. The new `camdown-live-pv` recipe makes viewers participants
in the cause-and-effect chain: sound check, request, compliment/pose, provocation,
insult/reaction, chase commentary, final direct reply. These are authored cinematic
performances, not claims of current provider, HUD or Host/Client acceptance.

The separate live PV scene adds native Animator-driven LED faces, with independent
material state and nested role ownership. Its matched blue/orange cinematic
costumes preserve production materials. Capture now renders at twice each output
dimension before a spatial downsample when the GPU texture limit allows it; this
avoids temporal-history dependence and global game quality changes. Camera target,
active render target and aspect restore in the capture's terminal cleanup.

The EDL mix limiter now disables automatic make-up gain. A real FFmpeg regression
compares the same source with and without silent music: both measured -24.1 dB
mean level. Plain/captioned EOF and frame-rate conversion also passed in
`captures/edit/media-tests/34c9b06f718a/`. The new face/camera/supersampled take
repeated with all 24 PNG frames and event streams equal in
`captures/edit/pv/live-cut/repeat/`.

Final selected live PV: [54-second animation](../captures/edit/pv/live-cut/delivery-v2/final.mp4),
1920×1080 at 24 fps, 1296 decoded frames. SHA-256:
`1d391f73b2628aefc11b338ae02867ab06e8d88cbec643ef142cc390cad78a8f`.
A separate 9–13 second praise pickup fixes the speaking partner's occlusion;
five prefix frames before that camera change match the full take byte-for-byte.
All 71 selected output frames across five contact sheets were visually reviewed,
including dialogue, audience callbacks, reactions and edit neighborhoods. This is
sampled picture review, not continuous audiovisual or human aesthetic acceptance.
The delivered audio measures -19.05 LUFS, -2.19 dBTP and 7.00 LU loudness range;
continuous listening was unavailable. See the adjacent
[review record](../captures/edit/pv/live-cut/delivery-v2/review.json).
Unity returned to the original Lobby, clean, in Edit mode. All changes and media
remain local, with previous films preserved.
