# GameDirector × CDREBIRTH 适配器

CDREBIRTH 是 GameDirector 的第一个适配试验场。本目录只放 **CDREBIRTH 专属**内容：

- `manifests/cdrebirth.manifest.json` — 游戏能力清单（角色/动画/点位/音频/镜头词汇）。契约性资产，被内核单测引用，改动会触发 `SampleAssetTests`。
- `com.gamedirector.cdrebirth/` — Unity 适配器包（L3）骨架。CDREBIRTH 以 **embedded 快照**消费（`Packages/com.gamedirector.cdrebirth`，由 CDREBIRTH 侧 `tools/dev/sync_gamedirector.sh` 单向同步、记录源 commit），**源码不进 CDREBIRTH 仓库的版本库历史，快照只读**。不要用绝对 `file:` 路径引用（会破坏其他机器的包解析）。
- 接入计划、权威边界（Fusion Host/Client）、与游戏内 AI Director (DM) 的共存策略：见 [`../../docs/03-cdrebirth-adapter-plan.md`](../../docs/03-cdrebirth-adapter-plan.md)。

规则：manifest 里的 clip 名、点位、角色 id 必须与游戏真实资产同步维护——它是 LLM 编排的唯一合法词汇表。
