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


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--out", required=True, help="fresh output directory")
    parser.add_argument("--runtime", required=True, help="target RID from the distribution manifest")
    args = parser.parse_args()

    root = dist.REPO
    config = dist.load_config(root)
    rid = args.runtime
    if rid not in config["supportedPlatforms"]:
        raise SystemExit(f"unsupported runtime {rid}; supported: {', '.join(config['supportedPlatforms'])}")
    version = dist.read_version(root)
    out = Path(args.out).resolve()
    archive = Path(str(out) + ("." + config["platforms"][rid]["archive"]))
    if out.exists() or archive.exists():
        raise SystemExit("Output exists; use a fresh candidate directory")
    out.mkdir(parents=True)

    # Apps: gd, gamedirector-mcp, gamedirector-workbench as sibling native apps.
    for app_key in ("cli", "mcp", "workbench"):
        folder = dist.publish_app(root, config, app_key, rid, out / config["apps"][app_key]["folder"])
        dist.write_runtime_manifest(folder, config, app_key, rid, version, version)
        dist.apply_unix_modes(config, rid, folder)

    # The Unity package ready for `gd unity install` (same staging as the UPM tgz).
    dist.stage_unity_package(root, config, rid, out / config["unity"]["distributionFolder"], version)

    # Media installer data + the declarative manifest the runtime reads.
    dist.media_manifest_copy(root, config, out)
    dist.write_distribution_manifest_copy(root, out)

    shutil.copytree(root / "docs", out / "docs")
    shutil.copy2(root / "README.md", out / "README.md")
    shutil.copytree(root / "samples", out / "samples",
                    ignore=shutil.ignore_patterns("__pycache__", ".DS_Store"))
    shutil.copytree(root / "skills", out / "skills")
    # Retain the existing second-engine integration and optional game adapter.
    shutil.copytree(root / "packages/gamedirector-three", out / "three",
                    ignore=shutil.ignore_patterns("node_modules", ".DS_Store"))
    shutil.copytree(root / "adapters/sandring", out / "adapters/sandring")
    shutil.copytree(root / "adapters/cdrebirth/com.gamedirector.cdrebirth", out / "unity/com.gamedirector.cdrebirth")
    (out / "adapters/cdrebirth").mkdir(parents=True)
    shutil.copy2(root / "adapters/cdrebirth/performances.json", out / "adapters/cdrebirth/performances.json")
    shutil.copytree(root / "adapters/cdrebirth/manifests", out / "adapters/cdrebirth/manifests")

    exe = config["apps"]["workbench"]["executable"] + config["platforms"][rid]["executableExtension"]
    (out / "INSTALL.md").write_text(f"""# GameDirector {version} — {rid}

Self-contained CLI/MCP/Workbench: no .NET, Python or Node required.
The optional Three engine integration retains its own Node/browser prerequisites.

- `cli/gd{config['platforms'][rid]['executableExtension']} doctor` — environment check
- `cli/gd{config['platforms'][rid]['executableExtension']} workbench --open true` — browser workbench (or use the Start launcher)
- `cli/gd{config['platforms'][rid]['executableExtension']} mcp` — MCP stdio server for LLM hosts
- `cli/gd{config['platforms'][rid]['executableExtension']} unity install --project <Unity project>` — plan a Unity package
  install from `unity/com.gamedirector.unity` (add `--apply` to write). Repeating an identical
  install performs zero writes; local edits stop the install before any write.
- `cli/gd{config['platforms'][rid]['executableExtension']} media status` — media tool situation; `media install --option <id>`
  installs the complete ffmpeg+ffprobe unit (a package-manager strategy, or an offline
  verified bundle via `--descriptor bundle.json`).

Data stays in your per-user application data folder. See docs/09-product-workbench.md and
docs/16-distribution-and-install.md.
""", encoding="utf-8")
    command = out / "Start GameDirector.command"
    command.write_text(f'#!/bin/sh\nset -eu\ncd "$(dirname "$0")"\nexec ./workbench/{config["apps"]["workbench"]["executable"]} --open true\n', encoding="utf-8")
    command.chmod(0o755)
    (out / "Start GameDirector.cmd").write_text(
        "@echo off\r\ncd /d \"%~dp0\"\r\nstart \"\" workbench\\" + exe + " --open true\r\n", encoding="utf-8")

    state = dist.git_state(root)
    release = dict(product=config["product"], version=version, rid=rid,
                   protocolVersion=config["protocolVersion"], **state,
                   status="local candidate; not a published release",
                   files=dist.hash_tree(out))
    (out / config["releaseManifestFile"]).write_text(json.dumps(release, indent=2) + "\n", encoding="utf-8")

    dist.make_archive(out, archive, config, rid)
    print(json.dumps({"distribution": str(out), "archive": str(archive),
                      "version": version, "rid": rid, "sha256": dist.sha256(archive)}))


if __name__ == "__main__":
    main()
