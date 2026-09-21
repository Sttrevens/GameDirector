# 15 — Distribution, installation and runtime layout

Status: implemented 2026-09-20. This document is the contract for how GameDirector
is packaged, installed and launched. It replaces the ad-hoc candidate scripts with
one declarative manifest and one shared staging pipeline.

## Sources of truth

| Thing | File | Notes |
| --- | --- | --- |
| Platforms, layout, apps, Unity/media folders | `tools/distribution.json` | The single declarative distribution manifest. Consumed by the packaging scripts **and** by the shipped runtime (`GameDirector.Client.DistributionManifest` reads the copy staged at the distribution root). |
| Product version | `Directory.Build.props` → `<Version>` | Flows into assembly informational version; the runtime reads it via `DistributionManifest.ProductVersion`. Scripts assert it equals `packages/com.gamedirector.unity/package.json`. No copied literals. |
| Media strategies | `src/GameDirector.Client/media-distributions.json` | Ships with every app and inside the UPM bundle (`Tools~/media`). |
| Shared staging logic | `tools/gamedirector_dist.py` | Used by both packaging entry points. |

Supported platform RIDs are declared in the manifest: `win-x64`, `osx-arm64`,
`osx-x64`, `linux-x64`. Archive form is declared per platform: `zip` on Windows,
`tar.gz` elsewhere (executable bits recorded in the archive).

## The two artifacts

### Full standalone distribution (primary easy install)

```
python3 tools/package_release.py --out out/dist-win-x64 --runtime win-x64
```

Produces `out/dist-win-x64/` plus a single archive (`zip`/`tar.gz`):

```
<dist>/distribution.json          runtime copy of the manifest
<dist>/manifest.json              release manifest: version, rid, git state, SHA-256 of every file
<dist>/cli/gd(.exe)               self-contained CLI
<dist>/mcp/gamedirector-mcp       self-contained MCP stdio server
<dist>/workbench/gamedirector-workbench   self-contained workbench (wwwroot + Fonts + media data)
<dist>/<app>/gamedirector.runtime.json    per-app runtime manifest (version/rid/protocol/executables/file hashes)
<dist>/unity/com.gamedirector.unity       UPM package incl. Tools~/Workbench runtime for this platform
<dist>/media/media-distributions.json     media installer data
<dist>/docs, samples, skills, three, adapters …
<dist>/Start GameDirector.cmd|.command
```

Everything is `dotnet publish -r <rid> --self-contained true`, so no .NET, Python
or Node is needed by end users, and **any new project dependency (e.g.
GameDirector.Decisions) is published transitively** — there is no frozen file
list to maintain.

### Unity UPM bundle (per platform)

```
python3 tools/package_unity_candidate.py --out out/upm --runtime osx-arm64   # or omit --runtime for all platforms
```

Produces `com.gamedirector.unity-<version>-<rid>.tgz` per platform with `package/`
as the archive root, the complete runtime under `Tools~/Workbench` (Fonts included),
media data under `Tools~/media`, and `Tools~/Workbench/gamedirector.runtime.json`
for the launcher to validate. The tgz records Unix execute bits even when staged
on Windows (the declared `unixExecutables` list drives the modes).

The UPM tgz is an offline runtime: after **Add package from tarball**, Unity needs
no SDK and no network for the workbench itself. A **source-only Git UPM install
carries no runtime**; the launcher then shows a clear message pointing at the full
platform tarball (no invented hosted URL — obtain it from whoever packages the
release) and still offers to pick a runtime folder manually.

## CLI surface (gd)

```
gd workbench [args...]     launch the sibling workbench app (args/stdio/exit code preserved)
gd mcp [args...]           launch the sibling MCP stdio server (stdout stays protocol-clean)
gd unity install --project <Unity project> [--source <package>] [--apply]
gd media status
gd media install --option <id> [--descriptor bundle.json]
```

`gd unity install` defaults to **plan-only**; `--apply` writes. The default source
is the distribution's staged `unity/com.gamedirector.unity` (from the manifest —
never the developer checkout). Repeating an identical install performs **zero
writes**; local modifications or upstream conflicts **abort before any write**;
receipts (`.gamedirector-install.json`) are validated (malformed JSON, escaping
paths and bad hashes are honest errors); symlinked/junctioned destinations and
path components are refused. The same semantics live in
`tools/install_unity_package.py` for Python environments; both are pinned by
contract tests (`tools/test_unity_installer.py` and
`src/GameDirector.Core.Tests/UnityPackageInstallerTests.cs`).

`gd workbench` / `gd mcp` additionally gate on **runtime integrity**: the
companion app's `gamedirector.runtime.json` must declare safe relative paths,
and every file in its `sha256` map must hash-match — the native host alone
cannot vouch for the managed assemblies it loads. A modified or missing
payload file fails the launch with a reinstall message.

## Unity launcher behavior

`DirectorWorkbenchLauncher` (Editor):

