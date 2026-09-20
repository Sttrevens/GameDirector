"""Build a content-addressed local candidate. Does not commit, tag or publish."""
import argparse, hashlib, json, shutil, subprocess, zipfile
from pathlib import Path
p=argparse.ArgumentParser();p.add_argument('--out',required=True);a=p.parse_args()
r=Path(__file__).resolve().parents[1];out=Path(a.out).resolve()
if out.exists():raise SystemExit('Output exists; use a fresh candidate directory')
out.mkdir(parents=True)
for project,name in [('GameDirector.Cli','cli'),('GameDirector.Mcp','mcp'),('GameDirector.Workbench','workbench')]:
 subprocess.run(['dotnet','publish',str(r/'src'/project),'--no-restore','-c','Release','-p:UseAppHost=false','-o',str(out/name)],check=True)
# Rebuild generated Unity Core inside the candidate rather than changing the checkout.
for source,target in [(r/'packages/com.gamedirector.unity',out/'unity/com.gamedirector.unity'),(r/'adapters/cdrebirth/com.gamedirector.cdrebirth',out/'unity/com.gamedirector.cdrebirth')]:
 shutil.copytree(source,target,ignore=shutil.ignore_patterns('.DS_Store','.gamedirector-sync.json'))
shutil.copy2(r/'src/GameDirector.Core/bin/Release/netstandard2.1/GameDirector.Core.dll',out/'unity/com.gamedirector.unity/Plugins/GameDirector.Core.dll')
shutil.copytree(r/'docs',out/'docs');shutil.copy2(r/'README.md',out/'README.md')
# Include engine integration recipes; game source/assets remain in their owner's repo.
shutil.copytree(r/'packages/gamedirector-three',out/'three',ignore=shutil.ignore_patterns('node_modules'))
shutil.copytree(r/'adapters/sandring',out/'adapters/sandring')
(out/'adapters/cdrebirth').mkdir(parents=True)
shutil.copy2(r/'adapters/cdrebirth/performances.json',out/'adapters/cdrebirth/performances.json')
shutil.copytree(r/'adapters/cdrebirth/manifests',out/'adapters/cdrebirth/manifests')
(out/'tools').mkdir()
for name in ('setup_media_tools.sh', 'install_unity_package.py'):
 shutil.copy2(r/'tools'/name,out/'tools'/name)
shutil.copytree(r/'samples',out/'samples',ignore=shutil.ignore_patterns('__pycache__','.DS_Store'))
(out/'INSTALL.md').write_text('''# Local candidate installation

Requires .NET 8 and ASP.NET Core 8 runtimes (the .NET 8 SDK includes both).
Start the browser workbench with `dotnet workbench/gamedirector-workbench.dll --open true`
or the included Start GameDirector launcher. Data stays in your local application data
folder; add `--data /your/output/folder` to choose another. Read docs/09-product-workbench.md.

Start with `dotnet cli/gd.dll doctor`; install FFmpeg
with libx264/libass and ffprobe (or run tools/setup_media_tools.sh for an isolated FFmpeg), then read `dotnet cli/gd.dll guide`.
MCP stdio entry: `dotnet mcp/gamedirector-mcp.dll`.

Unity: preview a scoped installation with Python 3 (3.9+):
`python3 tools/install_unity_package.py --source unity/com.gamedirector.unity --project /path/to/game`.
Add `--apply` to install that plan. Commit the resulting package once. Repeating an
identical install performs zero writes. Upgrades preserve locally edited files and
stop before writes on a conflict. Do not run installation as part of each shoot.
Open Tools/GameDirector/Director Workbench. Discover/sync project assets, or select a
prefab and explicitly create a new offline stage. Open that stage in isolation and enter
Play Mode, then refresh live bindings. GenericSceneAdapter now discovers registered
roles/controller states and locations automatically without an authored manifest. Run package
Editor tests via the Unity Test Runner; add com.gamedirector.unity to manifest
`testables` if necessary. CDREBIRTH's package is optional and game-specific.

Three: install the dependencies in three/ and its Playwright browser, then pass
--game-root, --adapter and --cli to three/server.mjs. This candidate includes the
Sandring offline recipe; its original game source is not redistributed.

This is a local development candidate. Read release readiness and validation
reports for the engine/platform and human acceptance actually exercised.
''')
(out/'Start GameDirector.command').write_text('#!/bin/sh\nset -eu\ncd "$(dirname "$0")"\nexec dotnet workbench/gamedirector-workbench.dll --open true\n')
(out/'Start GameDirector.command').chmod(0o755)
(out/'Start GameDirector.cmd').write_text('@echo off\r\ncd /d "%~dp0"\r\ndotnet workbench\\gamedirector-workbench.dll --open true\r\n')
files={str(f.relative_to(out)):hashlib.sha256(f.read_bytes()).hexdigest() for f in sorted(out.rglob('*')) if f.is_file()}
source=subprocess.check_output(['git','-C',str(r),'rev-parse','HEAD'],text=True).strip()
dirty=bool(subprocess.check_output(['git','-C',str(r),'status','--porcelain'],text=True).strip())
(out/'candidate.json').write_text(json.dumps(dict(version='0.2.0-alpha.1',sourceCommit=source,sourceWorktreeDirty=dirty,files=files,status='local candidate; not a published release'),indent=2)+'\n')
archive=Path(str(out)+'.zip')
if archive.exists():raise SystemExit('Archive exists; preserved')
with zipfile.ZipFile(archive,'x',compression=zipfile.ZIP_DEFLATED) as z:
 for f in sorted(out.rglob('*')):
  if f.is_file():z.write(f,str(f.relative_to(out)))
print(json.dumps(dict(candidate=str(out),archive=str(archive),sha256=hashlib.sha256(archive.read_bytes()).hexdigest())))
