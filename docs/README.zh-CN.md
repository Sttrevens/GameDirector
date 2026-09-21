# GameDirector（中文文档）

> English version: [README.md](../README.md)

**通用游戏内 AI 导演内核** — 让 LLM 以"导演"身份编排游戏内相机、角色动画、怪物出场、音频与节奏，产出可复现的演出时间轴（分镜脚本），用于自动录制 PV / Machinima / 宣传素材，并通过可验证的录制、剪辑与审片流水线交付成片。

**Unity V1 本地候选**：Unity 原生 Film Inspector 与网页现在使用同一份有版本的作品。
从 **Tools → GameDirector → Create Independent Example** 创建自带角色、动画、声音和三镜头的独立示例；
选中 Film 资产即可编辑、连接工作台、检查拍摄条件、制作影片，以及打开网页版。
两个界面同时修改会提示冲突；制作固定使用提交时的版本。
候选包构建：`python tools/package_unity_candidate.py --out dist/unity-candidate --runtime win-x64`；macOS 对应 `osx-arm64` 或 `osx-x64`。
它随 Unity 包提供独立运行组件和中文字幕字体，无需 .NET SDK；编码仍需兼容的 FFmpeg/ffprobe。
Windows 已使用 Unity 2022.3.34f1 实拍独立三镜头示例，验证声音、中文字幕、版本冲突与局部重拍。本次新增发行与决策代码仍需 macOS 实机复验。尚未完成公开发行验收。
[本次实现与验证](13-unity-v1-implementation-2026-09-12.md)。

**导演工作台 Alpha**：完整发行包中运行 `cli/gd workbench --open true`（Windows 使用 `cli/gd.exe`）打开窗口。源码开发可运行 `dotnet run --project src/GameDirector.Workbench -- --open true`。
Unity 包内提供 `Tools → GameDirector → Director Workbench`，支持资产发现、新建离线拍摄场景和任务调度。
外部 MCP Agent 与自带 API 导演共用项目、资产和制作记录；支持多镜头画面初剪、局部重拍与中断恢复。
当前窗口支持真实资产拍摄、导入对白/配乐/音效、字幕和定向修改；出片资料与游戏源工程分开保存。语音合成、口型及 UE/Godot 插件尚未实现。[产品使用与能力边界](09-product-workbench.md)。

## 设计原则（第一性原理）

1. **LLM 是导演，不是木偶师。** LLM 离线产出一份确定性时间轴资产（shot list），游戏内播放引擎精确执行。不做逐帧实时遥控。
2. **通用的是协议、DSL 与导演 Agent；每游戏必须做的是一份自描述 manifest 加一个薄适配器。** 换游戏的边界是一份 manifest 和薄适配器；工作量与通用性必须由第二个游戏验证。
3. **游戏自我描述。** MCP/CLI 连接游戏后先拉取能力清单（角色、动画 clip、点位、可用镜头语言、音频），LLM 只在清单约束内编排。编译器负责证伪。
4. **确定性优先。** 同一时间轴 + 同一 dt 序列 ⇒ 同一事件序列。时间轴可被人工编辑、被 LLM 生成、被版本管理、被回放复现。
5. **权威边界显式化。** v0 一律在离线/沙盒（director mode）执行，规避联机权威问题；联机实拍（Host 执导、RPC 扇出）是 v1 的显式设计点，不是隐含行为。
6. **创作、判断与执行分离。** `IPlanGenerator` 提出影片，`IDecisionProvider` 在有证据的有限候选中判断，编译器与确定性时间轴验证并执行。Jev 接入位于独立 Decisions 模块，生产任务不调用模型。

## 安装与自动化入口

完整发行包为每个平台自带 .NET 运行组件。Unity 用户导入对应平台的 `.tgz`；命令行与 MCP 用户解压完整发行包即可使用，无需先安装 .NET、Python 或 Node。Three 适配器有其独立运行依赖。

```text
gd doctor
gd unity install --project <Unity工程目录>
gd unity install --project <Unity工程目录> --apply
gd workbench --open true
gd mcp
gd media status
```

安装默认只展示变更；显式 `--apply` 才写入。重复安装无变化时不写文件，已有本地修改会阻止覆盖。媒体能力按 FFmpeg、ffprobe、编码器和字幕过滤器整体检查；离线套件需提供完整文件与校验值。源码 Git UPM 本身不包含独立工作台程序，初次使用应选完整平台包。

