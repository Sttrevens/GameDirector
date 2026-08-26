# 02 — DSL 规范 v0.1

时间轴是一个 JSON 文件。LLM 写它、人审它、编译器证伪它、播放器确定执行它。`gd grammar` / `GdGrammar` 会打印同样的速查。

## 顶层结构

```json
{
  "version": "0.1",               // 必须精确匹配，否则 GD1000
  "id": "pv_grimforest_demo",     // 必填，GD1010
  "title": "人类可读标题",
  "metadata": { "任意": "键值对，不影响执行" },
  "cues": [ ... ]
}
```

## Cue 一览（时间单位：秒）

| type | 字段 | 语义 |
|---|---|---|
| `camera.shot` | `shot: ShotSpec` | 开始一个镜头；`durationSeconds=0` 表示持续到下一条 shot cue |
| `actor.spawn` | `role, location, headingTo?` | 在清单点位生成角色 |
| `actor.despawn` | `role` | 移除角色 |
| `actor.anim` | `role, clip, fade?` | 动画 crossfade 到 clip |
| `actor.move` | `role, to, speed?, headingTo?` | 向点位移动（ locomotion 由适配器决定） |
| `actor.face` | `role, headingTo` | 转身朝向角色或点位 |
| `audio.play` | `audioId, volume? 0..1` | 播放清单音频 |
| `audio.stop` | `audioId` | 停止 |
| `world.timescale` | `scale, duration?` | 时间缩放；`duration>0` 时编译器注入显式恢复 cue |
| `marker` | `label` | 纯标记：剪辑拍点 / 审片注记 |

### ShotSpec

```json
{
  "type": "lockoff | dolly | orbit | tracking | crane",
  "subject": "role id（必填）",
  "frame": "extreme-closeup | closeup | medium | full | wide",
  "from": "点位 id 或 \"current\"",
  "to": "运动镜头的终点点位 id",
  "durationSeconds": 4.0,
  "fov": 35.0,                 // 可选，5..170
  "ease": "linear | in | out | inOut",
  "lookAt": "可选：看向另一个 role",
  "params": { "shake": 0.4, "orbitDeg": 120.0 }   // 开放数值参数，适配器自定义风味
}
```

镜头语义（Unity 通用解释器 `CinematicCameraRig` 的约定，其他引擎适配器应对齐）：

- `lockoff` 固定机位；`dolly/crane` from→to 插值；`orbit` 绕主体转 `params.orbitDeg`（默认 90°）；`tracking` 保持初始偏移跟拍。
- `frame` 决定默认构图距离（ecu 0.6m / closeup 1.4m / medium 3m / full 5m / wide 10m），`from` 指向具体点位时点位的空间关系优先。
- 主体定位用 Renderer bounds 中心，无 Renderer 则 +1.2m 胸口高。

## 编译器规则（证伪器）

**Error（阻断播放）**：版本不符 `GD1000`、未知角色 `GD1001`、未知 clip `GD1002`、未知点位 `GD1003`、未知镜头类型 `GD1004`、非法时间 `GD1005`、未知 cue 类型 `GD1006`、未知景别 `GD1007`、未知音频 `GD1008`、非法数值（负速度、volume 超界、fov 超界等）`GD1009`、缺 id `GD1010`。

**Warning（可播放但需人审）**：镜头时间重叠（后者接管）`GD2001`、对可能未生成的角色做操作 `GD2002`、空 marker `GD2003`。

**注入行为**：`world.timescale` 带 `duration` 时，编译产物中会出现一条 `injected=true` 的恢复 cue（scale=1），时刻为 `t+duration`。编译产物即完整事件清单。

**排序**：按 `t` 稳定排序；同刻 cue 按书写顺序；注入恢复 cue 排在同刻最后。

## 兼容性承诺

- v0.1 只增不改：新语义 = 新 cue type 或新 ShotSpec 可选字段；不改旧字段含义。
- 破坏式变更必须 bump `version` 并同步编译器 `SupportedVersion`。
- `adapters/*/manifests/*.json` 与 `samples/timelines/*.json` 是契约资产：`SampleAssetTests` 锁定它们永远可解析、零 error 编译。
