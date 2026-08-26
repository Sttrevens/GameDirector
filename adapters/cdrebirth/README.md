# GameDirector × CDREBIRTH 适配器

CDREBIRTH 是 GameDirector 的第一个适配试验场。本目录只放 **CDREBIRTH 专属**内容：

- `manifests/cdrebirth.manifest.json` — 游戏能力清单（角色/动画/点位/音频/镜头词汇）。契约性资产，被内核单测引用，改动会触发 `SampleAssetTests`。
- `com.gamedirector.cdrebirth/` — Unity 适配器包（L3）骨架。通过 local package 方式被 CDREBIRTH 引用（`Packages/manifest.json` 加 `"com.gamedirector.cdrebirth": "file:<此目录绝对路径>"`），**源码不进 CDREBIRTH 仓库**。
- 接入计划、权威边界（Fusion Host/Client）、与游戏内 AI Director (DM) 的共存策略：见 [`../../docs/03-cdrebirth-adapter-plan.md`](../../docs/03-cdrebirth-adapter-plan.md)。

规则：manifest 里的 clip 名、点位、角色 id 必须与游戏真实资产同步维护——它是 LLM 编排的唯一合法词汇表。
