"""Shared GameDirector distribution packaging logic.

Single source of truth: tools/distribution.json declares supported platforms,
app layout, Unity bundle layout and media data locations. Versions come from
build metadata (Directory.Build.props <Version>) and the Unity package manifest
(packages/com.gamedirector.unity/package.json) — never from literals copied
into scripts; both must agree before anything is staged.

Both the full standalone distribution (package_release.py) and the Unity UPM
bundle (package_unity_candidate.py) are staged through this module so their
layout, runtime manifests and archives stay identical. Publishing is plain
`dotnet publish -r <rid> --self-contained`, so any new transitive dependency
(for example a future GameDirector.Decisions project reference) flows into the
output automatically — there is no frozen dependency list here to maintain.
"""
import hashlib
import json
import os
import shutil
import stat
import subprocess
import tarfile
import zipfile
import xml.etree.ElementTree as ET
from pathlib import Path

REPO = Path(__file__).resolve().parents[1]


def load_config(root: Path) -> dict:
    config = json.loads((root / "tools" / "distribution.json").read_text(encoding="utf-8"))
    if config.get("schemaVersion") != 1:
        raise SystemExit("unsupported distribution manifest schema")
    platforms = config["platforms"]
    for rid in config["supportedPlatforms"]:
        if rid not in platforms:
            raise SystemExit(f"supported platform {rid} lacks a layout entry")
    for key, app in config["apps"].items():
        for field in ("project", "folder", "executable"):
            if not app.get(field):
                raise SystemExit(f"app {key} lacks {field}")
        if not (root / app["project"]).is_dir():
            raise SystemExit(f"app {key} project missing: {app['project']}")
    return config


def read_version(root: Path) -> str:
    props = ET.parse(root / "Directory.Build.props")
    version = props.getroot().find("./PropertyGroup/Version")
    if version is None or not version.text:
        raise SystemExit("Directory.Build.props lacks <Version>")
    package = json.loads((root / "packages" / "com.gamedirector.unity" / "package.json").read_text(encoding="utf-8"))
    if package["version"] != version.text:
        raise SystemExit(f"version drift: Directory.Build.props={version.text} unity package={package['version']}")
    return version.text


def git_state(root: Path) -> dict:
    commit = subprocess.check_output(["git", "-C", str(root), "rev-parse", "HEAD"], text=True).strip()
    dirty = bool(subprocess.check_output(["git", "-C", str(root), "status", "--porcelain"], text=True).strip())
    return {"sourceCommit": commit, "sourceWorktreeDirty": dirty}


