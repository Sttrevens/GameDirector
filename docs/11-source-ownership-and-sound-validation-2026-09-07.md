# Source ownership and supplied sound — 2026-09-07

This continues the R2 product Alpha. Historical results in document 10 are retained;
this report supersedes its picture-only and pending Unity onboarding statements.
CDREBIRTH main was clean at 989932ebb7a915f74224f2a29ad81049cd45d601 at turn start;
the user had already preserved prior work on game-director-branch. This turn does
not modify or install into that checkout. Product implementation remains uncommitted.

## Completed contracts

1. Common physical-path output admission protects source roots across Workbench,
   single-frame capture, takes, resume and editing. Engine manifests declare the
   actual source project; managed production pins it before capture. Store startup
   validates ownership before lock creation; dangling symlinks cannot redirect that
   first write into the game. The UI exposes storage and explicitly exports recipes.
2. Installation is a separate, explicit action with an inspectable file plan,
   per-file ownership receipts and zero-write repeat installs. Upgrades stop on
   local edits before writing. Saved stages are intentional new creative assets;
   browsing, production and export are not installation or scene-save operations.
3. Supplied dialogue/music/SFX and subtitles are part of FilmPlan and the UI/MCP/API
   path. Audio is normalized to immutable decoded PCM before source-bound checks.
   Captions/audio use final-film time. Sound-only changes preserve picture cache keys.
4. Real Unity exposed two onboarding/production defects. NewScene may already be
   active despite SetActiveScene returning false; validate actual state. Unity also
   populates missing shader defaults on first render. Material identity now compares
   effective declared shader inputs rather than the serialized default cache, while
   retaining real value/texture/shader/state changes. Custom shader globals and
   MaterialPropertyBlock inputs still require adapter identity participation.

## Validation actually run

| Lane | Result and evidence |
| --- | --- |
| .NET | 40 passed / 0 skipped; src/GameDirector.Core.Tests/TestResults/ownership-audio-final.trx |
| Workbench + real FFmpeg | 22 checks passed; captures/workbench-contract-tests/0c608a830a84/result.json |
| Take interruption/replay | Passed; captures/recovery-tests/957228ffc4b8 |
| Edit finishing | Source EOF, 24/30 fps conversion, caption burn and unchanged mastered gain passed; captures/edit/media-tests/45fd0a27c905 |
| Unity onboarding | 3 passed / 0 skipped; captures/unity-package-validation-20260907/results-r3.xml |
| Unity material identity regression | 1 passed / 0 skipped; same project/results-r4.xml |
| Unity presentation participant tests | 6 passed in results-r2.xml; its prior onboarding failure was repaired and rerun separately |
| Real Unity two-shot capture + revision | Both Verified, revision reuses 1/2 shots, 99 source files unchanged; captures/unity-capture-validation-20260907-r4/result.json |
| Repeatable installer | Dry plan no writes, repeated bytes/mtime unchanged, preserve local edits, owned upgrade and removal passed; tools/test_unity_installer.py |
| Independent review | Storage/audio and Unity source/installation/material fixes ACCEPT after supported findings repaired; reviewers checked code/evidence, did not claim their own runtime run |

The Unity test project was created specifically for this product validation, with a
basic capsule fixture. It did not run the user's CDREBIRTH editor or bypass that
repository's guarded testing workflow. Capture evidence proves real engine rendering
and source-file preservation, not a second production game's creative acceptance.
The audio test uses generated tones and verifies audible/silent ranges from decoded
samples. A 5-second video container with a shifted 1-second audio track is admitted
as approximately 1 second of sound; an impossible source range is refused before
any capture. The model-provider fixture is synthetic, not a real API acceptance.

## Still open; not a GA release

* UE and Godot connectors are not implemented. Their discovery, binding, execution,
  restore and installation contracts need real engine acceptance.
* Actual user-selected model API, Windows runtime and clean-machine dependency
  setup. No credentials were borrowed from other products.
* Voice synthesis, lipsync, automatic retargeting and general proof of semantic
  suitability. Asset names/IDs alone do not establish performability or aesthetics.
* Large-project resource budgets and additional user-game onboarding trials.
* Human judgement of narrative clarity, visual quality and soundtrack.

A script references local imported sound IDs: sharing its JSON alone does not yet
transfer media. Existing R2 videos remain reviewable; incomplete legacy jobs lacking
a declared source root require new production. These migration and sharing limits
are explicit, not silently counted as release-ready behavior.

## Packaged R3 acceptance

`dist/0.2.0-alpha.1-workbench-20260907-r3.zip`

SHA-256: `c190c4bd01d911c58f0d8872ad25d8f3bb7cfba0d7024332ef327f7a87641199`.
The archive is immutable. This supplemental section was written after packaging.

* Fresh extraction: all 205 indexed files verified.
* Its own MCP: 19 tools, audio import, live Sandring registration, FilmPlan
  validation, three-shot production with supplied audio/captions, request deduplication,
  Verified job, and served video matching its recorded hash.
  Evidence: `captures/workbench/installed-r3/installed-validation.json`.
* Its own installer into a new isolated Unity project: 59 initial writes, zero
  repeat writes; Unity import then all 10 package tests passed with zero skipped.
  Repeating installation after Unity import also performed zero writes.
  Evidence: `captures/unity-installed-r3/results.xml`, `install-validation.json`,
  `post-import-install-validation.json`.
* Actual browser: imported sound, authored caption, saved draft, produced and reviewed
  a three-second Sandring film; video readyState=4, one visible sound row, explicit
  storage directory, no horizontal overflow at 1440 px. Screenshot:
  `output/playwright/workbench-sound-verified.png`.

The final CDREBIRTH read-only status check showed a concurrent switch to
`codex/e-surface-drag`, with modified PlayerPhysicsGrabController.cs and a new
PlayerPhysicsGrabController.InteractDrag.cs. Those were not written by this task
and were preserved. The preservation branch game-director-branch still exists.
Do not infer that the currently active CD checkout remains clean from its initial
main-branch snapshot.
