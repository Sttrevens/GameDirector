# GameDirector directing guide

The output is a coherent film that communicates this game's distinctive experience.
A valid timeline is executable vocabulary, not proof of good cinematography.

1. Establish scope. Read the game's current design and asset ownership. Use an isolated
   offline stage, never a live lobby or audience session. Decide the audience, feeling,
   core mechanic, length, and one visible causal chain before authoring.
2. Discover. Fetch the live manifest. Run `gd catalog-init --manifest manifest.json
   --out performances.json`. Unknown meanings must remain unknown. Inspect actual
   rigs, source assets, clip durations, held props, camera constraints and sounds.
3. Scout. Shoot short labelled samples of candidate performances. Describe the actual
   movement, readable reaction interval, useful framing, dependencies and limitations.
   Record design references separately from observed take paths and timecodes. Run
   `gd catalog-check performances.json --manifest manifest.json`; manifest changes
   require rechecking observations. The checker cannot certify that a cited observation
   is true; the director must inspect the evidence.
4. Compose. Build a beat sheet with setup, action, visible consequence, escalation,
   payoff. Explain why each selected performance expresses its beat. Do not infer
   emotion from a filename. Keep identity, screen direction, spatial relationships
   and held props consistent. Give important reactions time to read.
5. Stage. Declare deterministic visual participants for custom IK, material, facial or
   procedural animation. They own their snapshot, preparation, supplied-clock updates
   and restoration. For procedural Unity clip playback, the game adapter overrides
   both `IsAnimationBound` and `PlayAnimation`; a native Animator is the default,
   not a requirement for every game. Include generated/unserialized visual inputs
   through a pure, stable `AppendSourceIdentity` override. Do not preserve gameplay
   callbacks merely to restore visuals.
6. Rehearse cheaply. Record a small preview, inspect cut neighborhoods and each key
   performance. Revise camera, blocking, light and timing based on rendered evidence.
   Check occlusion and silhouette, subject separation, over/underexposure, continuity,
   typography and whether the core mechanic is understandable without explanation.
7. Capture. Use named takes, new output directories and source identities. `gd resume
   <take-directory>` re-encodes complete verified frame sets without the engine, or
   replays an incomplete take into a separate attempt after checking source identity.
   An acknowledged active take can be cancelled; unknown active sessions are never
   silently replaced. Old frames are preserved, never mixed with a new performance.
8. Sound. Picture takes are silent. Supply pre-recorded dialogue, ambience, music and
   foley from an interchangeable production source. No specific TTS provider or OS is
   required by the edit engine. Produce a measured soundtrack and align real voice
   durations with animation and subtitles; keep important speech intelligible.
9. Finish. Select ranges with edit reasons. Apply grading, mix and final titles through
   the EDL. Read the technical delivery receipt, then review actual rendered frames and
   continuously listen/watch. Mark listening unavailable when it was not performed.
10. Deliver. Keep the exact version/source identities, timelines, catalog, source takes,
    EDL, final media and review findings. Report technical, runtime and human acceptance
    separately. Authored dialogue/UI is cinematic illustration, not proof of a real
    network/provider/gameplay result.

A useful production folder contains: brief.md, manifest.json, performances.json,
beat-sheet.md, timelines/, takes/, edit.json, soundtrack.wav, titles.ass, review.md.
Never put credentials or audience identity data in production artifacts.

Portability acceptance: measure all asset preparation, visual binding, adaptation,
troubleshooting and revision work. Use another real game, not another scene of the
same game. Repeat recording and recovery tests on the declared engine/platform.
