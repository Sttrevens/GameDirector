"""Portable Godot addon/Stardeck distribution payload; source files are real, caches omitted."""
import shutil
import zipfile
from pathlib import Path

IGNORE=shutil.ignore_patterns('.godot','*.import','__pycache__','.DS_Store','bin','obj')

def addon_archive(root: Path, archive: Path) -> Path:
    """Godot Asset Library install layout (no enclosing package directory)."""
    source = root/'packages/gamedirector-godot/addons/gamedirector'
    with zipfile.ZipFile(archive, 'x', compression=zipfile.ZIP_DEFLATED) as opened:
        opened.write(root/'LICENSE', 'LICENSE')
        for path in sorted(source.rglob('*')):
            relative = path.relative_to(source)
            if path.is_file() and not any(IGNORE(source, [part]) for part in relative.parts):
                opened.write(path, (Path('addons/gamedirector')/relative).as_posix())
    return archive

def stage(root: Path, out: Path):
    shutil.copytree(root/'packages/gamedirector-godot',out/'godot',ignore=IGNORE)
    shutil.copytree(root/'adapters/stardeck',out/'adapters/stardeck',ignore=IGNORE)
