---
name: gamedirector
description: Create, revise and produce game films with an installed GameDirector CLI or MCP and a connected game engine. Use for offline game trailers, machinima and shot-local film revisions.
---

# GameDirector

Use the installed `gd` executable or GameDirector MCP. Read `gd --help` and `gd guide` for the installed version's commands and directing contract; discover MCP tools from the connected server. The runtime owns encoding, jobs, versions and output storage. This skill does not contain the runtime. See [installation](../../docs/install.md), [timeline format](../../docs/timeline-format.md), and the [directing playbook](../../docs/directing-playbook.md) for setup and authoring.

## Establish what can actually be filmed

- Run `gd doctor`; use the selected engine's endpoint when available. A passing local media check does not establish engine readiness.
- Fetch the live manifest. Ground roles, clips, locations, camera vocabulary and audio in these capabilities. Clip names are identifiers, not evidence of an emotion or performance.
- Inspect the performance catalog and its manifest freshness. Distinguish authored intent from observed evidence; scout uncertain performances before treating them as proven.
- With no game integration, use Unity's **Tools → GameDirector → Create Independent Example**. It supplies a film and assets without a model account. Save the user's current scene before switching scenes.

## Prepare and revise

Create a FilmPlan using the user's brief and actual available capabilities. An external Agent can provide creative proposals without a second generation API account. Jev selects among legal candidates; it does not generate arbitrary scripts, calculate exact camera coordinates, or watch video.

For a local revision, resolve the requested stable shot ID from the current document. Preserve all unrequested cameras, performances, timing, dialogue, audio and subtitles. Submit legal camera candidates to the decision workflow when configured. A missing candidate, stale evidence, timeout or unresolved decision is a review outcome, not permission to substitute an unrelated result.

Validate the complete FilmPlan, including audio, against the connected project. Read the current document revision before accepting a proposal. On a revision conflict, reread and reconcile the user's intent; do not force an unconditional save. Keep request IDs stable for retries of identical operations and use new IDs for changed inputs.

Decision thresholds start uncalibrated and produce review outcomes. Set a task's threshold only from an evaluated provider/model/task pairing; do not lower it merely to force a selection. A cancelled remote ask records uncertain completion, so retrying that ID must not make another paid call.

## Aspect-aware raw timelines

Aspect is a basic Core capability, not a manifest camera parameter. A `camera.shot`
cue may carry `aspect:{min?,max?}` (half-open width/height, default 0/infinity).
Same-time alternatives must be contiguous, partition all positive aspects, share
durationSeconds and cue event role/label (normally omitted), and have no unhinted twin. Validate every alternative; use both
`--width` and `--height` explicitly with `gd validate`/`gd compile` (also `--take`).
Interactive CLI/MCP play has no size and refuses hints client-side (GD1013); take
resolves before saving/sending, retaining the authored byte hash for provenance.
After a hinted group, from/to `current` (including omitted from) needs an intervening
unhinted explicit-anchor camera cue. Otherwise GD1013 prevents aspect leakage.

`shot.with` lists known, distinct roles other than the subject. Under vertical fit
it is review-only: roles may spawn inside the shot window, absent until then, and
may despawn. A role never present during that window fails GD1011. Presence at t
is reserved for later contain. Hor+ keeps fov vertical; it does not auto-fit. Inspect
both 960x540 and 540x960; author OTS or explicit pair coverage rather than promising
automatic fitting. Use `gd review <take-directory> --safe-area tiktok`
for the latest Verified attempt; presets, all policy thresholds and cadence come from `gd grammar`
and the marked Core-owned policy in [review](../../docs/review/README.md), not copied platform insets. Lost/protected side or near-plane cuts error;
head cuts and safe-area violations warn only. Single-subject lateral crop never
errors. Legacy centre-only reviews warn only; inspect footage between samples.
Aim uses each engine's legacy bounds; reviews use role-bounds-v1. Contain remains
later; FilmPlan variants are not supported by cue hints.

## Produce and inspect

Submit the accepted document revision through the production service. Follow its job state, use supported cancellation/resume, and keep output outside game source assets. The saved film determines a repeat take; do not ask a model to decide the film again during capture.

Inspect the produced video/contact sheet alongside technical verification. Frame count, encoding, visibility receipts and hashes do not establish visual quality, natural acting or satisfaction of creative intent. Report what was actually watched and which shots need another pass. Reuse verified unchanged shots through the existing production workflow.

## Installation and credentials

Use `gd unity install --project <root>` to inspect the installation plan; add `--apply` when installation is within the user's requested work. Preserve local-edit conflicts and resolve them explicitly. Installation is not part of each shoot. Use a complete platform-specific runtime distribution for machines without a .NET SDK.

Keep model credentials in the local service's provider settings. Never write them to Film assets, plans, catalogs, exported projects, or source control. A disconnected/disabled provider does not prevent manual or externally authored film production.
