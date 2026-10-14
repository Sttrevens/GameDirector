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
import functools
import hashlib
import json
import os
import shutil
import stat
import subprocess
import tarfile
import tempfile
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
        for field in ("project", "executable"):
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


CANDIDATE_STATUS = "local candidate; not a published release"
RELEASE_STATUS = "release"


def release_status(state: dict, release: bool) -> str:
    """Status stamped into shipped manifests. A release build must come from a clean commit."""
    if not release:
        return CANDIDATE_STATUS
    if state["sourceWorktreeDirty"]:
        raise SystemExit("--release requires a clean worktree; commit or remove local changes first")
    return RELEASE_STATUS


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


CORE_DLL = "packages/com.gamedirector.unity/Plugins/GameDirector.Core.dll"
CORE_DLL_HASH = "tools/core-dll.sha256"


def verify_core_dll(root: Path) -> None:
    """Package only the canonical Unity DLL. POSIX hosts rebuild it with the pinned
    SDK and compare bytes; every host checks the recorded canonical hash, which is
    the only check on Windows (no bash there; CI proves the hash on Linux)."""
    if os.name != "nt":
        subprocess.run(["bash", str(root / "tools/build_core_dll.sh"), "--check"], cwd=root, check=True)
    recorded = (root / CORE_DLL_HASH).read_text(encoding="utf-8").strip()
    if sha256(root / CORE_DLL) != recorded:
        raise SystemExit(f"{CORE_DLL} does not match {CORE_DLL_HASH}; run tools/build_core_dll.sh and commit both")


def copy_release_documents(root: Path, dest: Path) -> None:
    for name in ("LICENSE", "THIRD-PARTY-NOTICES.md", "CHANGELOG.md"):
        shutil.copy2(root / name, dest / name)


def copy_runtime_notices(root: Path, app: dict, rid: str, dest: Path) -> None:
    """Copy exact pack/installed SDK legal texts for the published frameworks.
    Versions and package-cache/SDK locations come from publish/restore metadata;
    missing upstream texts fail closed rather than being fabricated."""
    runtime_configs = list(dest.glob("*.runtimeconfig.json"))
    if len(runtime_configs) != 1:
        raise SystemExit("Expected one self-contained runtime configuration")
    frameworks = json.loads(runtime_configs[0].read_text(encoding="utf-8"))["runtimeOptions"]["includedFrameworks"]
    assets = json.loads((root / app["project"] / "obj/project.assets.json").read_text(encoding="utf-8"))
    sdks = {Path(f["runtimeIdentifierGraphPath"]).parents[2]
            for f in assets["project"]["frameworks"].values() if "runtimeIdentifierGraphPath" in f}
    for framework in frameworks:
        name, version = framework["name"], framework["version"]
        pack = f"{name}.Runtime.{rid}"
        sources = [Path(folder) / pack.lower() / version for folder in assets["packageFolders"]]
        for sdk in sorted(sdks):
            sources += [sdk / "packs" / pack / version, sdk / "shared" / name / version, sdk, sdk.parent]
            # Homebrew keeps the installed SDK's legal texts in share/doc.
            sources += sorted((sdk.parent / "share/doc").glob("*"))
        target = dest / "licenses" / name
        target.mkdir(parents=True, exist_ok=True)
        for output, aliases in (("LICENSE.txt", ("LICENSE.TXT", "LICENSE.txt", "LICENSE")),
                                ("THIRD-PARTY-NOTICES.txt", ("THIRD-PARTY-NOTICES.TXT", "THIRD-PARTY-NOTICES.txt", "ThirdPartyNotices.txt"))):
            source = next((folder / alias for folder in sources for alias in aliases if (folder / alias).is_file()), None)
            if source is None:
                raise SystemExit(f"runtime legal text missing: {name} {version} {output}")
            shutil.copy2(source, target / output)


@functools.lru_cache(maxsize=None)
def nuget_global_packages(root: Path) -> Path:
    """Offline publish source follows NuGet configuration, never a home literal.
    Asked once per process; a CLI query killed by the host is retried, then NuGet's
    documented default folder is used if it exists."""
    configured = os.environ.get("NUGET_PACKAGES")
    if configured:
        return Path(configured).expanduser().resolve()
    for _ in range(2):
        try:
            output = subprocess.check_output(["dotnet", "nuget", "locals", "global-packages", "-l"],
                                             cwd=root, text=True, timeout=120)
        except (subprocess.CalledProcessError, subprocess.TimeoutExpired):
            continue
        for line in output.splitlines():
            label, separator, folder = line.partition(":")
            if separator and label.strip() == "global-packages" and folder.strip():
                return Path(folder.strip()).expanduser().resolve()
    default = Path.home() / ".nuget" / "packages"
    if default.is_dir():
        return default.resolve()
    raise SystemExit("dotnet nuget locals did not report the configured global-packages folder")


