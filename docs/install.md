# Distribution, installation and runtime layout

This document describes how GameDirector is packaged, installed and launched,
using one declarative manifest and one shared staging pipeline. Published package
manager channels are planned; platform archives are the current installation path.

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

## Release artifacts

### Full standalone distribution (primary easy install)

```
python3 tools/package_release.py --out out/dist-win-x64 --runtime win-x64
```

Produces the staging folder plus `gamedirector-<version>-<rid>.zip` (Windows)
or `.tar.gz` (macOS/Linux). Each full archive has one enclosing folder named
like the archive, without its extension; `curl -fsSL <url> | tar -xz` creates
only that folder. All three entrypoints share one runtime:

```
<dist>/distribution.json          runtime copy of the manifest
<dist>/manifest.json              release manifest: version, rid, git state, SHA-256 of every file
<dist>/bin/gd(.exe)               CLI
<dist>/bin/gamedirector-mcp(.exe)  MCP stdio server
<dist>/bin/gamedirector-workbench(.exe)   Workbench (wwwroot + Fonts + media data)
<dist>/bin/gamedirector.runtime.json     shared version/rid/protocol/apps/executables/file hashes
<dist>/unity/com.gamedirector.unity      UPM source; installer fills Tools~/bin from archive bin/
<dist>/media/media-distributions.json     media installer data
<dist>/godot/addons/gamedirector          pure-GDScript Godot addon
<dist>/docs, samples, skills, three, adapters …
<dist>/Start GameDirector.cmd|.command
```

Everything is `dotnet publish -r <rid> --self-contained true`, so no .NET, Python
or Node is needed by end users, and **any new project dependency (e.g.
GameDirector.Decisions) is published transitively** — there is no frozen file
list to maintain. Publishing rejects any differing overlapping file rather
than overwriting it. Shared NuGet versions are declared in `Directory.Build.props`;
packaging uses only the local NuGet cache.

### Unity UPM bundle (per platform)

```
python3 tools/package_unity_candidate.py --out out/upm --runtime osx-arm64   # or omit --runtime for all platforms
```

Produces `com.gamedirector.unity-<version>-<rid>.tgz` per platform with `package/`
as the UPM-required archive root, the SAME shared runtime under `Tools~/bin`
(gd, MCP, Workbench and Fonts included), media data under `Tools~/media`,
and `Tools~/bin/gamedirector.runtime.json`
for the launcher to validate. The tgz records Unix execute bits even when staged
on Windows (the declared `unixExecutables` list drives the modes).

The UPM tgz is an offline runtime: after **Add package from tarball**, Unity needs
no SDK and no network for the workbench itself. A **source-only Git UPM install
carries no runtime**; the launcher then shows a clear message pointing at the full
platform tarball from Releases and still offers to pick a runtime folder manually.

### Godot addon ZIP

`tools/package_release.py` also emits `gamedirector-godot-<version>.zip` beside
its full archive. Extract it at a Godot project root: entries are under
`addons/gamedirector/`, plus `LICENSE`. Script `.uid` files ship; generated
imports and caches do not. The full archive carries the uncompressed addon;
the standalone ZIP is built once beside the full archives. Users still need the full platform distribution for `gd`
and its verified MCP companion. See [the Godot guide](godot.md).

## CLI surface (gd)

```
gd workbench --open        open a verified running instance, or launch the sibling app
gd workbench [args...]     launch the sibling workbench app (args/stdio/exit code preserved)
gd mcp [args...]           launch the sibling MCP stdio server (stdout stays protocol-clean)
gd unity install --project <Unity project> [--source <package>] [--apply]
gd media status
gd media install --option <id> [--descriptor bundle.json]
```

For headless/SSH use, set `GAMEDIRECTOR_BROWSER=none` with `gd workbench --open` to print the one-time sign-in URL to stdout without launching a browser.

`gd unity install` defaults to **plan-only**; `--apply` writes. The default source
is the distribution's staged `unity/com.gamedirector.unity` (from the manifest —
never the developer checkout). The installer verifies the archive's `bin/`
and fills the package's `Tools~/bin` without mutating the archive or staging
source. Installed package contents are byte-identical to the corresponding tgz,
except for the installer's ownership receipt. `--source` also accepts an already
unpacked complete UPM package. Repeating an identical install performs **zero
writes**; local modifications or upstream conflicts **abort before any write**;
receipts (`.gamedirector-install.json`) are validated (malformed JSON, escaping
paths and bad hashes are honest errors); symlinked/junctioned destinations and
path components are refused. The same semantics live in
`tools/install_unity_package.py` for Python environments; both are pinned by
contract tests (`tools/test_unity_installer.py` and
`src/GameDirector.Core.Tests/UnityPackageInstallerTests.cs`).

