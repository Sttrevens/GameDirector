# 04 — 路线图：从内核到 AI 剪片

## M0 — 通用内核（本提交）

零依赖 DSL/编译器/播放器 + 单测 + CLI + MCP 骨架 + Unity 桥包源码 + CDREBIRTH 适配计划。
验收：`dotnet test` 全绿；样例时间轴对 CDREBIRTH manifest 零 error 编译。**不触碰 CDREBIRTH 仓库。**

## M1 — 进游戏（CDREBIRTH 首次实拍）

按 `docs/03` 的接线清单执行。验收：24s 示范时间轴在 grimforest 沙盒完整播放、`gd capture` 帧可用、怪物 AI 被导演模式完全接管。
关键风险：怪物呈现模式隔离不干净（AI 抢戏）；Unity 侧代码首次真实编译（本仓库无法静态验证 Unity 程序集）。

## M2 — 录制与审片闭环

- 适配器驱动 AVPro Movie Capture：一次播放 = 一个视频 take + 编译产物事件清单（EDL 素材元数据）。
- `gd` 增加 `take` 命令：play + record + 落盘命名规范（`{timelineId}/take{n}_{timestamp}.mp4`）。
- LLM 审片：抽帧（ffmpeg 均匀抽帧 + marker cue 时刻精准抽帧）→ 多模态审片 → 改时间轴重拍。
验收：LLM 对同一脚本连拍 3 take，能指出至少一处构图/节奏问题并产出修改后的合法时间轴（编译零 error）。

## M3 — AI 剪片

- 多脚本多 take 素材池 + 事件清单即 EDL（edit decision list）：marker cue 是天然剪辑点。
- `gd edit`：输入多个 take 元数据，输出 ffmpeg concat/xfade 装配命令，生成成片。
- 需要时为播放器加 seek（剪辑预览需要跳帧），这是唯一被路线图背书的播放器扩展。
验收：从 ≥3 条时间轴的素材里，LLM 产出一版 30-60s 成片，含至少一次镜头组接决策的理由说明（为什么这个 take 的这条镜头）。

## M4 — 通用性证伪（产品化判官）

**用第二个游戏验证"通用"不是幻觉**：选一个架构差异大的项目（Godot 或另一个 Unity 项目），只写 manifest + 薄适配器。
验收指标（写进产品叙事）：
- 适配工作量 ≤ 5 人日；
- Core/CLI/MCP 零改动（只允许加 cue type，且加完必须向后兼容 v0.1）；
- 第二个游戏的样例时间轴复用 ≥ 60% 的 cue 词汇。
达不到 ⇒ 分层设计错了，回去改内核，不是改适配器。

## 明确不做（当前）

- 实时逐帧遥控 / 直播导演（与确定性原则冲突，除非未来出现真实需求）
- 游戏玩法真相干预（那是各游戏自己的 AI/任务系统；GameDirector 只做呈现层）
- 非 loopback 的桥、鉴权、远程控制（录制工具，不是服务端）