def publish_app(root: Path, config: dict, app_key: str, rid: str, dest: Path) -> Path:
    """Self-contained single-RID publish; transitively includes every project
    dependency (Client, Core, Decisions when referenced) and Client content
    (Fonts, media-distributions.json)."""
    app = config["apps"][app_key]
    if dest.exists():
        raise SystemExit(f"Publish destination exists; use a fresh staging directory: {dest}")
    dest.mkdir(parents=True)
    with tempfile.TemporaryDirectory(prefix="gd-publish-locks-") as locks:
        dotnet(root, ["publish", str(root / app["project"]), "-c", "Release", "-r", rid,
                      "--self-contained", "true", "-o", str(dest),
                      "--source", str(nuget_global_packages(root)),
                      # Shipped builds carry no debug symbols: PDB source-link maps
                      # would embed the builder's absolute checkout path.
                      "-p:DebugType=None", "-p:DebugSymbols=false",
                      "-p:GameDirectorPublishLockRoot=" + locks])
    copy_runtime_notices(root, app, rid, dest)
    return dest


def local_path_markers(root: Path) -> list:
    """Byte strings that must never appear in a shipped file: the builder's
    checkout path and NuGet cache path (both reveal the local account)."""
    markers = {str(root.resolve()), str(nuget_global_packages(root).resolve())}
    return sorted(m.encode("utf-8") for m in markers if len(m) > 3)


def assert_no_local_paths(folder: Path, root: Path) -> None:
    markers = local_path_markers(root)
    leaks = []
    for path in sorted(folder.rglob("*")):
        if path.is_file():
            data = path.read_bytes()
            if any(m in data for m in markers):
                leaks.append(path.relative_to(folder).as_posix())
    if leaks:
        raise SystemExit("Shipped files embed a local build path: " + ", ".join(leaks[:10]))


def merge_publishes(sources: list, dest: Path) -> Path:
    """Reject any non-identical overlap BEFORE copying; never last-writer-wins."""
    if dest.exists():
        raise SystemExit(f"Runtime destination exists: {dest}")
    files = {}
    folded = {}
    for source in sources:
        for path in sorted(source.rglob("*")):
            if not path.is_file():
                continue
            name = path.relative_to(source).as_posix()
            if path.is_symlink():
                raise SystemExit(f"Published symlink is not portable: {name}")
            if name.casefold() in folded and folded[name.casefold()] != name:
                raise SystemExit(f"Case-colliding runtime path: {name}")
            folded[name.casefold()] = name
            if name in files and sha256(files[name]) != sha256(path):
                raise SystemExit(f"Non-identical shared runtime file: {name}")
            files[name] = path
    dest.mkdir(parents=True)
    for name, source in files.items():
        target = dest / name
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(source, target)
    return dest


def publish_runtime(root: Path, config: dict, rid: str, dest: Path, version: str) -> Path:
    with tempfile.TemporaryDirectory(prefix="gd-shared-publish-") as temporary:
        sources = [publish_app(root, config, key, rid, Path(temporary) / key) for key in config["apps"]]
        merge_publishes(sources, dest)
    write_runtime_manifest(dest, config, rid, version)
    apply_unix_modes(config, rid, dest)
    return dest


def unity_runtime_folder(config: dict) -> Path:
    return Path(config["unity"]["bundledRuntimeParent"]) / config["runtimeFolder"]


def archive_name(config: dict, version: str, rid: str, unity: bool = False) -> str:
    layout = config["unity"] if unity else config
    return layout["archiveName"].format(version=version, rid=rid,
        archive=config["platforms"][rid]["archive"], packageName=config["unity"]["packageName"])


def executable_names(config: dict, rid: str, folder: Path) -> list:
    """Only present declared programs, independent of packaging OS/X_OK."""
    return sorted(n for n in config["platforms"][rid]["unixExecutables"] if (folder / n).is_file())


def write_runtime_manifest(folder: Path, config: dict, rid: str, version: str) -> dict:
    manifest = {
        "product": config["product"],
        "app": "shared",
        "apps": {key: app["executable"] + config["platforms"][rid]["executableExtension"]
                 for key, app in config["apps"].items()},
        "version": version,
        "unityPackageVersion": version,
        "rid": rid,
        "protocolVersion": config["protocolVersion"],
        "executables": executable_names(config, rid, folder),
        "sha256": hash_tree(folder),
    }
    (folder / config["runtimeManifestFile"]).write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    return manifest


def copy_tree(source: Path, target: Path, excludes: tuple = (".DS_Store", ".gamedirector-sync.json", ".gamedirector-install.json")) -> None:
    shutil.copytree(source, target, ignore=shutil.ignore_patterns(*excludes))