`gd workbench` / `gd mcp` additionally gate on **runtime integrity**: the
shared `bin/gamedirector.runtime.json` declares safe relative paths and app
entrypoints. Every payload file must be declared and hash-match; missing,
modified, unlisted or linked files block launch. The native host alone cannot
vouch for the managed assemblies and libraries it loads. The same manifest and
complete-tree verification apply inside the Unity tgz.

## Unity MCP configuration

Choose **Window → GameDirector → Copy MCP Config** (also under **Tools**).
It verifies the bundled shared runtime and copies:

```json
{"mcpServers":{"gamedirector":{"command":"<absolute bundled gd path>","args":["mcp"]}}}
```

Paste this into your agent's MCP settings. No `env` is needed: Unity's bridge
is the default endpoint. The command path survives Unity restarts. Package-cache
paths, or the existing content-addressed runtime mirror used for Unix permission
repair, can change after a package update; the confirmation dialog names the
actual source path and tells you to copy the config again after updating.

## Workbench caller authority

The Workbench is a **single-user local service**, not a machine-wide service.
Host/Origin/Fetch-Metadata checks run first but cannot authenticate local processes.
Every instance generates a new 256-bit random `token` in `<store>/.service.json`:
Unix creates a private temp file, sets **0600 before writing**, then atomically
renames it into place; Windows inherits the user-only ACL of the per-user
`%LOCALAPPDATA%` store (no custom ACL code). A custom `--data`/`GAMEDIRECTOR_HOME`
on Windows must also be a private user-owned directory, never a shared store.
Do not print, share, commit or put the token in a URL.

Static UI assets and **GET `/api/health`** remain public. Every other `/api/*`
request needs `Authorization: Bearer <token>` or an instance session cookie,
except **POST `/api/session`**, whose credential is a single-use code.
**POST `/api/session/code` is Bearer-only**, even for an authenticated browser.
Missing/wrong credentials return HTTP 401 with `code: unauthorized`, `retryable: false`.
MCP and `gd workbench --open` use `WorkbenchLocator`: a health/instance-verified
canonical descriptor supplies endpoint **and token**. An explicit
`GAMEDIRECTOR_WORKBENCH` instead uses only its paired `GAMEDIRECTOR_WORKBENCH_TOKEN`;
it never borrows another instance's token. Unity uses its verified discovered
descriptor or the same explicit environment pair, never EditorPrefs for secrets.

