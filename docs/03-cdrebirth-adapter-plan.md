# 03 — CDREBIRTH 适配计划（第一个试验场）

> Context read（按 cdrebirth-docs-orient）：`AGENTS.md`、`Docs/ProjectPulse/README.md`、`Docs/ProjectPulse/FeatureRouting.md`、`Docs/AI_Director_Level_Line/README.md`、`Docs/FilmingSystem/README.md`。路由结论："AI Director" 一词在 CDREBIRTH 内默认指向 `Docs/AI_Director_Level_Line`（玩法真相的 DM）；**本项目不是那个系统**，是外部呈现层导演，二者物理隔离共存（见下）。

## 从 CDREBIRTH 现状推出的三个适配事实

1. **游戏代码不依赖 Cinemachine / Timeline（包已安装但游戏代码基本不用）。** `Packages/manifest.json` 里有 cinemachine 2.10 / timeline 1.7.6，但全项目只有 `Player.cs` 引用 Cinemachine，Timeline 仅出现在第三方插件里 ⇒ 镜头系统由 GameDirector 自带通用解释器（`CinematicCameraRig`）承担，不与游戏内联机时代的相机栈（`AimCameraLock` 执行序 32700 等）纠缠，也不给游戏仓库加新依赖。
2. **已有 AVPro Movie Capture（RenderHeads）。** M2 的视频 take 直接驱动它，不需要引入新录制方案。
3. **联机权威纪律极强（runner-scoped、禁全局单例、SA/IA/peer 分层）。** ⇒ v0 彻底绕开：director mode = 无 NetworkRunner 的离线沙盒场景。桥包代码也遵守项目教训：全部状态挂在场景组件上，零静态（marker registry 除外——纯呈现查找表，OnEnable/OnDisable 对称维护）。

## 权威透镜（Fusion Host/Client，按 AGENTS.md 要求显式分类）

| 动作 | v0（沙盒） | v1（联机实拍，未实现） |
|---|---|---|
| 镜头/构图/慢动作/截图 | local-only（沙盒内无网络概念） | peer-local 呈现，Host 机位为默认录制端 |
| 怪物 spawn / 动画 / 位移 | local-only Instantiate（无 runner） | **StateAuthority-owned**，Host 执行 cue，经 RPC/`[Networked]` 扇出到各 peer |
| 时间轴播放/停止 | local-only | Host 持有播放真相，peer 只镜像呈现事件 |
| 玩家输入 | 不涉及（沙盒无玩家控制） | 导演模式建议冻结 IA 输入或转入观战 |

v0 判定：任何需要 Fusion 权威的 cue 都**不允许**出现在沙盒外。`manifest.capabilities["director.mode"] = "offline-sandbox"` 是这个承诺的机读形式。

## 与游戏内 DM 的共存

DM（LLMTaskDirector/LiveShow/Ambient）是玩法真相（任务、评分、热度）。GameDirector 进沙盒场景时，该场景**不加载** DM/LiveShow/ScoreManager 等系统——不是"禁用"，是"场景里根本没有"。两个导演永不同时指挥同一批 actor。

## 角色与点位绑定方案

- **角色**：`hero` → 本地玩家（`PlayerMovement` 所在 transform；M1 接线时直接解析，marker 兜底）；`bigguai` / `speaker` → `CdRebirthAdapter` 序列化的 prefab 引用（M1 从 PGC 资产源绑定）。生成后必须**关掉怪物 AI/仇恨组件**（进入"导演控制的呈现模式"），否则时间轴和 AI 会抢方向盘——这是 M1 的关键 falsifier。
- **动画 clip**：manifest 中的 clip 名（`RageExpose`/`ConfidencePose`/`Tornado` 等，源自 ProjectPulse 前景语义）必须在 M1 与真实 Animator Controller state 名逐一核对，不一致以**真实资产**为准改 manifest——manifest 永远向游戏资产对齐，不反向。
- **点位**：在 grimforest 沙盒场景副本里摆放 `DirectorLocationAnchor`（id 对应 `gf_gate` / `gf_stage` / `gf_cam_*`）。场景 marker 是运行时真相；manifest 坐标只是离线校验与文档。
- **音频**：M1 接到项目音频服务/mixer；v0 打日志占位。

