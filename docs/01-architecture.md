# 01 — 架构：为什么是这个形状

> 目标读者：给这个内核做扩展的人、写第二个游戏适配器的人、以及驱动它的 LLM。

## 问题的一阶分解

"LLM 导演调度游戏资产"本质上是三件事：

1. **表达**：把创意意图写成一份机器可校验的分镜脚本（时间轴）。—— DSL 的问题
2. **证伪**：在渲染任何一帧之前，证明这份脚本在这个游戏里可执行。—— 编译器的问题
3. **执行**：同一脚本在任何时刻重放都产生同一事件序列。—— 确定性播放器 + 适配器的问题

实时性不是目标：**可复现**才是。PV 是录出来的，不是直播出来的。所以 LLM 不逐帧遥控游戏（延迟、抖动、联机权威全都会炸），而是离线编译一份时间轴资产，游戏内引擎精确执行。LLM 是导演，不是木偶师。

## 分层与仓库映射

```
L0 传输/桥接     packages/com.gamedirector.unity (HttpListener, 主线程封送)
                 src/GameDirector.Mcp (stdio MCP)  ·  src/GameDirector.Cli (gd)
L1 导演 DSL      src/GameDirector.Core  ← 唯一不允许依赖任何引擎/游戏的部分
                 ├ Dsl/         时间轴 + 清单 POCO（无 attribute，双序列化器兼容）
                 ├ Compilation/ TimelineCompiler（证伪器）
                 ├ Playback/    TimelinePlayer（确定性执行器）
                 └ Adapters/    IGameDirectorAdapter（L3 契约）
L2 能力清单      adapters/<game>/manifests/*.json（离线校验用）
                 + 运行时 GET /manifest（游戏自描述，LLM 的合法词汇表）
L3 游戏适配器    packages/com.gamedirector.unity/GameDirectorAdapterBase（Unity 通用 80%）
                 adapters/cdrebirth/com.gamedirector.cdrebirth（首个实例，薄）
```

依赖方向只允许向下。Core 是 netstandard2.1 零依赖：同一个 DLL 既进 dotnet 工具链，也直接进 Unity `Plugins/`。

## 数据流（一次完整的导演循环）

```
LLM ──① 拉 manifest──▶ 游戏运行中 (GET /manifest)
LLM ──② 写 timeline.json（只用 manifest 词汇）
LLM ──③ gd validate ──▶ TimelineCompiler（本地证伪，秒级，零渲染）
LLM ──④ gd play ──▶ 桥 ──▶ 游戏侧二次编译 ──▶ TimelinePlayer ──▶ Adapter ──▶ 引擎
LLM ──⑤ gd capture /status ──▶ 看画面、看事件流 ──▶ 回到 ② 改稿
```

第 ④ 步游戏侧会**再编译一次**（不信任外部进程）。第 ⑤ 步是质量地板：没有"审片—改稿"闭环的 AI 导演是玩具。M2/M3 会把 ⑤ 升级为 AVPro 视频 take + 多 take 挑选（AI 剪片）。

## 关键不变量

- **确定性**：同一时间轴 + 同一 dt 序列 ⇒ 同一事件序列（有单测锁定）。时间轴用非缩放时间驱动，慢动作 cue 不影响 cue 自身的时刻。
- **显式恢复**：`world.timescale` 带 duration 时，编译器注入一条显式 restore cue。编译产物即完整事件清单，没有隐藏行为。
- **编译期在场模拟**：编译器按顺序模拟 spawn/despawn，对"怪物还没出生就播动画"这类错误给 warning。`PresentAtStart=false` 的角色必须先 spawn。
- **场景真名不出现在内核**：内核只见 role/location id；id→Transform 的解析全部在适配器侧（场景 marker 优先，manifest 坐标兜底）。
- **桥是 loopback-only 的录制工具**：默认只在 Editor play mode 起，release build 拒绝启动。

## 两个"导演"的边界（重要）

游戏内已有的 AI Director/DM（任务/评分真相）与本项目是**不同物种**：DM 是玩法真相的权威，GameDirector 是呈现层的摄影师。v0 的共存策略是物理隔离——director mode 跑在无 DM/LiveShow 的专用沙盒场景。任何"让 GameDirector 指挥真实联机局"的想法都是 v1 议题，且必须走 Host 权威 + RPC 扇出设计（见 03 的权威透镜）。

## 当前未实现（刻意留白）

- 时间轴 seek / 倒放（M3 剪辑需要时再设计）
- cue 的开放式扩展字段（用 `shot.params` 顶住，真不够再加 schema 版本 0.2）
- 联机实拍（v1）、视频录制（M2，走 AVPro）、多机位同时渲染（M2+）