发行构建：`python tools/package_release.py --runtime win-x64 --out dist/windows-candidate`。平台与布局由 `tools/distribution.json` 声明。Agent 使用说明随包提供于 `skills/gamedirector/SKILL.md`。

[安装说明](16-distribution-and-install.md) · [Windows 实测记录](15-windows-verification-2026-09-20.md)

Jev 已接入表演匹配、单镜头相机建议、声音匹配、候选影片比较和文本语义复核。模型配置与创作模型分开，密钥仅存内存；自动选择门槛默认留空，需按任务评估后设置。缺少证据、判断失败或未校准时保留原作品，审计记录解释原因。[决策层与接口](17-jev-decisions.md)。

## 分层

| 层 | 内容 | 本仓库位置 | 通用性 |
|---|---|---|---|
| L0 传输/桥接 | 引擎内嵌桥（HTTP JSON-RPC）、进程外 MCP server / CLI | `packages/com.gamedirector.unity`、`src/GameDirector.Mcp`、`src/GameDirector.Cli` | 引擎级通用 |
| L1 导演 DSL | 镜头语言 + 时间轴原语 + 编译校验 + 确定性播放器 | `src/GameDirector.Core`（零依赖 netstandard2.1） | 完全通用 |
| L2 能力清单 | 游戏自描述：roles / actors+clips / locations / audio / shotTypes | `adapters/*/manifests/*.json` + 运行时 endpoint | 机制通用，内容每游戏声明 |
| L3 游戏适配器 | DSL → 具体引擎/游戏系统调用 | `packages/com.gamedirector.unity`（通用基类）+ `adapters/cdrebirth`（首个实例） | 每游戏一个薄层 |

## 仓库地图

```
src/GameDirector.Core        # DSL 模型、能力清单模型、时间轴编译器、确定性播放器、适配器接口（零依赖）
src/GameDirector.Client      # JSON 序列化 + 游戏桥 HTTP 客户端（net8，供 CLI/MCP 复用）
src/GameDirector.Decisions   # 有限候选决策协议、Jev provider、证据与审计模型
src/GameDirector.Cli         # gd 命令行：validate / play / stop / status / manifest / capture / take / edit / grammar
src/GameDirector.Mcp         # MCP server（stdio），把同样能力暴露给任意 LLM 宿主
src/GameDirector.Workbench   # 独立导演窗口、模型 API 接入、项目与持久化出片任务
src/GameDirector.Core.Tests  # 编译器与播放器单测 + 样例资产防回归
packages/com.gamedirector.unity  # Unity 桥包：HttpListener 服务、镜头解释器、帧捕获、通用场景适配器基类
adapters/cdrebirth           # 首个游戏适配：manifest、适配器包骨架、接入计划
samples/timelines            # 样例分镜脚本（人工编写，供测试与演示）
docs                         # 架构 / DSL 规范 / 适配计划 / 路线图
tools/build_core_dll.sh      # 构建 Core DLL 到 Unity 包 Plugins/
```

## Quickstart（无 Unity 也可验证内核）

```bash
dotnet test                                    # 内核单测
dotnet run --project src/GameDirector.Cli -- \
  validate samples/timelines/pv_grimforest_demo.json \
  --manifest adapters/cdrebirth/manifests/cdrebirth.manifest.json
dotnet run --project src/GameDirector.Cli -- grammar   # 打印 DSL 速查
```

接游戏（首个适配场为私有项目 CDREBIRTH，M1 已验证，在游戏 Editor 播放态、沙盒场景内）：

```bash
dotnet run --project src/GameDirector.Cli -- manifest --endpoint http://127.0.0.1:39777
dotnet run --project src/GameDirector.Cli -- play samples/timelines/pv_grimforest_demo.json
dotnet run --project src/GameDirector.Cli -- capture --out captures/frame.png
```

## 状态

- M0：通用内核 + 测试 + CLI/MCP 骨架 + Unity 桥包源码 + 首个适配计划。
- M1（2026-08-26 完成）：首次实拍——24s 样例时间轴在沙盒完整播放、事件流与编译产物一致。踩坑与结论见 `docs/03-cdrebirth-adapter-plan.md` 的 M1 结果节。
- 后续里程碑见 `docs/04-roadmap.md`（M2/M3 已有真实录制剪辑实现，M4 已增加 Sandring/Three 实测，严格接入与发布验收见 docs/08）。