def stage_unity_package(root: Path, config: dict, rid: str, package_dest: Path, version: str,
                        runtime_source: Path = None, include_runtime: bool = True) -> Path:
    """The UPM package with the complete per-platform runtime inside Tools~/.
    Used by both the standalone Unity candidate and the full distribution."""
    unity = config["unity"]
    copy_tree(root / unity["packageSource"], package_dest)
    copy_release_documents(root, package_dest)
    write_distribution_manifest_copy(root, package_dest)
    # copy_tree preserved Plugins/GameDirector.Core.dll. Entrypoints verify this
    # canonical pinned-SDK binary first; do not overwrite it with a dev build.
    # A full archive omits this copy; its installer fills it from archive bin/.
    if include_runtime:
        runtime = package_dest / unity_runtime_folder(config)
        if runtime_source is None:
            publish_runtime(root, config, rid, runtime, version)
        else:
            manifest = json.loads((runtime_source / config["runtimeManifestFile"]).read_text())
            if manifest["rid"] != rid or manifest["version"] != version or manifest["app"] != "shared":
                raise SystemExit("Runtime source does not match the target RID/version/layout")
            expected = manifest["sha256"]
            actual = hash_tree(runtime_source)
            actual.pop(config["runtimeManifestFile"])
            if actual != expected:
                raise SystemExit("Runtime source failed complete integrity verification")
            copy_tree(runtime_source, runtime)
        if not (runtime / "Fonts" / "NotoSansCJKsc-Regular.otf").is_file():
            raise SystemExit("Subtitle font missing from the staged runtime; refusing to package.")
    # Media installer data travels with the package so installs work offline.
    media_dir = package_dest / config["unity"]["mediaDataFolder"]
    media_dir.mkdir(parents=True, exist_ok=True)
    shutil.copy2(root / config["media"]["manifestSource"], media_dir / config["media"]["manifestFile"])
    return package_dest


def apply_unix_modes(config: dict, rid: str, folder: Path) -> None:
    """Normalize staging permissions: declared programs 0755, other files 0644.
    Archive modes use the same declaration, independently of the build OS."""
    executables = set(executable_names(config, rid, folder))
    for path in folder.rglob("*"):
        if path.is_file():
            path.chmod(0o755 if path.relative_to(folder).as_posix() in executables else 0o644)


def archive_file_mode(relative: str, config: dict, rid: str, runtime_prefix: str, root_launchers: bool = False) -> int:
    names = config["platforms"][rid]["unixExecutables"]
    paths = {runtime_prefix + "/" + name for name in names}
    if root_launchers:
        paths.update(names)
    return 0o755 if relative in paths else 0o644


def make_unity_archive(package: Path, target: Path, config: dict, rid: str) -> Path:
    def reset(ti: tarfile.TarInfo) -> tarfile.TarInfo:
        ti.uid = ti.gid = 0
        ti.uname = ti.gname = ""
        ti.mode = archive_file_mode(Path(ti.name).relative_to("package").as_posix(), config, rid,
                                    unity_runtime_folder(config).as_posix()) if ti.isfile() else 0o755
        return ti
    with tarfile.open(target, "x:gz") as tar:
        tar.add(package, arcname="package", filter=reset)
    return target


def make_archive(folder: Path, archive_path: Path, config: dict, rid: str) -> Path:
    if archive_path.exists():
        raise SystemExit(f"Archive exists; preserved: {archive_path}")
    extension = "." + config["platforms"][rid]["archive"]
    top = archive_path.name.removesuffix(extension)
    if config["platforms"][rid]["archive"] == "zip":
        with zipfile.ZipFile(archive_path, "x", compression=zipfile.ZIP_DEFLATED) as z:
            for f in sorted(folder.rglob("*")):
                if f.is_file():
                    relative = f.relative_to(folder).as_posix()
                    info = zipfile.ZipInfo((Path(top) / relative).as_posix())
                    info.create_system = 3
                    info.external_attr = (stat.S_IFREG | archive_file_mode(relative, config, rid, config["runtimeFolder"], True)) << 16
                    info.compress_type = zipfile.ZIP_DEFLATED
                    z.writestr(info, f.read_bytes())
    else:
        def reset(ti: tarfile.TarInfo) -> tarfile.TarInfo:
            ti.uid = ti.gid = 0
            ti.uname = ti.gname = ""
            if ti.isfile():
                relative = Path(ti.name).relative_to(top).as_posix()
                ti.mode = archive_file_mode(relative, config, rid, config["runtimeFolder"], True)
            else:
                ti.mode = 0o755
            return ti
        with tarfile.open(archive_path, "x:gz") as tar:
            for f in sorted(folder.rglob("*")):
                tar.add(f, arcname=(Path(top) / f.relative_to(folder)).as_posix(), filter=reset, recursive=False)
    return archive_path


def write_distribution_manifest_copy(root: Path, out: Path) -> None:
    shutil.copy2(root / "tools" / "distribution.json", out / "distribution.json")


def media_manifest_copy(root: Path, config: dict, out: Path) -> Path:
    folder = out / config["media"]["distributionFolder"]
    folder.mkdir(parents=True, exist_ok=True)
    shutil.copy2(root / config["media"]["manifestSource"], folder / config["media"]["manifestFile"])
    return folder
