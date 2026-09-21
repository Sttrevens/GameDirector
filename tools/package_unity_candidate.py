"""Build the Unity-first UPM candidate: a self-contained .tgz per platform.

The package carries the complete workbench runtime under Tools~/Workbench
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
import tarfile
import tempfile
import zipfile
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import gamedirector_dist as dist


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--out", required=True)
    parser.add_argument("--runtime", default=None,
                        help="target RID from the distribution manifest (default: all supported platforms)")
    args = parser.parse_args()

    root = dist.REPO
    config = dist.load_config(root)
    rids = [args.runtime] if args.runtime else list(config["supportedPlatforms"])
    for rid in rids:
        if rid not in config["supportedPlatforms"]:
            raise SystemExit(f"unsupported runtime {rid}; supported: {', '.join(config['supportedPlatforms'])}")
    version = dist.read_version(root)
    out = Path(args.out).resolve()
    archive = Path(str(out) + ".zip")
    if out.exists() or archive.exists():
        raise SystemExit("Candidate already exists; choose a new output.")
    out.mkdir(parents=True)

    package_name = config["unity"]["packageName"]
    packages = []
    for rid in rids:
        name = f"{package_name}-{version}-{rid}.tgz"
        target = out / name
        with tempfile.TemporaryDirectory(prefix="gd-unity-package-") as temporary:
            package = dist.stage_unity_package(root, config, rid, Path(temporary) / "package", version)
            # tgz with exec bits recorded; Unity extracts under PackageCache.
            with tarfile.open(target, "x:gz") as tar:
                def reset(ti: tarfile.TarInfo) -> tarfile.TarInfo:
                    ti.uid = ti.gid = 0
                    ti.uname = ti.gname = ""
                    if ti.isfile():
                        base = ti.name.split("/")[-1]
                        executable = base in config["platforms"][rid]["unixExecutables"]
                        ti.mode = 0o755 if executable else 0o644
                    else:
                        ti.mode = 0o755
                    return ti
                tar.add(package, arcname="package", filter=reset)
        packages.append({"file": name, "rid": rid, "sha256": dist.sha256(target)})

    rid_lines = "\n".join(f"- {p['rid']}: `{p['file']}`" for p in packages)
    (out / "INSTALL.md").write_text(f"""# GameDirector Unity 候选包（自包含平台运行时）

版本：{version}。这是验证中的候选包，尚未签名、公证，也未完成陌生用户和干净机器验收。
每个 tgz 只支持一个平台；请按当前 Unity 编辑器所在平台选择：

{rid_lines}

1. 在 Unity Package Manager 选择 **Add package from tarball**，选择对应平台的 tgz。
2. 先保存当前场景，然后选择 **Tools → GameDirector → Create Independent Example**。
3. 选中 Film 资产，在原生 Inspector 点 **启动 / 连接工作台**。后台程序随包提供，无需安装 .NET SDK。
   启动器会先校验随包运行组件的平台与协议版本（Tools~/Workbench/gamedirector.runtime.json），
   不匹配会给出明确错误；跨机打包丢失的 Unix 执行权限会自动修复（PackageCache 内的包会先镜像到
   用户数据目录再修复，绝不改写 PackageCache）。
4. 打开示例离线场景并进入 Play Mode，连接作品、导入示例声音，检查拍摄条件。

## 编码组件（ffmpeg + ffprobe，完整套件才视为可用）

此候选不打包 FFmpeg。首次需要编码时，在准备检查或媒体库界面选择安装方式：
当前平台的包管理器（winget / Homebrew，完整套件含 ffprobe），或离线核验套件——
在安装界面填写你核验过的 bundle descriptor JSON 的完整路径后点击安装，
描述文件必须为 ffmpeg 与 ffprobe 同时给出路径与 SHA-256。不完整的组合不会被当作可用。
也可以把兼容程序的绝对路径通过 `GAMEDIRECTOR_FFMPEG` / `GAMEDIRECTOR_FFPROBE`
传给启动 Unity 的环境；指向不存在文件的环境变量会明确报错，不会静默回退。

中文字幕的 Noto Sans CJK SC 字体随运行组件提供（Tools~/Workbench/Fonts，含许可证与来源）。
影片与声音库保存在当前用户本机数据目录；拍摄不会自动保存游戏场景或安装插件。
第一版范围是本机 Unity 离线拍摄与本机浏览器工作台，并非云端渲染服务。
""", encoding="utf-8")
    shutil.copy2(root / "docs" / "09-product-workbench.md", out / "WORKBENCH.md")

    state = dist.git_state(root)
    (out / "candidate.json").write_text(json.dumps(dict(
        product=config["product"], version=version, packages=packages, **state,
        status="local candidate; not a public release", mediaRuntimeIncluded=False,
        note="media unit (ffmpeg+ffprobe) installs at first use; no curated download is hash-verified yet"),
        indent=2) + "\n", encoding="utf-8")
    with zipfile.ZipFile(archive, "x", compression=zipfile.ZIP_DEFLATED) as z:
        for f in sorted(out.iterdir()):
            z.write(f, f.name)
    print(json.dumps({"candidate": str(out), "archive": str(archive),
                      "version": version, "sha256": dist.sha256(archive)}))


if __name__ == "__main__":
    main()
