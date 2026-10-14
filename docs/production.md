# Production contract

The deliverable is a watchable film made from repeatable, identifiable engine
captures, not merely a successful HTTP call, compilation, or beautiful still.
Engine-neutral authoring, engine presentation, and media assembly stay separate.
Adapters advertise only executable capabilities; artistic decisions require
watching the film rather than inventing an aesthetic score.

## Capture and ownership

- Validate timelines, dimensions, manifests, and capabilities before mutation.
- Use an owned presentation stage/sandbox, never silently alter gameplay truth.
- One explicit frame clock controls animation, camera, and presentation time.
- Frame-indexed transactions reject wrong order/mode before mutation; retrying
  the current frame cannot double-advance it.
- Stop, failure, replacement, and normal completion clean up owned resources.
- Every take records timeline/source identity, frame hashes/counts, and receipts.
  Successful video verification does not prove artistic quality.

Unity uses an owned sandbox; Three.js uses fresh browsers with a declared GL
backend; Godot uses fresh admitted GDScript workers. Interactive controls and
review/measurement capabilities differ by engine; consult the live manifest and
[engine contract](contract/README.md). Cross-engine or cross-GPU pixel equality
is not promised. Godot's scene/resource source changes require a bridge restart.

## Replay-only pre-roll

`TakeRequest.captureFromFrame` defaults to 0 and must be in `[0, FrameCount)`.
`FrameCount` in the engine receipt is the total timeline/replay length; the receipt
also echoes `captureFromFrame`, so the picture contains `FrameCount - captureFromFrame`
frames. `capturedFrames` counts only rendered PNGs. Core admission requires a
`camera.shot` at or before `FrameClock.TimeOf(captureFromFrame, fps)`, not necessarily at t=0.

Clients request **every** timeline index from zero, strictly in order. For indices
below `captureFromFrame`, `FrameRequest.advanceOnly: true` advances to
`FrameClock.TimeOf(index + 1, fps)` without rendering, inspection, PNG encoding or
transfer; the reply is **204 with no body**. Remaining requests use `advanceOnly: false`,
render at `index / fps`, then advance one step and return a PNG. Only the last index
can be retried: same mode, same reply, no additional advancement. Wrong order/mode
is refused before mutation. Each request retains the bounded one-frame work unit.

`gd take ... --from-frame N` selects the capture start (`gd compile --take` accepts
it too). `job.json.ExpectedFrames`, PNG filenames, `frames.jsonl` indices and
`take.json.FrameCount` count **picture frames**, numbered from zero; the job and
`take.json` persist `captureFromFrame`. Resume replays from zero into a new attempt
with that same capture start, preserving interrupted evidence.

Film preparation retains all performance cues before each shot's End, but no
longer generates a lockoff pre-roll camera, substitutes subjects or requires a
role at t=0. Each shot captures from its frame-aligned Start; Studio edits the
picture range `[0, End - Start)`. Cache generation is `format.4`; prune reclaims
older generations. `FilmLimits.MaxCaptureSeconds` still bounds total **replayed**
seconds, including the non-rendered prefix. Progress reports rendered and advanced
frame counts. Supersampling and media encoding profiles are unchanged.

## Produce, revise, inspect

1. Fetch the live manifest; author against real role/clip/location vocabulary.
2. Validate the timeline or accepted FilmDocument revision, including audio.
3. Capture with explicit dimensions/fps into a fresh output directory outside
   project source. Preserve failed/interrupted attempts as evidence.
4. Review the encoded picture and receipts. `gd review` samples composition;
   supported engines also provide `gd measure` and expectation sidecars.
5. Revise only the requested shots, then reuse verified unchanged material through
   the production service. `gd edit` composes sound/subtitles and validates media.
6. Inspect the actual final film and listen to audio; record what was reviewed
   and what remains uncertain. Numeric baselines do not judge acting or intent.

FFmpeg/ffprobe remain external prerequisites; picture capture is not a promise
of live engine audio. Verified takes retain hashes rather than PNGs unless
`--keep-frames` is selected. See [review limits](review/README.md),
[Workbench](workbench.md), and the [directing playbook](directing-playbook.md).

## Reproducible example

Use the independent [Stardeck sample](../samples/godot/stardeck/README.md)
for a reproducible capture and review workflow. Release test counts do not
substitute for reviewing the picture produced by your own stage.
