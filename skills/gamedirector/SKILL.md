---
name: gamedirector
description: Create, revise and produce game films with an installed GameDirector CLI or MCP and a connected game engine. Use for offline game trailers, machinima and shot-local film revisions.
---

# GameDirector

Use the installed `gd` executable or GameDirector MCP. Read `gd --help` and `gd guide` for the installed version's commands and directing contract; discover MCP tools from the connected server. The runtime owns encoding, jobs, versions and output storage. This skill does not contain the runtime.

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

## Produce and inspect

Submit the accepted document revision through the production service. Follow its job state, use supported cancellation/resume, and keep output outside game source assets. The saved film determines a repeat take; do not ask a model to decide the film again during capture.

Inspect the produced video/contact sheet alongside technical verification. Frame count, encoding, visibility receipts and hashes do not establish visual quality, natural acting or satisfaction of creative intent. Report what was actually watched and which shots need another pass. Reuse verified unchanged shots through the existing production workflow.

## Installation and credentials

Use `gd unity install --project <root>` to inspect the installation plan; add `--apply` when installation is within the user's requested work. Preserve local-edit conflicts and resolve them explicitly. Installation is not part of each shoot. Use a complete platform-specific runtime distribution for machines without a .NET SDK.

Keep model credentials in the local service's provider settings. Never write them to Film assets, plans, catalogs, exported projects, or source control. A disconnected/disabled provider does not prevent manual or externally authored film production.
