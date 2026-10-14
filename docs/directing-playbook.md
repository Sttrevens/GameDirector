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
   is true; the director must inspect the evidence. Catalogs pin the manifest's
   vocabulary identity (machine/session-volatile and public read-only inspection
   capability values excluded), so a moved project, new session or inspection stamp
   does not stale catalogs or decision evidence; take caches still key on the full
   manifest; a catalog still stamped in the
   legacy file-bytes format must be migrated with `gd catalog-restamp performances.json
   --manifest manifest.json` before checks pass.
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

## Aspect is a basic capability

For raw timelines, use `camera.shot` cues with `aspect:{min?,max?}` on width/height.
Intervals are half-open: omitted min=0, max=infinity; 1:1 selects min:1. Same-time
alternates must be contiguous in authored order, partition all positive aspects,
and share durationSeconds and event role/label (normally omitted), with no unhinted
camera twin. All alternates are
validated; Core keeps exactly one and clears the hint. Use explicit anchors after
a hinted group: a later from/to `current` (including omitted from) is GD1013 until
an unhinted explicit-anchor cue strictly earlier than the read (beyond Core's cue epsilon) resets that dependency. A same-instant cut cannot reset it; neither can a cut whose subject/lookAt despawns at that instant.

Validate and compile hinted timelines with **both** `--width` and `--height`; no
silent landscape default. MCP `gd_validate_timeline` likewise accepts optional `width`/`height`, requiring both for hints. `gd take` resolves at its capture size, stores/sends the
resolved timeline and records the authored file's byte hash only for hinted jobs.
Resume uses that resolved input. Interactive `gd play` and MCP `gd_play_timeline`
have no size and refuse hints locally, even with an old Unity package. Unhinted
compiled outputs, stored inputs, hashes and jobs retain their existing bytes.

`shot.with` names known, distinct roles other than the subject to protect in later
composition reviews; it does not move the camera. Under today's vertical fit a
role may enter inside the shot window and be absent until spawn, or despawn during
it. Refuse only a role never present in the window (GD1011); unknown ids are GD1001,
duplicate/subject ids GD1009. Presence at shot start is a later contain-only rule.
For an OTS foreground that is intentionally cropped, omit it from with.

Hor+ fixes vertical fov at every aspect; it does **not** automatically fit a pair.
Review both 960x540 and 540x960 and author suitable alternate coverage/anchors.
Contract 1.1 adds Hor+ and role-bounds/crown reference samples. Three emits per-role
reviews; `gd review <take-directory>` checks the latest Verified attempt, never a
stale or rejected receipt. Lost subject/with roles and protected side/near-plane
cuts are errors; head cuts warn (default medium: framed-by-centre). Single-subject
lateral crop never errors; authored angles or overflow beyond the policy's rect-width fraction warn.
Use `gd review <take-directory> --safe-area tiktok` for Core-owned TikTok warnings only; preset values, all policy thresholds and cadence are generated by `gd grammar` (the marked Core-owned table is in [review](review/README.md)). Legacy centre-only
reviews warn only. Sampling can miss transient cuts; inspect footage.
Aim uses each engine's legacy bounds; reviews use role-bounds-v1. Contain and film
variants remain deferred. FilmPlan still has one camera per shot; cue-level
alternates do not reach films. See [composition review](review/README.md).

Portability acceptance: measure all asset preparation, visual binding, adaptation,
troubleshooting and revision work. Use another real game, not another scene of the
same game. Repeat recording and recovery tests on the declared engine/platform.
