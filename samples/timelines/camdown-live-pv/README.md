# CAM DOWN! — 观众还在看

The user accepted the previous film's structure and storytelling but rejected its
roughness and missing distinction from Content Warning. This revision keeps the
animated film: camera, two friends, compliment, pose, insult, rage, escape, payoff.

## What the picture must communicate

1. A streamer asks a question and an identifiable audience replies while on air.
2. A viewer's request motivates an actual choice by a performer.
3. Praise and insult are directed at the same monster. Their different meanings
   cause visibly different performances, with enough reaction time to read them.
4. Viewers respond to those exact events and remember their earlier provocation.
5. The broadcast survives the chase. The final question receives an immediate
   answer, rather than ending on an upload or saved recording.

Official comparison source: Content Warning's Steam description places its
SpöökTube upload/watch party after returning to the surface, and already lists
voice chat. The difference this PV illustrates is semantic speech as an action
and the reciprocal live audience relationship, not merely microphone support.
https://store.steampowered.com/app/2881650/Content_Warning/

CDREBIRTH source anchors: current ProjectPulse routing; DEC-2026-08-10-010
(authoritative Voice Talk result feedback); AudienceSpeechAdmissionAndExpressionBudget
2026-08-27; LiveShowDualResolutionMvp 2026-08-21. The film does not invent audience
world-control, rewards, viewer counts, simultaneous dual-resolution payouts or
retired prompt-based Voice Talk. Outcome graphics follow the performed reaction.

## Production

- `story.json` owns dialogue and the audience's causal responses.
- `build_soundtrack.py <CDREBIRTH> <output>` synthesizes stock robot voices and
  assembles game music and foley. Its voice cache includes voice, text, rate and
  processing version, so direction changes invalidate the proper source.
- `timeline.json` is the ordinary director timeline, with native Animator face
  tracks on nested roles. `timeline-base.json` is its pre-dialogue staging source.
- `build_titles.py <output>` uses the script and measured dialogue durations for
  typography and at most two simultaneous audience replies. Dialogue is on the
  final, highest layer. There are no fabricated metrics or reward counters.
- `LivePvStageBuilder.Configure()` makes a separate scene and assets from the
  clean earlier PV. Independent material-driven face animations remove dependence
  on the game's face UI cameras and shared render textures. The partner wears the
  existing orange costume. The original scene, materials and movie are preserved.

This is authored cinematic illustration, including the audience conversation.
It is not a recorded provider session, exact production HUD, Host/Client test,
Voice Talk authority test or evidence that every possible utterance gets a reply.

Capture the saved `GD_CAMDOWN_LivePV_Sandbox.unity` through `gd take`, then finish
through `gd edit`. Local outputs live in `captures/edit/pv/live-cut/`.

`dialogue-timing.json` retains the measured voice windows; `build_timeline.py`
recreates the face tracks from those windows and the authored base timeline.
The final mix adds separate ascending/descending response accents after the
monster's positive/negative reaction. The two-pass master feeds the EDL at unity
gain; the final limiter preserves that gain.

Final selected export: `captures/edit/pv/live-cut/delivery-v2/final.mp4`,
54.000 seconds, 1920×1080, 24 fps, 1296 frames. Its EDL retains the full take
and the separately recorded praise pickup as hashed source takes. The pickup's
pre-change frames 0, 24, 72, 144 and 215 match the full take byte-for-byte.
