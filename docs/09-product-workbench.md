# GameDirector Workbench — local product Alpha

The product is a **local director workstation with engine connectors**. Unity's
dockable window and the standalone browser window operate the same project store,
production queue and film receipts. External MCP agents and the built-in API
director submit to that same service. Engine packages own discovery, safe visual
bindings and rendering; they do not each host their own model loop or job database.

## Unity V1 film authoring (2026-09-12 candidate)

Use **Tools → GameDirector → Create Independent Example** in a saved scene to create
owned sample assets, a capsule performer with an Animator state, generated melody,
a separate offline stage and a three-shot Film asset. No game-specific repository
or model key is required. Sample creation does not save the user's current scene.

Select the Film asset for the native Inspector. Click **启动 / 连接工作台**, open its
stage and enter Play Mode, import the supplied sample sound, then save/check/produce.
The full Unity candidate packages a self-contained Workbench in `Tools~/Workbench`;
its launcher finds that runtime without a .NET SDK, selects an available local port,
and reconnects through a persisted instance descriptor. FFmpeg with libx264/libass
and ffprobe remain external prerequisites in this candidate. They must be available
to the Unity process via PATH or GAMEDIRECTOR_FFMPEG / GAMEDIRECTOR_FFPROBE.
The bundled Noto Sans CJK SC font makes Chinese subtitle rendering independent of
installed system fonts; its OFL notice and pinned source/hash travel with the runtime.

The Film Inspector supports title/dimensions, scene/model/sound references, camera
subject/anchor/type/framing, timing, adding/removing shots and scenes, native save,
readiness, production, status and conflict recovery. Complex performance/audio/caption
scripts have an explicit apply editor. The browser provides visual audio controls,
caption editing and video review. Opening the browser preserves the project/document
selection. The asset GUID identifies a film: renaming preserves it, copying creates
another document. Moving the entire Unity project still changes its current project
identity; portable project/media transfer is not implemented.

A Film asset is the local authoring copy, saved deliberately with Unity Undo support.
The Workbench is authoritative for accepted revisions. `FilmDocument` contains a
schema version, parent revision, stable request ID and content hash. Each accepted
revision is a single immutable atomic record; no separately updated head pointer.
Saving requires `expectedRevision`; a stale client gets HTTP 409 with the current
record. Missing revisions get 428. A repeated request returns its original receipt;
using that request ID with different input is rejected. Clients preserve unconfirmed
operation identities when responses are lost. Legacy default drafts remain readable
without writing their source file and migrate on the first explicit save.

Web tabs keep unsaved content and its original base in per-tab session recovery.
A saved remote update never overwrites dirty local content. Users explicitly load
the newest revision or save their local edit as a different film. Inspector local
edits remain Unity assets; loading a remote revision is explicit and undoable.
The file and web UIs submit the same exact revision for readiness and production.
Saving an incomplete draft is allowed; filming requires a valid live capability
manifest, sound assets and encoding tools. Readiness performs no recording.

Document API: list/read at `/api/projects/{project}/documents[/{document}]`,
save with `{expectedRevision, requestId, film}`, inspect a historical `?revision=N`,
check `/readiness` with `{revision, preview}`, produce with `{requestId, revision, preview}`.
MCP exposes the same document operations. The old unversioned draft write endpoint
returns 428 so older clients cannot silently overwrite native/web changes.

## Run and use

Source checkout: `bash tools/start_workbench.sh`. Local candidate: run the included
Start GameDirector launcher or `dotnet workbench/gamedirector-workbench.dll --open true`.
Requires .NET 8 + ASP.NET Core 8 (or the .NET 8 SDK), FFmpeg with libx264, and ffprobe.
`GAMEDIRECTOR_FFMPEG` can select the isolated FFmpeg installed by the media setup tool.
The window is at `http://127.0.0.1:39800`. The service persists data in the current
user's local application data directory; `--data` selects a different store. Only one
process may own a store. API credentials live in memory and are cleared on restart.

1. Start your project's presentation adapter, then connect it in the window. Unity
   defaults to `http://127.0.0.1:39777`; the Three adapter defaults to port 39778.
   In **接入项目 → 选择文件夹…**, browse local folders, use the home/project
   shortcuts, go up a level or filter immediate subfolders. Confirming fills the
   absolute project path; cancelling preserves the prior value. Manual entry
   remains available. The picker reads directory names only and does not upload
   the project or start its engine adapter.
   Adding a project saves it immediately; engine connection is checked separately.
   An unavailable bridge leaves a visible saved project with retry/setup guidance.
   Unity defaults to port 39777, Three to 39778; manually edited addresses are kept.
   Use **接入设置** to change an existing project’s directory or address. Registration
   errors stay inside the dialog, and changing the source identity clears old bindings.
