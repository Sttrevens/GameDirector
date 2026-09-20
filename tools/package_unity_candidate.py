"""Build a Unity-only, self-contained local candidate; never tag or publish.

The UPM archive includes the companion under Tools~/Workbench. Unity ignores the
runtime as assets, while the native launcher locates it through PackageInfo.
FFmpeg/ffprobe are deliberately declared external prerequisites until a reviewed
media distribution is available. No game adapters, credentials or store are shipped.
"""
import argparse, hashlib, json, os, shutil, subprocess, tarfile, tempfile, zipfile
from pathlib import Path

p = argparse.ArgumentParser()
p.add_argument('--out', required=True)
p.add_argument('--runtime', choices=['osx-arm64', 'osx-x64', 'win-x64'], default='osx-arm64')
a = p.parse_args()
repo = Path(__file__).resolve().parents[1]
out = Path(a.out).resolve()
archive = Path(str(out) + '.zip')
if out.exists() or archive.exists(): raise SystemExit('Candidate already exists; choose a new output.')
out.mkdir(parents=True)
with tempfile.TemporaryDirectory(prefix='gd-unity-package-') as temporary:
    staging = Path(temporary).resolve()
    build = staging/'build'
    for project in ('GameDirector.Workbench', 'GameDirector.Client', 'GameDirector.Core'):
        shutil.copytree(repo/'src'/project, build/'src'/project, ignore=shutil.ignore_patterns('bin', 'obj'))
    for file in ('Directory.Build.props', 'global.json', 'docs/07-directing-playbook.md'):
        destination = build/file; destination.parent.mkdir(parents=True, exist_ok=True); shutil.copy2(repo/file, destination)
    package = staging/'package'
    shutil.copytree(repo/'packages/com.gamedirector.unity', package, ignore=shutil.ignore_patterns('.DS_Store', '.gamedirector-sync.json'))
    runtime = package/'Tools~'/'Workbench'
    subprocess.run(['dotnet', 'publish', str(build/'src/GameDirector.Workbench'), '-c', 'Release', '-r', a.runtime,
                    '--self-contained', 'true', '-o', str(runtime)], check=True)
    shutil.copy2(build/'src/GameDirector.Core/bin/Release/netstandard2.1/GameDirector.Core.dll', package/'Plugins/GameDirector.Core.dll')
    if not (runtime/'Fonts/NotoSansCJKsc-Regular.otf').is_file(): raise RuntimeError('Subtitle font missing from runtime')
    meta = json.loads((package/'package.json').read_text())
    name = f"com.gamedirector.unity-{meta['version']}-{a.runtime}.tgz"
    with tarfile.open(out/name, 'w:gz') as tar: tar.add(package, arcname='package')
    payload = {str(f.relative_to(package)): hashlib.sha256(f.read_bytes()).hexdigest() for f in sorted(package.rglob('*')) if f.is_file()}
    (out/'INSTALL.md').write_text(f'''# GameDirector Unity V1 本地试用候选

平台：{a.runtime}。当前实测：macOS Apple Silicon + Unity 2022.3.34f1。
这是验证中的候选包，尚未签名、公证，也未完成陌生用户和干净机器验收。

1. 在 Unity Package Manager 选择 **Add package from tarball**，选择 `{name}`。
2. 先保存当前场景，然后选择 **Tools → GameDirector → Create Independent Example**。
   它创建独立的示例角色、动画、声音、拍摄场景和 Film 资产，不需要 CDREBIRTH 或模型 API。
3. 选中 Film 资产，在原生 Inspector 点 **启动 / 连接工作台**。后台程序随包提供，无需安装 .NET SDK。
4. 打开示例离线场景并进入 Play Mode，连接作品、导入示例声音，检查拍摄条件。
5. 制作预览，或点 **在浏览器打开同一作品** 继续编辑镜头、声音和字幕、查看成片。
   首次原生 Inspector 使用细节见随附文档。

影片与声音库保存在当前用户本机数据目录。Film 资产保存作品的本地创作内容；
工作台保存已接受的版本与制作任务。拍摄不会自动保存游戏场景或安装插件。

## 当前必须满足的环境条件

编码仍需 FFmpeg（libx264 + libass）和 ffprobe。此候选未打包这些可执行程序。
将兼容程序的绝对路径通过 `GAMEDIRECTOR_FFMPEG` / `GAMEDIRECTOR_FFPROBE` 传给启动 Unity 的环境，
或保证它们存在于 Unity 继承的 PATH。准备检查会在拍摄前指出缺失项。
中文字幕的 Noto Sans CJK SC 字体已随运行组件提供，许可证和来源位于运行组件 Fonts 目录。

原生 Inspector 支持场景/角色关联、镜头编辑、保存、冲突处理、准备检查和制作；
复杂表演与音轨脚本目前通过显式应用脚本编辑，网页提供音轨与字幕编辑。
生成方案是可选能力；真实模型 API 需自行配置，密钥随服务重启清除。

第一版范围是本机 Unity 离线拍摄与本机浏览器工作台。网页并非无需 Unity 的云端渲染服务。
尚未承诺 Windows/Intel Mac、所有 Unity 版本/渲染管线、任意游戏逻辑或整套素材搬家。
''', encoding='utf-8')
    shutil.copy2(repo/'docs/09-product-workbench.md', out/'WORKBENCH.md')
    commit = subprocess.check_output(['git', '-C', str(repo), 'rev-parse', 'HEAD'], text=True).strip()
    source_files = {str(f.relative_to(repo)): hashlib.sha256(f.read_bytes()).hexdigest()
                    for folder in ('src', 'packages/com.gamedirector.unity') for f in (repo/folder).rglob('*')
                    if f.is_file() and not any(x in ('bin','obj','.DS_Store') for x in f.relative_to(repo).parts)}
    (out/'candidate.json').write_text(json.dumps(dict(version=meta['version'], runtime=a.runtime,
        sourceCommit=commit, sourceWorktreeDirty=bool(subprocess.check_output(['git','-C',str(repo),'status','--porcelain'],text=True).strip()),
        status='local candidate; not a public release', mediaRuntimeIncluded=False, package=name,
        packageSha256=hashlib.sha256((out/name).read_bytes()).hexdigest(), payload=payload, sourceFiles=source_files), indent=2)+'\n')
with zipfile.ZipFile(archive, 'x', compression=zipfile.ZIP_DEFLATED) as z:
    for f in sorted(out.iterdir()): z.write(f, f.name)
print(json.dumps(dict(candidate=str(out), archive=str(archive), sha256=hashlib.sha256(archive.read_bytes()).hexdigest())))
