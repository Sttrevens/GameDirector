# Windows verification — 2026-09-20

The core production workflow was executed on Windows 11 x64 with Unity
2022.3.34f1 in a newly created validation project. No existing game project was
opened or edited. This establishes a tested Windows path alongside the earlier
Apple Silicon record; it does not establish every Unity version, render pipeline,
GPU, Intel Mac or clean-machine installation.

## Measured production behavior

`tools/run_unity_validation.py` creates an owned project and runs the independent
example through the native Unity document client and the same local service used
by the browser. The editor path is supplied by the caller. The test reads media
tool overrides from the environment rather than assuming a developer's Mac paths.

Baseline evidence: `captures/windows-unity-baseline/evidence-film/result.json`.

- A real three-shot Unity film with imported sound and Chinese subtitles encoded
  successfully at 640 × 360, 12 fps.
- Unity saved document revision 1; a browser-equivalent camera edit saved revision
  2. A stale revision-1 save returned 409 without overwriting revision 2.
- Changing only the third camera reused the two unchanged shots.
- All 135 tracked source files retained their bytes across production.
- The served video matched the job's SHA-256 and contained an audio stream.
- The contact sheet was inspected: the example capsule, shot changes and Chinese
  captions are visible. This is a technical example, not a cinematic-quality demo.
- Unity EditMode: 12 passed, 0 failed, 0 skipped.

The initial Unity attempt failed in the caller's reduced Windows process
environment. Providing the standard `ALLUSERSPROFILE` and
`PROCESSOR_ARCHITECTURE` values to the validation child process allowed Unity's
package manager to initialize. No machine environment, PATH, registry, licensing
or HTTP URL reservations were modified.

## Regression evidence

Before the new distribution and decision modules were integrated:

- Core .NET tests: 58 passed, no skips. Windows tests exercise junctions without
  requiring Developer Mode or symbolic-link privilege; Unix tests retain symlinks.
- Media pipeline: real plain/subtitled video, EOF/frame counts, frame-rate
  conversion and mastered gain passed.
- Workbench: 24 black-box contracts passed, including generation repair limits,
  request reuse, project isolation, decoded audio timing, cancellation and resume.
- Film documents: 14 revision/persistence/concurrency checks passed.
- Recovery: partial replay preservation, source drift rejection, lost start and
  offline re-encode passed.
- Python installer: dry run, repeated zero-write install, local edit preservation,
  owned upgrade and removal passed.

These local logs are retained in `.tools/` and `captures/` (ignored build/test
output). Run the same tests against the final code and generated distribution;
baseline evidence alone does not certify the new modules.

After the initial decision API integration, the 24 Workbench contracts passed
again (`captures/workbench-contract-tests/5f8ca4e3cc7d/result.json`). A separate
HTTP test (`tools/test_decisions_http.py`) passed routing, empty-evidence no-match,
camera proposal, resolved-model provenance, path scrubbing, dedupe/conflict,
malformed response preservation, audit read/list and memory-only credential
checks using a local synthetic provider. This is not a live OpenRouter test.

The first native-package smoke caught a missing DecisionService registration
that compilation alone did not catch. The registration was corrected and is now
covered by the HTTP checks. Native-package tests use an external owned data
folder because the deliberately empty PATH also removes Git, which is required
to prove that output inside a repository is ignored.

## Reproducing the engine check

Build the solution in Debug, provide compatible FFmpeg/ffprobe on PATH or via
`GAMEDIRECTOR_FFMPEG` / `GAMEDIRECTOR_FFPROBE`, then run:

```text
python tools/run_unity_validation.py --editor <Unity executable> --out <new output folder>
```

Use a fresh output directory. Add `--package <unpacked Unity package>` to check a
generated package instead of the source package. The harness requires an owned
project marker before performing any Unity work. Output stays available for
inspection; it is not automatically deleted.
With a complete package, the film is produced by that package's native Workbench
executable, rather than the development build.

Add `--check-launcher` with a complete package to exercise the production Unity
runtime validation, permission preparation and native launch methods in a
separate owned store. The harness restores the previous editor address preference
and terminates only the service recorded by that owned store.

CI runs core/decision tests on Windows, macOS and Linux. It cross-builds all
declared distributions, and separately runs native CLI/Workbench/MCP and Unity
installer smoke checks on Windows and macOS hosts. These workflow definitions
were added locally; a future CI result is not an already-observed pass.

## Final local code checks

- Locked dependency restore passed.
- Release tests: **187 passed, 0 failed, 0 skipped** (108 core/distribution/media
  tests and 79 decision/generation tests).
- Real FFmpeg and ffprobe were copied into a fresh validation tree, hash-checked,
  capability-checked in staging, activated and capability-checked again. Both
  checks passed; the user's active media directory was untouched. Evidence:
  `captures/media-bundle-real-20260920/result.json`.
- The final decision HTTP fixture includes ranking with an uncalibrated policy,
  semantic noul/score checks, catalog download and non-destructive scaffolding.
  Changes to a candidate's rationale also conflict on request-ID reuse.

## Final packaged acceptance

- Windows native distribution passed with an empty PATH and invalid DOTNET_ROOT:
  CLI, embedded guide, Workbench, MCP, shared store, Unity installation preview,
  installation and repeated installation with zero writes.
- The final packaged runtime produced the independent Unity film, including
  audio and Chinese subtitles. Editing one camera reused two shots; all 467
  checked source files were unchanged. Evidence: `captures/windows-unity-final/evidence/result.json`.
- The production Unity launcher validated and started the bundled runtime:
  `captures/windows-unity-final/launcher/launcher-result.json`.
- Unity 2022.3.34f1 EditMode tests against the final package: **12 passed, 0 failed,
  0 skipped**. Evidence: `captures/windows-unity-final/editor-tests.xml`.
- Final HTTP regressions passed: 24 Workbench checks, 14 film-document checks and
  12 decision integration checks using four synthetic provider responses.
- Windows, Apple Silicon Mac, Intel Mac and Linux UPM archives were generated.
  Archive entries, runtime hashes and Unix executable permissions were inspected.
  macOS and Linux execution, clean-machine installation, signing/notarization,
  live OpenRouter requests and browser visual acceptance remain unverified.
- These are local unsigned candidates, not a published release. FFmpeg/ffprobe
  remain external dependencies; no unverified download URL is shipped.
