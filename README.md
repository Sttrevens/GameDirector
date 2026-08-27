# GameDirector

**通用游戏内 AI 导演内核** — 让 LLM 以"导演"身份编排游戏内相机、角色动画、怪物出场、音频与节奏，产出可复现的演出时间轴（分镜脚本），用于自动录制 PV / Machinima / 宣传素材，并最终支撑 AI 剪辑流水线。

第一个适配试验场：[CDREBIRTH](../CDREBIRTH)（Unity 2022.3 + Photon Fusion 合作魂系直播拍摄 roguelite）。

## 设计原则（第一性原理）

1. **LLM 是导演，不是木偶师。** LLM 离线产出一份确定性时间轴资产（shot list），游戏内播放引擎精确执行。不做逐帧实时遥控。
2. **通用的是协议、DSL 与导演 Agent；每游戏必须做的是一份自描述 manifest 加一个薄适配器。** 换游戏 = 写 manifest + 几天适配，不是重写一套。
3. **游戏自我描述。** MCP/CLI 连接游戏后先拉取能力清单（角色、动画 clip、点位、可用镜头语言、音频），LLM 只在清单约束内编排。编译器负责证伪。
4. **确定性优先。** 同一时间轴 + 同一 dt 序列 ⇒ 同一事件序列。时间轴可被人工编辑、被 LLM 生成、被版本管理、被回放复现。
5. **权威边界显式化。** v0 一律在离线/沙盒（director mode）执行，规避联机权威问题；联机实拍（Host 执导、RPC 扇出）是 v1 的显式设计点，不是隐含行为。

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
src/GameDirector.Cli         # gd 命令行：validate / play / stop / status / manifest / capture / grammar
src/GameDirector.Mcp         # MCP server（stdio），把同样能力暴露给任意 LLM 宿主
src/GameDirector.Core.Tests  # 编译器与播放器单测 + 样例资产防回归
packages/com.gamedirector.unity  # Unity 桥包：HttpListener 服务、镜头解释器、帧捕获、通用场景适配器基类
adapters/cdrebirth           # 首个游戏适配：manifest、适配器包骨架、接入计划
samples/timelines            # 样例分镜脚本（人工编写，供测试与演示）
docs                         # 架构 / DSL 规范 / CDREBIRTH 适配计划 / 路线图
tools/build_core_dll.sh      # 构建 Core DLL 到 Unity 包 Plugins/
```

## Quickstart（无 Unity 也可验证内核）

```bash
export PATH="/opt/homebrew/bin:$PATH"
dotnet test                                    # 内核单测
dotnet run --project src/GameDirector.Cli -- \
  validate samples/timelines/pv_grimforest_demo.json \
  --manifest adapters/cdrebirth/manifests/cdrebirth.manifest.json
dotnet run --project src/GameDirector.Cli -- grammar   # 打印 DSL 速查
```

接游戏（CDREBIRTH 为首个适配场，M1 已验证）：

CDREBIRTH 通过 **embedded 快照**消费两个 Unity 包（`Packages/com.gamedirector.unity`、`Packages/com.gamedirector.cdrebirth`），快照由 CDREBIRTH 侧的 `tools/dev/sync_gamedirector.sh` 从本仓库单向同步并记录源 commit。⚠️ 不要用 `file:/绝对路径` 引用本仓库——绝对路径会写进共享 `manifest.json`，其他机器（Windows/CI）无法解析（M1 初版踩过，已修复）。游戏成熟到需要 Git UPM 钉 commit 时再升级，前提是多机器/CI 对私有远端都有读权限。

接好之后（在游戏 Editor 播放态、沙盒场景内）：

```bash
dotnet run --project src/GameDirector.Cli -- manifest --endpoint http://127.0.0.1:39777
dotnet run --project src/GameDirector.Cli -- play samples/timelines/pv_grimforest_demo.json
dotnet run --project src/GameDirector.Cli -- capture --out captures/frame.png
```

## 状态

- M0：通用内核 + 测试 + CLI/MCP 骨架 + Unity 桥包源码 + CDREBIRTH 适配计划。
- M1（2026-08-26 完成）：CDREBIRTH 首次实拍——24s 样例时间轴在 grimforest 沙盒完整播放、事件流与编译产物一致、揭示帧 BigGuai 居中（6 候选机位经勘察时间轴实拍筛选）。踩坑与结论见 `docs/03-cdrebirth-adapter-plan.md` 的 M1 结果节。
- 消费方式：CDREBIRTH 内嵌快照（embedded packages，见上）。
- 后续里程碑见 `docs/04-roadmap.md`（M2 录制/审片，M3 = AI 剪片，M4 第二游戏证伪）。