def sha256(path: Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def hash_tree(folder: Path) -> dict:
    return {str(f.relative_to(folder)).replace("\\", "/"): sha256(f)
            for f in sorted(folder.rglob("*")) if f.is_file()}


def dotnet(root: Path, args: list) -> None:
    subprocess.run(["dotnet", *args], cwd=root, check=True)


def publish_app(root: Path, config: dict, app_key: str, rid: str, dest: Path) -> Path:
    """Self-contained single-RID publish; transitively includes every project
    dependency (Client, Core, Decisions when referenced) and Client content
    (Fonts, media-distributions.json)."""
    app = config["apps"][app_key]
    if dest.exists():
        raise SystemExit(f"Publish destination exists; use a fresh staging directory: {dest}")
    dest.mkdir(parents=True)
    dotnet(root, ["publish", str(root / app["project"]), "-c", "Release", "-r", rid,
                  "--self-contained", "true", "-o", str(dest)])
    return dest


def executable_names(config: dict, rid: str, folder: Path) -> list:
    """Files that must carry the Unix execute bit: the apphost plus the
    declared Unix executables that actually exist in this publish output.
    Windows has no exec bit, so cross-builds trust the declared list only."""
    names = [] if os.name == "nt" else [p.name for p in folder.iterdir() if p.is_file() and os.access(p, os.X_OK)]
    declared = [n for n in config["platforms"][rid]["unixExecutables"] if (folder / n).is_file()]
    return sorted(set(names) | set(declared))


def write_runtime_manifest(folder: Path, config: dict, app_key: str, rid: str, version: str, unity_version: str) -> dict:
    manifest = {
        "product": config["product"],
        "app": app_key,
        "version": version,
        "unityPackageVersion": unity_version,
        "rid": rid,
        "protocolVersion": config["protocolVersion"],
        "executables": executable_names(config, rid, folder),
        "sha256": hash_tree(folder),
    }
    (folder / config["runtimeManifestFile"]).write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    return manifest


def copy_tree(source: Path, target: Path, excludes: tuple = (".DS_Store", ".gamedirector-sync.json", ".gamedirector-install.json")) -> None:
    shutil.copytree(source, target, ignore=shutil.ignore_patterns(*excludes))


def stage_unity_package(root: Path, config: dict, rid: str, package_dest: Path, version: str) -> Path:
    """The UPM package with the complete per-platform runtime inside Tools~/.
    Used by both the standalone Unity candidate and the full distribution."""
    unity = config["unity"]
    copy_tree(root / unity["packageSource"], package_dest)
    # Rebuild the Unity-side Core DLL from the exact sources being packaged.
    dotnet(root, ["build", str(root / "src" / "GameDirector.Core"), "-c", "Release"])
    plugins = package_dest / "Plugins"
    plugins.mkdir(exist_ok=True)
    shutil.copy2(root / "src" / "GameDirector.Core" / "bin" / "Release" / "netstandard2.1" / "GameDirector.Core.dll",
                 plugins / "GameDirector.Core.dll")
    # The self-contained workbench runtime, including Fonts and media data via Client.
    runtime = package_dest / unity["bundledRuntimeFolder"]
    publish_app(root, config, "workbench", rid, runtime)
    if not (runtime / "Fonts" / "NotoSansCJKsc-Regular.otf").is_file():
        raise SystemExit("Subtitle font missing from the staged runtime; refusing to package.")
    # Media installer data travels with the package so installs work offline.
    media_dir = package_dest / config["unity"]["mediaDataFolder"]
    media_dir.mkdir(parents=True, exist_ok=True)
    shutil.copy2(root / config["media"]["manifestSource"], media_dir / config["media"]["manifestFile"])
    write_runtime_manifest(runtime, config, "workbench", rid, version, version)
    apply_unix_modes(config, rid, runtime)
    return package_dest


def apply_unix_modes(config: dict, rid: str, folder: Path) -> None:
    """Cross-built archives lose exec bits; stamp them into the staging tree so
    tarfile records them (and the Unity launcher repairs them as a fallback)."""
    if config["platforms"][rid]["archive"] != "tar.gz":
        return
    for name in executable_names(config, rid, folder):
        path = folder / name
        path.chmod(path.stat().st_mode | stat.S_IXUSR | stat.S_IXGRP | stat.S_IXOTH)


def make_archive(folder: Path, archive_path: Path, config: dict, rid: str) -> Path:
    if archive_path.exists():
        raise SystemExit(f"Archive exists; preserved: {archive_path}")
    if config["platforms"][rid]["archive"] == "zip":
        with zipfile.ZipFile(archive_path, "x", compression=zipfile.ZIP_DEFLATED) as z:
            for f in sorted(folder.rglob("*")):
                if f.is_file():
                    z.write(f, str(f.relative_to(folder)))
    else:
        def reset(ti: tarfile.TarInfo) -> tarfile.TarInfo:
            ti.uid = ti.gid = 0
            ti.uname = ti.gname = ""
            if ti.isfile():
                base = ti.name.split("/")[-1]
                executable = base in config["platforms"][rid]["unixExecutables"] or (os.name != "nt" and os.access(folder / ti.name, os.X_OK))
                ti.mode = 0o755 if executable else 0o644
            else:
                ti.mode = 0o755
            return ti
        with tarfile.open(archive_path, "x:gz") as tar:
            for f in sorted(folder.rglob("*")):
                tar.add(f, arcname=str(f.relative_to(folder)), filter=reset, recursive=False)
    return archive_path


def write_distribution_manifest_copy(root: Path, out: Path) -> None:
    shutil.copy2(root / "tools" / "distribution.json", out / "distribution.json")


def media_manifest_copy(root: Path, config: dict, out: Path) -> Path:
    folder = out / config["media"]["distributionFolder"]
    folder.mkdir(parents=True, exist_ok=True)
    shutil.copy2(root / config["media"]["manifestSource"], folder / config["media"]["manifestFile"])
    return folder
