#!/usr/bin/env bash
# Isolated optional FFmpeg with libass. Does not replace system executables.
set -euo pipefail
repository_root="$(cd "$(dirname "$0")/.." && pwd)"
media_python="${1:-python3}"
"$media_python" -m venv "$repository_root/.tools/media-venv"
"$repository_root/.tools/media-venv/bin/python" -m pip install 'imageio-ffmpeg==0.6.0'
"$repository_root/.tools/media-venv/bin/python" - "$repository_root" <<'PY'
import pathlib, shutil, sys
import imageio_ffmpeg
root=pathlib.Path(sys.argv[1])
target=root/'.tools/media/ffmpeg'
target.parent.mkdir(parents=True,exist_ok=True)
shutil.copy2(imageio_ffmpeg.get_ffmpeg_exe(),target)
print('Media runtime ready. Set GAMEDIRECTOR_FFMPEG to: '+str(target))
print('Keep ffprobe available on PATH, or set GAMEDIRECTOR_FFPROBE.')
PY