Unity's browser buttons, `gd workbench --open`, and Workbench `--open` mint a
random, **60-second, single-use** code via Bearer POST, then open `/?code=<code>`.
`app.js` strips that code immediately with `history.replaceState` and exchanges
it same-origin for an **HttpOnly, SameSite=Strict, Path=/** cookie. The token never
reaches browser JavaScript. Codes and sessions live only in memory; codes expire
at 60 seconds, sessions last for the instance (browser-session cookie, no persistent
expiry), and restart invalidates both. Without a session the UI displays one line:
“Open the Workbench from Unity or run gd workbench --open.” Bridges are unchanged.

## Unity launcher behavior

### Per-user data root (one layout, every host)

All per-user data hangs off ONE root resolved by `GameDirectorPaths` in Core:
`GAMEDIRECTOR_HOME` (absolute override) → macOS `~/Library/Application Support/GameDirector`
→ Windows `%LocalApplicationData%\GameDirector` → freedesktop `$XDG_DATA_HOME/GameDirector`
(or `~/.local/share/GameDirector`). Below it: `Workbench/` (store + `.service.json`),
`RuntimeMirror/`, `media/`. Unity Mono and .NET 8 resolve `LocalApplicationData`
differently on macOS, so the root is computed from the user profile there — a
Unity-launched and a standalone Workbench always share one store.

**Legacy macOS store.** Unity launchers before this unification stored the Workbench
store at `~/.local/share/GameDirector/Workbench`. Nothing is migrated automatically
(user data is never moved silently): when a Workbench on the default store detects
that legacy folder (it has `.identity.json`, the canonical store does not), it logs
one warning and reports `legacyStore` in `/api/health`. To keep the old projects,
stop the Workbench, move `~/.local/share/GameDirector/Workbench` to
`~/Library/Application Support/GameDirector/Workbench`, and start it again.

`DirectorWorkbenchLauncher` (Editor):

1. Reuses a healthy instance (`.service.json` discovery + product/protocol/instance check), retaining its token only in memory.
2. Resolves the bundled runtime via `PackageInfo` and the packaged copy of
   `distribution.json` → `Tools~/bin`.
3. Validates `gamedirector.runtime.json`: product, **rid must match the editor
   platform** (`win-x64`/`osx-arm64`/`osx-x64`/`linux-x64`), **protocol must equal 1**.
   Mismatches fail with actionable Chinese/English messages, not hangs.
4. **Verifies declared integrity before the executable or any mirror is used**:
   every path in the manifest's `sha256` map must stay below the runtime folder
   (rooted/escaping entries are hard errors), must exist, and must hash-match;
   every app and executable must have a hash; unlisted or linked payloads are
   refused. Declared `apps` and `executables` paths are bounds-checked the same way. Failures tell the user to reinstall the package.
   A user-picked legacy folder without a manifest keeps its previous behavior.
5. On Unix, if the apphost lost its execute bit (cross-built archive): chmod in
   place for embedded packages; when the package lives in **PackageCache (never
   written)**, the runtime is mirrored to `RuntimeMirror/<content-hash>/` inside
   the per-user data root (see above), chmodded there, and launched from the mirror.
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
  hash-verified by this repo's contract tests. Only complete, verified media
  units are supported; hashes and URLs are never invented.
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
  The descriptor must be a non-empty regular file without symlinks/reparse points;
  installation is an **explicit authenticated action**. Descriptor hashes verify
  integrity, **not provenance**: selecting the descriptor authorizes executing its
  staged binaries. Only install bundles you trust.
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
- The Unity launcher does not inject `GAMEDIRECTOR_FFMPEG`/`GAMEDIRECTOR_FFPROBE`:
  `MediaTools.Resolve` already probes the runtime's `media`/`../media` folders and
  `GameDirectorPaths.Media()`. Real user-provided overrides remain intact.
- An explicit `GAMEDIRECTOR_FFMPEG`/`GAMEDIRECTOR_FFPROBE` pointing at a missing
  file now fails honestly ("points to … which does not exist") instead of
  silently falling back.

Nothing alters system configuration, user PATH, game scenes or credentials.

## Provisioning a Three game for GameDirector (dev)

From the GameDirector checkout, with Git, tar, Node and npm available:

```sh
export GAMEDIRECTOR_BROWSER=none
python3 tools/provision_three_game.py --repo <path-to-your-game> --sha <commit> --name <game> --paths src public package.json package-lock.json tsconfig.json
```

The local object store is the only source: `git archive <fullsha> -- <paths>`
is piped into tar, then `npm ci --omit=dev --ignore-scripts` runs in the extracted
snapshot. The tool never checks out, fetches, creates a worktree, or writes in
the source repo. Missing paths, archive symlinks/submodules and local/link
package dependencies refuse rather than read from the active checkout.

The verified tree is published at `~/.gamedirector-dev/games/<name>/<fullsha>/`
and made read-only, including `node_modules`. Its `.gamedirector-provenance.json`
records the full commit/tree, canonical selected paths, unchanged lock SHA-256,
Node/npm/Three versions, source repo path and npm engines warnings. The repo
path is audit metadata, not identity (identity uses commit/tree). An identical
re-run is a read-only no-op with byte-identical provenance; conflicting paths,
lock or installed Three refuse. The resolved lock's Three version must match
`node_modules/three/package.json` before publication. Scripts are disabled;
npm install/network/engines failures publish nothing. Scratch/cache are in
`/tmp` and cleaned on exit. This is dev tooling, not the product installer or
its per-user data layout; Sandring baselines live separately in
`~/.gamedirector-dev/baselines/`.

**Node engines:** if the game's `engines` range excludes the installed Node,
npm emits `EBADENGINE` warnings; the tool records them and continues. If npm
refuses, stop and report it; do not install another Node or override the refusal.
