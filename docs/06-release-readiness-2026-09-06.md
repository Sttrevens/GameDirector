# GameDirector 泛化与发布评估 · 2026-09-06

This is the historical pre-hardening review. Current implementation and acceptance: [2026-09-07 foundation report](08-foundation-hardening-2026-09-07.md).

## 判断

当前适合内部离线制作，以及由维护者陪同的技术 Alpha；尚不满足面向其他团队、自助接入的正式产品发布条件。

通用内核、时间轴执行、录制事务和剪辑输出已经有实现及实片证据。跨游戏适配成本、独立用户成功率和跨项目审美质量仍未得到证明。不能把 CDREBIRTH 的三次导演作品当作三个独立游戏的验证。

这次只评估，没有修改产品实现、切换 Unity 场景或发布软件。被评估对象是 HEAD `0d8064b49ff8d0f511feaa30feaebb493533a052` 加当前未提交工作区，不能用该 SHA 单独复现新版能力。

## 从用户结果定义 release ready

一位没有参与开发的目标用户，应能在声明支持的环境中：安装固定版本 → 接入自己的合法游戏资产 → 得到真实能力清单 → 编排并预览 → 修改和补拍 → 导出完整声画 → 遇到失败后恢复工作。上述流程必须保护项目，并让用户清楚知道不支持的功能。

“任何游戏”需要拆开：另一个同类 Unity 项目、渲染/动画架构不同的 Unity 项目、另一种引擎、没有源码或接入权限的成品游戏。当前对这四类的覆盖不能等同。

| 范围 | 当前依据 | 判断 |
| --- | --- | --- |
| CDREBIRTH、macOS、Unity 2022.3、已准备的离线导演场景 | 多次录制、补拍、真实 1080p 成片、同机复拍 | 可以继续内部生产 |
| 另一个以原生 Animator 为主的 Unity 3D 项目 | 有 GenericSceneAdapter、注册表、通用镜头与逐帧捕获 | 架构上可接，尚无第二项目运行验收 |
| 脚本驱动表情/IK、程序动画、复杂渲染效果的 Unity 项目 | 当前呈现接管有具体限制 | 需要补齐适配契约，不能承诺直接保真 |
| Godot / Unreal | Core 不依赖 Unity；传输与时间轴概念可复用 | 缺引擎桥、呈现时钟、相机/捕获实现及端到端验证 |
| 无接入权限的任意商业游戏 | 当前依赖游戏内桥和资源绑定 | 不在当前产品支持范围 |
| 联机真实游戏局 | 当前明确离线隔离 | 未实现，不能由动画里的直播情节推导出来 |

## 主要 findings

### F1 · 脚本隔离尚未区分玩法逻辑与必要的视觉逻辑

**优先级：P1，阻塞广泛 Unity 兼容承诺。**

`PresentationMode.CreateActor` 删除生成角色的所有 MonoBehaviour；`Lease` 禁用借用角色的所有 MonoBehaviour。`GameDirectorAdapterBase` 自行推进 Animator、ParticleSystem 和直线位移，并关闭根运动及动画事件。它能避免玩法系统抢控制，但也会影响由脚本提供的面部渲染、IK、绑定、程序动画和视觉事件。

这是从当前代码可确认的机制；尚未在另一个游戏中实际测量影响。CDREBIRTH 新片为面部单独制作原生材质动画，证明了当前案例能适配，不等于其他视觉脚本自动兼容。

**完整修复方向：**游戏适配层显式声明哪些组件负责玩法、哪些负责呈现，以及呈现参与者的准备、逐帧推进、快照与恢复责任。不能通过全量保留脚本来解除隔离，也不应在 Core 加游戏名特判。用含一个脚本视觉依赖的第二个项目验收。

证据：`packages/com.gamedirector.unity/Runtime/PresentationMode.cs:13`、`:63`；`GameDirectorAdapterBase.cs:134`、`:157`。

### F2 · “薄适配器”只验证过一个游戏，不能外推到第二引擎

**优先级：P1，阻塞通用产品声明。**

Core 接口只使用角色、位置和镜头数据，CLI 的 HTTP 调用和剪辑器也没有游戏依赖，这部分分层成立。但当前唯一的引擎运行实现位于 Unity 包，唯一游戏适配实例是 CDREBIRTH。切到 Godot/Unreal，仍需要对应的桥接、执行时钟、呈现生命周期、相机、帧捕获与预检实现；不是只补一份角色清单就能拍。

**验收方向：**先在结构不同的第二个 Unity 项目完成一支 30–60 秒作品，记录全部接入、资产准备、排错和补拍成本。沿用现有 M4 目标：≤5 人日、既有功能下 Core/CLI/MCP 不改、≥60% cue 词汇复用。这些是待验证目标，不是当前已达标事实。第二引擎承诺再通过对应引擎的独立项目验收。

证据：`src/GameDirector.Core/Adapters/IGameDirectorAdapter.cs:16`；`packages/com.gamedirector.unity/Runtime/GenericSceneAdapter.cs:13`；`docs/04-roadmap.md:27`。

### F3 · 艺术指导和声音制作尚未成为随产品交付的通用流程

**优先级：P1，相对于“高审美、丝滑导演”的产品承诺。**