## Production workflow

The current implementation adds repeatable frame-stepped picture takes and a game-neutral edit renderer. Read `docs/05-production-contract.md` for findings, evidence and remaining acceptance lanes. A live manifest omits unbound audio; picture takes declare audio as post-production instead of pretending to record engine sound.

```bash
dotnet run --project src/GameDirector.Cli -- take timeline.json --out captures/edit/take-001 --fps 24 --width 1280 --height 720
dotnet run --project src/GameDirector.Cli -- edit edit.json --out captures/edit/cut-001
```

Each take writes its original timeline, live manifest, numbered PNG frames, event/visibility receipts, `picture.mp4`, and a verified `take.json` with source/media hashes. New output directories prevent overwriting takes. `take/start` starts one session; `take/frame` uses its take id and exact frame index, so retrying a lost frame response cannot advance the performance twice. Stop, cancellation, failure and completion release borrowed state. A 60-second idle lease ends abandoned capture sessions.

An edit plan declares `version:1`, `frameRate`, `width`, `height`, `sources` (id to media path), and `ranges` (`source`, `start`, `end`, `beat`, `reason`). Optional `grade`, `music`, `musicVolume` and `subtitles` support finishing. Paths resolve relative to the plan. Each segment and the final movie must retain the exact planned frame count and frame rate. Cuts align to output frames; source bounds and verified take hashes are checked before rendering. Graded H.264/PCM MOV segments use an exact rational video clock and concatenate without intermediate AAC priming; final audio encodes once. The output contains `final.mp4`, a contact sheet and `delivery.json`. Technical verification does not replace watching the film.

Unity adapters own only offline presentation. Every scene registry must live in a class-matching script file. A game auto-launcher must honor the explicit `OfflinePresentationScene` marker before starting networking.

Video requires FFmpeg with `libx264`, plus `libass/subtitles` for captioned edits, and ffprobe. Capabilities are checked before recording/rendering starts. `GAMEDIRECTOR_FFMPEG` and `GAMEDIRECTOR_FFPROBE` can select isolated executables without replacing system tools. Use `gd media status` and the offered installer to obtain a complete pair.

真实导演剪辑样例：[《别停机 / KEEP ROLLING》](../samples/timelines/directors-cut/README.md)，包含三段时间轴、十二个剪辑决定、字幕和配乐制作配方。完整交付与复拍验证见 [生产记录](05-production-contract.md)。

动画宣传片样例：[CAM DOWN! 动画 PV](../samples/timelines/camdown-pv/README.md)。真实持机道具、双人走位、怪物表演、对白与成片均由可复用时间轴和剪辑流程完成。

## 0.2.0-alpha.1 foundation candidate

Current implementation and measured limits: [foundation report](08-foundation-hardening-2026-09-07.md).
Historical engine validation covers Unity/CDREBIRTH and Three/Sandring on macOS; current Windows independent Unity validation is recorded above.

```sh
dotnet run --project src/GameDirector.Cli -- doctor
dotnet run --project src/GameDirector.Cli -- guide
dotnet run --project src/GameDirector.Cli -- catalog-init --manifest manifest.json --out performances.json
dotnet run --project src/GameDirector.Cli -- catalog-check performances.json --manifest manifest.json
dotnet run --project src/GameDirector.Cli -- resume captures/interrupted-take
python3 tools/package_release.py --runtime win-x64 --out dist/candidate-name
```

Custom Unity script visuals implement `IDirectorPresentationParticipant`; they are
explicitly clocked while gameplay callbacks remain disabled. Add
`com.gamedirector.unity` to the Unity project's `testables` to discover its Editor tests.
Takes persist frame hashes and an exclusive job journal. Resume either encodes a
complete frame set offline or replays into a new preserved attempt after source checks.
The [director guide](07-directing-playbook.md) is embedded in CLI/MCP distributions.
The [Sandring recipe](../adapters/sandring/README.md) shows the second engine path.

Release packaging includes the .NET CLI/MCP, Unity packages, Three interpreter,
adapter recipes, samples and docs, with hashes in candidate.json. It does not include
private game source, videos, credentials or a promise of unrun platform/human acceptance.