1. Reuses a healthy instance (`.service.json` discovery + protocol check).
2. Resolves the bundled runtime via `PackageInfo` → `Tools~/Workbench`.
3. Validates `gamedirector.runtime.json`: product, **rid must match the editor
   platform** (`win-x64`/`osx-arm64`/`osx-x64`/`linux-x64`), **protocol must equal 1**.
   Mismatches fail with actionable Chinese/English messages, not hangs.
4. **Verifies declared integrity before the executable or any mirror is used**:
   every path in the manifest's `sha256` map must stay below the runtime folder
   (rooted/escaping entries are hard errors), must exist, and must hash-match;
   the entry executable must be declared; declared `executables` paths are
   bounds-checked the same way. Failures tell the user to reinstall the package.
   A user-picked legacy folder without a manifest keeps its previous behavior.
5. On Unix, if the apphost lost its execute bit (cross-built archive): chmod in
   place for embedded packages; when the package lives in **PackageCache (never
   written)**, the runtime is mirrored to `%LocalApplicationData%/GameDirector/
   RuntimeMirror/<content-hash>/`, chmodded there, and launched from the mirror.
   A reused `.complete` mirror is trusted **only after its declared hashes
   re-verify**; a stale or tampered mirror is preserved under a separate name,
   and a verified fresh directory is published under a cross-process lock.
   No copying occurs through existing mirror paths. The marker is written only after the fresh mirror
   itself verifies. Timed-out `chmod`/`test -x` helpers are killed and reaped,
   never leaked.
6. Starts `--port 0 --data <per-user folder>`, logs under the data folder, waits
   for the service descriptor.

## Media tools: the complete unit only

The install unit is **ffmpeg + ffprobe together** (with libx264/libass for the
default profile). Rules:

- `media-distributions.json` declares package-manager strategies (winget, brew)
  with per-platform data, timeouts and license text. Entries that cannot provide
  both tools are rejected at manifest parse time — nothing partial is ever
  advertised as ready.
- **Curated `downloads` are intentionally empty** until a complete archive is
  hash-verified by this repo's contract tests. The previous ffmpeg-only Mac pin
  (no ffprobe) was removed. Hashes/URLs are never invented.
- **Offline user bundle**: a descriptor JSON pins every tool file's SHA-256
  (optionally inside a pinned `.zip`/`.tar.gz`). Every hash is verified into a
  staging folder before anything lands in the per-user media folder; archives
  are extracted by declared layout only (no traversal). The **staged unit must
  then prove the full encoding contract** (`MediaTools.EnsureEncoding` run
  against the exact staged binaries — never env overrides) before activation,
  so a hash-clean but non-working bundle leaves the previous pair byte-identical.
  Activation moves the previous tools into a backup folder first and **rolls
  back byte-for-byte on any error** (a backup that could not be restored is
  preserved on disk and named in the error). Unix installs are chmodded;
  staging/backup scratch is cleaned up. CLI: `gd media install --option
  user-bundle --descriptor bundle.json`.
  Workbench: enter the descriptor's full path in the offline bundle field, then use the
  "user-bundle" install option. The API accepts an optional `descriptorPath`;
  omitting it retains the conventional `<data>/media-bundle.json` location.
- Installs are **serialized** process-wide **and across processes**: the CLI and
  the Workbench share one per-user media folder, so an OS-level file lock
  (`<media>/.install.lock`, released by the OS if a holder dies) serializes
  them. Package-manager runs have hard timeouts and **drain both output streams
  in every outcome** — success, timeout kill and user cancellation — so the
  receipt always carries what the manager printed; the tree is killed on
  cancel. Every install ends with a full re-verification (`EnsureEncoding`:
  encoder, subtitle filters, pinned font, `ffprobe -version`) plus cache
  invalidation. A receipt JSON is written to the logs folder in every outcome
  **including cancellation**; failures name the receipt.
- An explicit `GAMEDIRECTOR_FFMPEG`/`GAMEDIRECTOR_FFPROBE` pointing at a missing
  file now fails honestly ("points to … which does not exist") instead of
  silently falling back.

Nothing alters system configuration, user PATH, game scenes or credentials.

## Validation performed (win-x64, 2026-09-20)

- `dotnet test src/GameDirector.Core.Tests` — 107 green, including install
  dry-run/no-op/upgrade/conflict/traversal/symlink, manifest/schema/runtime
  mismatch, CLI launcher exit-code/cancellation, media unit completeness,
  bundle hash gating and archive layout, staged-validation failure leaving the
  old pair untouched, activation rollback, cross-process install lock, and
  runtime integrity gates (CLI full-tree, Unity declared hashes).
- `python tools/test_unity_installer.py` — Python contract still green.
- `python tools/test_archive_layout.py` — archive modes/no-duplicate members.
- Real artifacts: `out/dist-win-x64.zip` and `out/upm-win-x64/` built on this
  machine; `gd.exe unity install` plan→apply→repeat-zero-writes, `gd.exe mcp`
  clean stdio exit, `gd.exe media status` options listing all exercised from the
  packaged binaries.

Not claimed: macOS/Linux runtime validation (no devices here); the Unity Editor
launch path compiles against Unity APIs and follows the existing patterns but
needs the coordinator's Unity validation environment for a live run.