清单提供动画名称、角色、位置等合法词汇，没有统一的动作时长、表演含义、接触/持物约束和关键动作时刻描述。镜头自动检查是碰撞体可见性抽样，不判断叙事是否清楚、表情是否合适或声音是否自然。

本片的灯光、材质、面部动画、对白、弹幕、声音与审片由本次导演工作及游戏样例脚本提供。声音样例直接依赖 CDREBIRTH 音频目录和 macOS `say`。工具可以执行这些决定，但仓库还没有让一个新宿主可靠复用整套导演过程的完整交付物。

**完整修复方向：**提供可复用的素材勘察与表演描述、风格约束、分镜—预演—审片—补拍工作流，以及可替换的声音输入。保留人或 Agent 的创意决策，不把固定模板数量当作审美质量。由未参与开发的使用者完成成片，检查玩法辨识度、画面连续性、字幕与声音。

证据：`src/GameDirector.Core/Dsl/ManifestModels.cs:73`；`packages/com.gamedirector.unity/Runtime/CinematicCameraRig.cs:80`；`adapters/cdrebirth/com.gamedirector.cdrebirth/Editor/LivePvStageBuilder.cs:18`；`samples/timelines/camdown-live-pv/build_soundtrack.py:6`。

### F4 · 中断恢复只有短暂帧重试，没有持久生产任务恢复

**优先级：P2，阻塞无维护者陪同的可靠长任务。**

当前同 take id / frame index 重试、失败记录和 60 秒空闲清理是正确的基础。客户端录制仍是单次循环，输出目录必须全新；引擎只保留内存中的当前 take 和最后一帧。进程或 Editor 重启后，没有从已有任务检查点继续的产品入口。补拍当前要重演前缀来重建状态。

**完整修复方向：**提供持久任务清单与恢复入口，区分已录制可重新编码、未完成需重演/重拍、源资产变化后禁止沿用三种情况；复用现有来源哈希与事务身份，不强行实现不可靠的任意时间 seek。以中断、启动响应丢失、编码失败和空间不足等真实失败路径验收。

证据：`src/GameDirector.Client/TakeRecorder.cs:9`；`DirectorBridgeClient.cs:90`；`packages/com.gamedirector.unity/Runtime/DirectorRuntime.cs:15`、`:70`。

### F5 · 缺固定发布候选及干净机器交付验证

**优先级：P1，阻塞正式发行。**

本次实时查询 GitHub：Release 列表为空，Actions workflows 数量为 0；本地无 tag。新版关键实现仍有未提交及未跟踪文件。Unity 包仍标为 0.1.0，Core DLL 由脚本生成同步；Quickstart 主要从源码执行。MCP 有打包配置，但不等于安装与分发已验收。

**完整修复方向：**冻结一个包含源代码、生成包和依赖约束的版本，建立 CI 的构建/测试/包一致性检查，并在声明支持的干净机器上执行安装、接入、样片和升级验收。先声明经过实测的系统、Unity 与渲染管线范围。许可证、分发素材和字体也需要在打包时明确；本次未进行授权审计。

正式发布不一定要求公网 SaaS、GUI 或联机导演。可以先发布有明确范围的本地 CLI/MCP + Unity 离线工具。

证据：`README.md` Quickstart/消费方式；`src/GameDirector.Mcp/GameDirector.Mcp.csproj`；`packages/com.gamedirector.unity/package.json`；实时 GitHub Release / workflows 查询和本地 `git status`。

## 验证结果及边界

- **本次执行：**完整 .NET solution 构建通过，0 warning / 0 error；30 项测试通过、0 失败、0 跳过。它们不编译或运行 Unity 程序集。
- **本次读取已有生产证据：**新版 54 秒、1080p/24fps、1296 帧交付；同机重复录制 24/24 PNG 相同；已有 24/30fps 源尾帧、字幕及混音回归产物；两份嵌入包同步差异为零的记录。
- **未执行：**第二项目接入、第二引擎、Windows/其他渲染管线、干净安装、Editor 崩溃恢复、真人独立制作验收。
- **艺术证据限制：**新版已有 71 张关键画面抽样复查；连续听审未完成，不能用媒体结构校验替代声画与审美验收。

生产记录：[新版成片审阅](../captures/edit/pv/live-cut/delivery-v2/review.json)、[同机复拍](../captures/edit/pv/live-cut/repeat/repeat.json)。媒体位于本地忽略目录，并非已分发给新用户的发布资产。

## 建议的最小发布路径

1. 先把候选范围定为“明确版本与渲染管线的 Unity 离线 AI 导演工具”，保留跨引擎北极星。
2. 在第二个真实 Unity 项目完成接入、预演、正式录制、局部修订和 30–60 秒成片；用实际差异修正呈现契约。
3. 冻结候选版本，建立安装诊断、发布 CI 与持久失败恢复，并让另一位用户在干净环境中独立完成相同闭环。
4. 通过上述验收后发布有限范围 Beta；重复满足稳定性与使用者验收后判断 1.0。第二引擎与联机导演分别验收，均不作为 Unity 离线版本的强制前置项。

“接入成功”的计时应包括为拍摄补做资产、视觉绑定和项目特定代码；审美评估应包括片子是否讲清游戏，而不只检查帧数。这样才能证明成本和质量确实能泛化。