2. Inspect the actual live roles, clips and spatial anchors. A Unity inventory is
   separately labelled as discovered assets, not proof of usable performance.
3. Import a FilmPlan, establish a starter shot, or connect your own model API and
   describe the story. API setup takes the full Chat Completions URL, model ID and
   optional key for local models. It makes no call until Generate is clicked.
4. Check shootability and produce a low-resolution preview or full-resolution
   first cut with supplied audio and captions. Inspect the resulting video in the same window.
5. Change a shot's camera, subject, framing, time range or purpose. Submit a new
   revision; verified unaffected takes are reused. Previous versions remain intact.

The Workbench supports **supplied dialogue, music, sound effects and burned captions**.
Import WAV/MP3/M4A/OGG/FLAC (5 MiB per input, up to 10 minutes decoded audio). Audio
is decoded into immutable 48 kHz stereo PCM in the Workbench store. Film cues use
those returned content IDs and seconds from the final edited film, with source
trim, gain and fades. Mixing and source-range validation use actual decoded samples,
not misleading container duration or timestamps. A sound/caption-only revision
reuses all unchanged pictures. There is no automatic voice synthesis or lipsync;
those require compatible supplied performances. Technical verification is not
human acceptance of the animation or soundtrack.

## Ownership: filming must not dirty a source project

* **Install / upgrade** is an explicit one-time dependency change to commit.
* **Save a stage or film recipe** is intentional creative authoring to version.
* **Discover / preview / record / export** uses external production storage.
  It never installs the plugin or saves source scenes, prefabs or game scripts.

A bridge must declare an absolute `project.sourceRoot` before managed production.
The Workbench pins that root on connection and refuses overlap with its data store.
Client capture/edit/recovery paths reject recognized engine/project roots and
unignored Git source paths. Physical path resolution also rejects preexisting
symlink escapes, including a dangling ownership-lock link. Development outputs
explicitly ignored by Git are allowed, but never inside a registered game root.
`GET /api/storage` and the window show the owned output directory. Engine-managed
`Library/`, `.godot/` or equivalent caches are separate from product source changes.
This protects ordinary local workflow mistakes; it is not an OS sandbox against
another process deliberately replacing filesystem links during a write.

Film scripts can be explicitly downloaded with **导出拍摄脚本** for version control.
A script refers to immutable imported audio IDs in its project's media library;
sharing the JSON alone does not transfer that library. Local production receipts,
frames and audio remain in the store. Existing R2 videos remain reviewable; incomplete
legacy takes without source ownership cannot resume and require a new production.

## Unity installation and onboarding

Install `packages/com.gamedirector.unity` as an embedded Unity package (2022.3+).
Use `python3 tools/install_unity_package.py --source packages/com.gamedirector.unity
--project /path/to/game` to preview a scoped plan; add `--apply` to install it.
Requires Python 3.9+. The installer records package-owned file hashes, skips identical
files/receipts, preserves local edits and deletes only unchanged owned obsolete files.
It never changes ProjectSettings or authoring assets. Repeat install is zero-write.
A conflicting local package requires you to retain/merge that edit before upgrade.
Open **Tools → GameDirector → Director Workbench**. This is a supported native
EditorWindow, not an internal Unity browser API. It can discover/sync assets, create
an offline stage, refresh live bindings, submit a brief to the configured API and
show production status. The Film asset Inspector provides native shot editing; video review opens the shared browser
window.

Asset discovery is read-only and scoped to the chosen Assets folder. It records
GUID + local file ID, type, dependency hash, and clip duration. Imported sub-assets
retain distinct IDs after a rename. Scanning advances incrementally and can be
stopped; incomplete scans do not replace a previously synced inventory.

**Create offline stage from selected asset (writes new scene)** is an explicit
authoring action. It creates a new uniquely named scene under
`Assets/Scenes/GameDirector/`, copies the selected prefab's visual presentation,
removes gameplay scripts from that copy, binds it as `lead`, adds three camera
anchors and two lights, then saves/closes only that new scene. Existing scenes and
the source prefab are not saved or replaced. The prior active scene is restored. Unity cannot add a scene while an untitled scene
is loaded: that case is rejected before any folders or assets are written. The user
must name/save or close that scene themselves. A named scene with unsaved edits is
allowed and is not saved by stage creation.
Open the created stage in isolation before Play Mode. A game's global automatic
launcher must respect `OfflinePresentationScene`; the generic package cannot
guarantee arbitrary project-level startup scripts will cooperate.

