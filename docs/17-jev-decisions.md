# 17 — Jev decision layer (bounded candidate decisions)

Status: implemented and locally verified (unit + HTTP contract with a synthetic
provider). No live OpenRouter call has been made from this checkout — see
"Verification and limits".

The decision layer lets a small decision model choose **among bounded,
evidence-backed candidates** instead of generating anything. It never writes
documents, never touches production, and never infers meaning from names. Every
ask — selected, needs-review, no-match, completed — commits one audit record.

## Pieces

- `src/GameDirector.Decisions` — typed contract (`DecisionContracts.cs`), wire
  format (`JevWire.cs`), provider (`OpenRouterJevProvider.cs`), task question
  builders (`DecisionTasks.cs`), policies (`DecisionPolicies.cs`), audit model
  (`DecisionRecord.cs`), local-path scrubber (`LocalPathScrubber.cs`).
- `src/GameDirector.Workbench/DecisionService.cs` — orchestration: provider
  connection (memory-only key), persisted policies, catalog storage and
  freshness, the five task workflows, audit persistence, dedupe/conflict, and
  bounded preparation for generation.
- `src/GameDirector.Workbench/ApiDirector.cs` — generation consumes a selected
  performance-match decision as evidence and persists decision linkage with
  each saved proposal.
- MCP: `gd_studio_decision` dispatches any supported task id to the same HTTP
  API; `gd_studio_decision_records`, `gd_studio_decision_settings`,
  `gd_studio_catalog` expose audit/config/catalog.

## Tasks

| task | question kind | candidates | result |
|---|---|---|---|
| `performance-match` | choice | evidence-backed catalog entries (max 24) + `none` | selected entry id |
| `camera-choice` | choice | compiler-validated cameras for one exact shot (max 16) + `none` | selected id + shot-local proposal film |
| `audio-match` | choice | imported sounds **with authored descriptions** (max 24) + `none` | selected media id |
| `film-ranking` | score (per candidate) | submitted films that compile (max 8) | ranking; top pick only when calibrated confidence is met |
| `semantic-review` | noul and/or score | caller-requested checks over a supplied text (max 8 checks) | `completed` with answers; never selects or applies |

Bounded sets are hard bounds, not truncations: when the eligible set exceeds a
task's bound, the outcome is needs-review ("nothing was offered") and the
provider is not called. The audit's candidate ids/hashes are always exactly the
offered set.

Camera proposals are exact shot-local revisions: the whole film compiles with
only that shot's camera replaced; proposals never auto-save (document
acceptance stays revision-aware). The convenience enumerator derives candidates
from manifest shot/frame vocabulary in declaration order with no frame
preference, as a disclosed deterministic subset; external agents may submit
their own candidate list instead, no generative API required.

`semantic-review` is text-only by construction: noul checks carry the fixed
false/true criteria poles; score checks carry the caller's ordered legend as
the criteria array (index = score). It never judges rendered video, audio or
gameplay and never replaces compilation or rendering verification; the audit
reason states this. It needs no engine connection.

## Policies: calibration, not magic numbers

Each task has `{enabled, minConfidence}` with `minConfidence` **nullable**.
Null means *uncalibrated*: explicit asks still run and answers are recorded,
but the outcome is always needs-review — nothing auto-selects. There is no
universal confidence cutoff: a value only means something for a specific
provider + model + task, measured on real outcomes. Ship default is therefore
null for all tasks; the UI preserves blank (never coerces blank to 0), the
audit records null, and the docs you are reading are the only "default".
Configure an explicit per-task bar after evaluating your own pairing.

## Idempotency and audit

One record per requestId under `decisions/<project>/<task>/<requestId>.json`.
The input hash covers: task, question version, scrubbed inputs, film/document
identity, candidate ids+hashes, manifest/evidence hashes, model, per-task
enabled flag and threshold, and the provider **endpoint**. The API key is a
memory-only secret and is never part of identity — rotating a key does not
orphan records; changing endpoint/policy with a reused requestId conflicts
(409). Identical repeats return the saved record without a provider call.

