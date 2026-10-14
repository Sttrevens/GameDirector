"""Build a self-contained full GameDirector distribution for one platform.

Layout and platforms come from tools/distribution.json; the version comes from
Directory.Build.props (asserted equal to the Unity package version). Every app
is published self-contained per RID, so no .NET runtime, Python or Node is
needed by end users. The Unity package (with its Tools~/ runtime) is staged by
the same logic as the standalone UPM candidate.

  python3 tools/package_release.py --out out/dist --runtime win-x64

Does not commit, tag or publish."""
import argparse
import json
import shutil
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import gamedirector_dist as dist
import stage_godot


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--out", required=True, help="fresh output directory")
    parser.add_argument("--runtime", required=True, help="target RID from the distribution manifest")
    parser.add_argument("--release", action="store_true", help="stamp a release build; requires a clean worktree")
    args = parser.parse_args()

    root = dist.REPO
    config = dist.load_config(root)
    state = dist.git_state(root)
    status = dist.release_status(state, args.release)
    rid = args.runtime
    if rid not in config["supportedPlatforms"]:
        raise SystemExit(f"unsupported runtime {rid}; supported: {', '.join(config['supportedPlatforms'])}")
    version = dist.read_version(root)
    dist.verify_core_dll(root)
    out = Path(args.out).resolve()
    archive = out.parent / dist.archive_name(config, version, rid)
    if out.exists() or archive.exists():
        raise SystemExit("Output exists; use a fresh candidate directory")
    out.mkdir(parents=True)

    # Three native entrypoints share one self-contained runtime and hash manifest.
    dist.publish_runtime(root, config, rid, out / config["runtimeFolder"], version)

    # No second runtime: gd unity install materializes Tools~/bin from archive bin/.
    dist.stage_unity_package(root, config, rid, out / config["unity"]["distributionFolder"], version,
                             include_runtime=False)

    # Media installer data + the declarative manifest the runtime reads.
    dist.media_manifest_copy(root, config, out)
    dist.write_distribution_manifest_copy(root, out)

    shutil.copytree(root / "docs", out / "docs", ignore=shutil.ignore_patterns("history", ".DS_Store"))
    shutil.copy2(root / "README.md", out / "README.md")
    dist.copy_release_documents(root, out)
    shutil.copytree(root / "samples", out / "samples",
                    ignore=stage_godot.IGNORE)
    shutil.copytree(root / "skills", out / "skills")
    # Retain the optional Three engine integration and public sample adapters.
    shutil.copytree(root / "packages/gamedirector-three", out / "three",
                    ignore=shutil.ignore_patterns("node_modules", ".DS_Store"))
    stage_godot.stage(root, out)
    addon_name = config["godotArchiveName"].format(version=version)
    standalone_addon = out.parent / addon_name
    if not standalone_addon.exists():
        stage_godot.addon_archive(root, standalone_addon)
    shutil.copytree(root / "adapters/sandring", out / "adapters/sandring")

    cli = config["apps"]["cli"]["executable"] + config["platforms"][rid]["executableExtension"]
    cli_path = config["runtimeFolder"] + "/" + cli
    (out / "INSTALL.md").write_text(f"""# GameDirector {version} — {rid}

Self-contained CLI/MCP/Workbench: no .NET, Python or Node required.
Godot uses the bundled .NET supervisor and a pure-GDScript addon (copy `godot/addons/gamedirector` into your project). Install Godot 4.6 or newer (tested on 4.6.2) separately; no Node/Python or Godot .NET build is required.
The optional Three engine integration retains its own Node/browser prerequisites.

- `{cli_path} doctor` — environment check
- `{cli_path} workbench --open` — browser workbench (or use the Start launcher)
- `{cli_path} mcp` — MCP stdio server for LLM hosts
- `{cli_path} unity install --project <Unity project>` — plan a Unity package
  install from `unity/com.gamedirector.unity`, including the archive's shared
  `{config['runtimeFolder']}/` under `{dist.unity_runtime_folder(config)}` (add `--apply` to write).
  The installed package is byte-identical to the platform tgz (except the install receipt).
  Repeating an identical install performs zero writes; local edits stop it before any write.
- `{cli_path} godot serve --project <Godot project> --stage res://stage.tscn --godot <binary>` — local engine bridge, default port 39779 (`--port` overrides). See docs/godot.md.
- `{cli_path} media status` — media tool situation; `media install --option <id>`
  installs the complete ffmpeg+ffprobe unit (a package-manager strategy, or an offline
  verified bundle via `--descriptor bundle.json`).

Data stays in your per-user application data folder. See docs/workbench.md and
docs/install.md.
""", encoding="utf-8")
    command = out / "Start GameDirector.command"
    command.write_text(f'#!/bin/sh\nset -eu\ncd "$(dirname "$0")"\nexec ./{config["runtimeFolder"]}/{config["apps"]["cli"]["executable"]} workbench --open\n', encoding="utf-8")
    command.chmod(0o755)
    (out / "Start GameDirector.cmd").write_text(
        "@echo off\r\ncd /d \"%~dp0\"\r\n\"" + cli_path.replace("/", "\\") + "\" workbench --open\r\n", encoding="utf-8")

    release = dict(product=config["product"], version=version, rid=rid,
                   protocolVersion=config["protocolVersion"], **state,
                   status=status,
                   files=dist.hash_tree(out))
    (out / config["releaseManifestFile"]).write_text(json.dumps(release, indent=2) + "\n", encoding="utf-8")

    dist.assert_no_local_paths(out, root)
    dist.make_archive(out, archive, config, rid)
    print(json.dumps({"distribution": str(out), "archive": str(archive), "godotAddon": str(standalone_addon),
                      "version": version, "rid": rid, "sha256": dist.sha256(archive), "godotAddonSha256": dist.sha256(standalone_addon)}))


if __name__ == "__main__":
    main()