## M1 接线清单（下次进 Unity 时执行）

1. CDREBIRTH `Packages/manifest.json` 增加 local package 引用：`com.gamedirector.unity` 与 `com.gamedirector.cdrebirth` 指向本仓库绝对路径（**源码不进 CDREBIRTH 仓库**；仓库侧只多两行 manifest 引用，可整行 revert）。
2. 跑 `tools/build_core_dll.sh` 生成 `Plugins/GameDirector.Core.dll`，让 Unity 异步编译（遵守 `Docs/AgentWorkflow/UnityEditorMonoGcStackOverflow_2026-08-21.md`：禁止 ForceSynchronousImport；等 Editor 自然编译完再查 Console）。
3. 复制 grimforest 场景为沙盒副本 → 摆 `DirectorLocationAnchor` + 挂 `DirectorBridgeServer/DirectorRuntime/CdRebirthAdapter` → manifestJson 指向 `cdrebirth.manifest.json`。
4. Play mode 验证四步：`gd manifest` 拉清单 → `gd validate` → `gd play samples/timelines/pv_grimforest_demo.json` → `gd capture` 看帧。预期 falsifier：若 BigGuai AI 抢戏（自行移动/攻击），说明呈现模式隔离没做干净，回到清单第 3 步。
5. 验证后按 CDREBIRTH 仓库规矩在其 `Docs/` 留一条接入记录（本仓库不动那条规矩，那次提交才算"触碰"CDREBIRTH）。

## 验收标准（M1 完成定义）

在打开的沙盒场景里，`pv_grimforest_demo.json` 全程 24s 无人工干预播放完毕，事件流与编译产物一致，`gd capture` 在怪物揭示（t≈8s）抓到的帧里 BigGuai 位于画面主体区。三条缺一即 M1 未完成。

## M1 结果（2026-08-26，已完成）

三条验收全部达成：24s 时间轴在 grimforest 沙盒完整播放（`final state: Finished`），事件流与编译产物一致，揭示帧 BigGuai 居中且环境可读（6 个勘察候选位经实拍筛选后定点）。

### M1 踩坑记录（后续适配者必读）

1. **二进制场景 + 同类型脚本组件 = 组件静默丢失。** CDREBIRTH 场景是二进制序列化；保存→重载后多个同类型 MonoBehaviour 会被丢弃（实测 5 个锚点组件丢 2-3 个，原生组件不受影响）。**对策（已是桥包标准模式）：每种脚本类型每场景只放一个注册表组件**（`DirectorLocationRegistry`：位置 = 子物体；`DirectorRoleRegistry`：条目引用原生 Transform）。
2. **编辑态无 OnEnable，静态注册表会缓存已销毁对象。** 场景重载（非 domain reload）后静态列表里是假 null 尸体，且"空才扫描"的优化让它永不恢复。**对策：查找前 RemoveAll 假 null，空缓存时重新 FindObjectsOfType。**
3. **manifest 以 TextAsset 形式供给运行时**（包内 Runtime/cdrebirth.manifest.json，由 `tools/sync_unity_package.sh` 从规范源复制）；改 manifest 后必须 refresh + 重进播放态，否则游戏服务的是旧清单。
4. **每次 take 要干净世界：退出播放再进入**（场景从磁盘重载）；v0 没有世界内重置。M2 的 `gd take` 应内置 reset。
5. **怪物/英雄必须进呈现模式**（`PresentationMode.Apply`：禁用除 Animator 外全部 Behaviour + 刚体运动学化），否则游戏 AI 与时间轴抢方向盘。
6. **取景不能盲摆锚点**：用一条勘察时间轴（`samples/timelines/scout_grimforest_gate.json`）批量实拍候选机位再定点——这就是 LLM 日后的 location scouting 回路。
7. 已知未决：`gf_stage`(3,0.1,14) 深入站台建筑内部，t≈16 后主体被遮挡——分镜 staging 问题（重设 stage 点位或改英雄走位），不影响系统验收。

### M1 过程还发现的环境事实

- `gd` CLI / 桥的 play 入口在刚进播放态几秒内可能抢跑（桥未就绪）；调用方应重试或先探 `/health`。
- 播放中发生 domain reload 会清空播放会话（桥自动恢复，但当条 take 作废）——拍摄期间不要触发编译。