Cancellation mid-ask persists a needs-review uncertainty record (remote
completion/billing is unknowable), so replaying the same requestId cannot
silently ask twice.

Records carry: provider id/endpoint, requested model, resolved
model/provider/request-id/cost when returned, answers (choice/score/noul with
confidence where the contract has it — noul has none), candidate ids+hashes,
manifest/evidence hashes, threshold in effect, outcome and reason, and the
proposal film (camera-choice) or ranking (film-ranking).

## Generation integration (bounded preparation)

`/api/direct` asks `DecisionService.PreparePerformanceEvidence` before
generating. Only when the provider is configured, performance matching is
enabled **and calibrated**, and a usable (present, fresh, valid, non-empty,
within bounds) catalog exists, does it run the performance-match task with a
deterministic `gen-<input-hash>` requestId that includes the generation request's
identity. Retrying the same generation reuses its persisted decision; an explicitly
new generation request can ask again. Only a **selected**
entry's evidence enters the prompt (`PERFORMANCE EVIDENCE` block). No provider,
disabled, uncalibrated, stale, empty, excess or no-match ⇒ no semantic claims
are injected and generation is otherwise unchanged. The saved proposal carries
`performanceDecision` linkage (task, requestId, inputHash, outcome, selected
candidate, used).

## Privacy and transport

Provider requests are scrubbed of local paths (known roots, drive/UNC/POSIX/
file-URI patterns); the serialized payload is guarded against path-like
fragments (JSON-escape-aware) and refused before any network use. Provider
failure reasons are scrubbed before entering audit records. One bounded attempt
per ask: no redirects, explicit timeout, request/response size caps, HTTPS
(loopback HTTP allowed for local decision servers). The decision endpoint,
model and key are independent of the generative plan provider; keys live only
in Workbench memory.

## HTTP API (Workbench, loopback only)

- `GET/POST /api/decisions/provider` — status / configure (empty endpoint+key disconnects)
- `GET/POST /api/decisions/policies` — read / replace per-task policies
- `GET /api/projects/{id}/catalog` — freshness status; `GET .../catalog/content` — download
- `POST /api/projects/{id}/catalog` — import (explicit replacement); `POST .../catalog/scaffold` — create skeleton **only when none exists** (never overwrites curated evidence; reports freshness)
- `POST /api/projects/{id}/decisions/performance-match|camera-choice|audio-match|film-ranking|semantic-review`
- `POST /api/projects/{id}/decisions/camera-choice/suggest` — manifest-vocabulary candidates
- `GET /api/projects/{id}/decisions` and `GET .../decisions/{task}/{requestId}` — audit

## Verification and limits

- `src/GameDirector.Decisions.Tests`: 79 tests — wire shapes and malformed/
  unasked/unoffered/out-of-range responses (incl. coordinator boundary tests),
  provider HTTP policy (no redirects, one attempt, timeout, caps, path guard,
  JSON-escape false-positive guard), service workflows (fresh/stale/missing/
  empty/excess evidence, disabled, unconfigured, uncalibrated, provider
  failure, scrubbed failure reasons, request-protocol validation as
  needs-review, cancellation uncertainty + no silent re-ask, key rotation vs
  endpoint/policy conflict), exact shot-local proposals, generation evidence
  injection/no-match/repeat-dedupe/unchanged-without-config, production never
  deciding, generator repair loop with injected handler.
- `tools/test_decisions_http.py`: black-box HTTP contract against a local
  synthetic provider (routing, dedupe/conflict, malformed answers, scrubbing,
  memory-only key, uncalibrated ranking, semantic noul/score, catalog download
  and non-destructive scaffolding). **Not a live OpenRouter test.**
- Not covered: live provider behavior, rate limits and pricing; UI rendering is
  exercised manually, not by automated browser tests; semantic-review answer
  quality depends entirely on the configured provider/model.
