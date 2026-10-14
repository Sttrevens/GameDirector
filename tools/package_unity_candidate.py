"""Build the Unity-first UPM candidate: a self-contained .tgz per platform.

The package carries gd, MCP and Workbench in one runtime under Tools~/bin
(self-contained publish for the RID, Fonts included), the media installer data
under Tools~/media, and a gamedirector.runtime.json the Unity launcher
validates (platform + protocol) before launching. Staging is shared with the
full distribution via tools/gamedirector_dist.py — same layout, same manifests.
FFmpeg/ffprobe stay external: no complete curated download is hash-verified
yet, and incomplete bundles are never shipped as ready; users can install the
complete unit from the workbench (package-manager strategy or an offline
verified bundle descriptor). No game adapters, credentials or store are shipped.

  python3 tools/package_unity_candidate.py --out out/upm --runtime win-x64

Never tags or publishes."""
import argparse
import json
import shutil
import sys
import tempfile
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import gamedirector_dist as dist


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--out", required=True)
    parser.add_argument("--release", action="store_true", help="stamp a release build; requires a clean worktree")
    parser.add_argument("--runtime", default=None,
                        help="target RID from the distribution manifest (default: all supported platforms)")
    parser.add_argument("--runtime-source", type=Path,
                        help="reuse a verified bin/ for --runtime (same bytes as full archive)")
    args = parser.parse_args()
    if args.runtime_source is not None and args.runtime is None:
        parser.error("--runtime-source requires --runtime")

    root = dist.REPO
    config = dist.load_config(root)
    rids = [args.runtime] if args.runtime else list(config["supportedPlatforms"])
    for rid in rids:
        if rid not in config["supportedPlatforms"]:
            raise SystemExit(f"unsupported runtime {rid}; supported: {', '.join(config['supportedPlatforms'])}")
    version = dist.read_version(root)
    dist.verify_core_dll(root)
    out = Path(args.out).resolve()
    if out.exists():
        raise SystemExit("Candidate already exists; choose a new output.")
    out.mkdir(parents=True)

    packages = []
    for rid in rids:
        name = dist.archive_name(config, version, rid, unity=True)
        target = out / name
        with tempfile.TemporaryDirectory(prefix="gd-unity-package-") as temporary:
            package = dist.stage_unity_package(root, config, rid, Path(temporary) / "package", version,
                                                runtime_source=args.runtime_source)
            dist.assert_no_local_paths(package, root)
            # tgz with exec bits recorded; Unity extracts under PackageCache.
            dist.make_unity_archive(package, target, config, rid)
        packages.append({"file": name, "rid": rid, "sha256": dist.sha256(target)})

    rid_lines = "\n".join(f"- {p['rid']}: `{p['file']}`" for p in packages)
    runtime_folder = dist.unity_runtime_folder(config).as_posix()
    (out / "INSTALL.md").write_text(f"""# GameDirector Unity platform package (shared, self-contained runtime)

Version {version}. The macOS runtime is not signed or notarized; see docs/install.md for system security prompts.
Each tgz supports one platform. Choose the platform running your Unity Editor:

{rid_lines}

1. In Unity Package Manager, choose **Add package from tarball** and select your platform's tgz.
2. Save your current scene, then choose **Tools → GameDirector → Create Independent Example**.
3. Select the Film asset and click **Start / connect Workbench** in the Inspector.
   The backend is included; no .NET SDK is needed. The launcher checks the bundled
   runtime's platform and protocol ({runtime_folder}/{config['runtimeManifestFile']})
   before starting it and reports mismatches explicitly. Unix execute bits lost during
   cross-platform packaging are repaired automatically. Packages in PackageCache are
   mirrored to your user data folder first; PackageCache is never modified.
4. Open the example offline stage, enter Play Mode, connect the film, import the example sound and check readiness.

## Encoding tools (the complete ffmpeg + ffprobe unit)

FFmpeg is not bundled. When you first need to render, choose an installation method
in the readiness check or media panel: your platform's package manager (winget or
Homebrew, including ffprobe), or a verified offline bundle. For an offline bundle,
enter the full path to your trusted bundle descriptor JSON and click Install.
The descriptor must supply paths and SHA-256 hashes for both ffmpeg and ffprobe.
Incomplete bundles are never treated as ready. You can also pass absolute paths to
compatible programs through `GAMEDIRECTOR_FFMPEG` / `GAMEDIRECTOR_FFPROBE` in Unity's
launch environment. Invalid overrides fail explicitly rather than falling back silently.

gd, MCP and the Workbench share one runtime in {runtime_folder}.
Choose Window → GameDirector → Copy MCP Config, then paste into your agent's MCP settings.
Noto Sans CJK SC is included for Chinese captions ({runtime_folder}/Fonts, with license and source).
Films and sound assets stay in your local user data folder. Shooting never automatically
saves your game scene or installs plugins. This release supports local Unity offline
shooting and a local browser Workbench, not cloud rendering.
""", encoding="utf-8")
    shutil.copy2(root / "docs" / "workbench.md", out / "WORKBENCH.md")
    dist.copy_release_documents(root, out)

    state = dist.git_state(root)
    (out / "candidate.json").write_text(json.dumps(dict(
        product=config["product"], version=version, packages=packages, **state,
        status=dist.release_status(state, args.release), mediaRuntimeIncluded=False,
        note="media unit (ffmpeg+ffprobe) installs at first use; no curated download is hash-verified yet"),
        indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"candidate": str(out), "packages": packages, "version": version}))


if __name__ == "__main__":
    main()