GenericSceneAdapter without a supplied manifest now discovers role registries,
bound base-layer Animator states and location registries. A model without a
controller can still be framed but has no advertised animation states. Attaching
unrelated animation clips, retargeting, procedural animation, facial controls and
custom simulation still require compatible bindings or a project adapter.

## Film contract and invalidation

See `samples/films/sandring-greeting.json` for a real three-shot example.

* Film version 1: title, frameRate, width, height, scenes, optional audio/subtitles.
* Audio cue: id, mediaId, bus (dialogue/music/sfx), at, sourceStart, duration, volume,
  fadeIn, fadeOut. Timing is in edited-film seconds. At most 64 cues.
* Subtitle: start, end, text; ordered, nonoverlapping, at most 300 captions.
* Scene: stable id, performance cue list, ordered shots. Each scene resets to the
  adapter's initial state; cross-scene world continuity is not implicit.
* Shot: globally unique stable id, start/end in **scene performance time**, purpose,
  camera. The camera's movement begins at start; end-start determines its duration.
* A shot replays the scene from t=0 and records its prefix, then the edit trims to
  start/end. This preserves performance continuity across alternate cameras at the
  cost of extra rendering. Actors needed by the prefix must be available at t=0.
* Pixel cache identity includes the generated timeline, source manifest/fingerprint,
  frame rate and dimensions. Editorial purpose/title do not invalidate pixels.
  Changing performance invalidates coverage whose prefix includes the change;
  changing a camera invalidates that shot; source changes invalidate all affected
  takes. This is a conservative exact-input cache, not a semantic equivalence engine.
* A job snapshots its full request, endpoint and manifest before queueing. requestId
  retries return the same job; different inputs with the same ID fail. One worker
  serializes capture. Cancel stops only the owned take. A restart marks unfinished
  jobs Interrupted; explicit resume verifies source/pixels and keeps partial work.
* Complete verified frames can be re-encoded offline. Incomplete captures must match
  the original source fingerprint and replay into a preserved child attempt.

The bounds are 32 scenes, 120 shots, 10 minutes of edited output and 60 minutes of
capture including pre-roll. Technical media verification does not establish
dramatic clarity, visual appeal or human approval.

## Shared external Agent interface

Start the existing MCP stdio executable. Set `GAMEDIRECTOR_WORKBENCH` only if the
Workbench uses a nondefault local address. New tools:

| Tool | Shared operation |
| --- | --- |
| `gd_studio_media` | Import named local audio into a project, or list its media IDs |
| `gd_studio_state` | Projects, inventories, live manifest snapshots and jobs |
| `gd_studio_project` | Register a local project and refresh live bindings |
| `gd_studio_validate` | Validate a FilmPlan file against the live engine |
| `gd_studio_produce` | Submit an immutable FilmPlan with an idempotent request ID |
| `gd_studio_job` | Inspect, cancel or resume a managed job |

Old `gd_take` / `gd_edit` remain low-level operations and are not automatically
imported into the Workbench store. Use studio tools for managed production.

The built-in API director uses the same compiler and Submit method, with at most
two model generations to correct invalid plans. Completed proposals persist;
retrying their request ID does not call the model again. Failed/unacknowledged
network requests are not automatically retried. Keys never enter project files,
browser storage, reports or model prompts. Model output cannot run commands or
arbitrary engine methods. The HTTP service binds only loopback and checks Host,
Origin, Fetch Metadata and JSON content type before accepting browser mutations.

## Product decisions and remaining release work

One standalone Workbench should remain the full creative surface. Engine docks
provide contextual discovery, selection, binding and audition, plus common task
controls. This avoids three divergent editors and lets web/custom projects use the
same product. The next shipping unit is **one reliable project → picture → revision
loop with supplied sound**, followed by richer performance authoring and evidence-based visual review.

Unity and the Three/Sandring adapter are implemented. UE and Godot connectors are
not implemented or claimed by this Alpha; each needs its own stable asset IDs,
offline execution/restore contract and real installation/capture/restart tests.
Non-engine applications also need a visual adapter; inspecting arbitrary source
code alone does not make procedural behavior safely schedulable.

Before public release: clean-machine installers and dependency setup, API-provider
compatibility and real-provider acceptance, Windows runtime, two additional real
project onboarding trials, voice/lipsync and performance authoring, resource budgets for
large projects, and human review of narrative/visual quality. A unified UI does
not close these lanes by itself.

References: [Unity EditorWindow](https://docs.unity3d.com/ScriptReference/EditorWindow.html),
[Unity GUID and local file identifiers](https://docs.unity3d.com/ScriptReference/AssetDatabase.TryGetGUIDAndLocalFileIdentifier.html),
[Chat Completions protocol](https://developers.openai.com/api/reference/resources/chat).
