# Unity V1：第一轮产品化实现与验收

2026-09-12。本轮承接 CwcMontage 产品化审阅，实际实现 Unity 原生 Film Inspector 与本机网页共享作品的最小完整链路。候选为 `0.2.0-alpha.1 / unity-v1-candidate-20260912-r3`，基于现有大量未提交工作继续实现，没有提交、切换分支或公开发布。

## 从责任和状态出发

用户的创作对象是一份作品。Inspector、网页、MCP 都是编辑和调度入口，已接受版本与制作任务由同一个本地工作台管理。Unity 提供经过绑定的角色、动画、机位与画面执行。模型只提出可审阅方案，方案仍须经过作品保存和实际可拍性检查。

这条原则落实为五条约束：

1. **只有一条已接受版本链。** 每次保存把内容、父版本、内容哈希、请求凭据写进一个不可变原子记录。双端更新不会最后写入者静默覆盖；冲突返回最新版本，保留本地修改。
2. **任务使用提交时的确定版本。** 后续草稿变化不改变已经提交的制作。保存和制作响应丢失时，以原请求身份确认结果，避免误造重复任务。
3. **创作与执行有不同写入权限。** 用户保存 Film/创建场景是明确创作；发现、准备检查、拍摄、导出使用外部存储，不隐式保存游戏场景或自动安装插件。
4. **时间由导演控制。** 离线舞台的 Animator 平时禁用，导演会话显式开启、采样和恢复，防止 Unity 自主更新改变原始状态或拍摄指纹。
5. **第一段影片有自己的素材。** 包内菜单生成角色、实际 Animator 状态、声音、拍摄舞台和三镜头作品，不依赖 CDREBIRTH 私有素材或模型 API。

## 已实现

- 新增 FilmDocument 版本协议、多作品、历史读取、旧默认草稿只读兼容与显式迁移。
- 原生 Film ScriptableObject 和自定义 Inspector：场景/主角/声音引用，标题与输出设置，镜头时间、意图、对象、机位、运镜和景别，增删镜头/场景，保存与冲突处理，准备检查、制作及任务恢复。复杂表演/音轨/字幕脚本采用显式应用，输入中间态不会每帧被丢弃。
- 网页恢复未保存草稿，保留原始编辑基线；版本冲突可载入新版或将修改另存；切换作品同步网址，刷新保持选择；未确认保存/制作有明确恢复入口。
- 原生窗口和 MCP 接入相同作品协议。旧版不带版本的草稿写入返回 428，防止旧客户端破坏共享编辑。
- 工作台持久化 storeId、实例身份和本地端口描述，支持空闲端口启动。原生启动器寻找随包运行组件，无需 .NET SDK，关闭 Inspector 不停止制作服务。
- 提供 Unity 专用 UPM 候选包和可重复打包工具。构建在临时源码副本进行，不改变原仓库依赖锁；包包含自带 .NET 运行时的工作台、网页版和字幕字体，不包含私有游戏或账号配置。

## 实际发现并修复的失败

第一次样例拍摄没有通过：Unity 实际创建的 Animator 状态名与猜测名称不同。示例现在读取创建后的真实状态路径，影片方案和引擎清单使用同一个绑定事实。

之后发现舞台 Animator 在两次请求之间自动运行。只设 `speed = 0` 不会可靠保存在场景里；改为持久化禁用拥有的 Animator，再由导演会话控制其时钟，保留原有源状态校验。

实际审片发现中文字幕是方框。已随工作台提供未经修改的 [Noto Sans CJK SC](https://github.com/notofonts/noto-cjk/tree/main/Sans/OTF/SimplifiedChinese)，附 [OFL 许可证](https://github.com/notofonts/noto-cjk/blob/main/Sans/LICENSE)、固定来源和文件哈希。渲染明确加载该字体，缺失时准备检查失败；交付记录包含字体哈希。审片缩略图改为覆盖完整时长，避免短片只显示第一帧。

## 验证证据

以下路径均相对仓库根目录，完整产物保留在本机。

| 验证 | 结果 | 证据 |
|---|---|---|
| .NET 内核/影片契约 | 40/40 通过 | 本轮 `dotnet test GameDirector.sln --no-build` 输出 |
| 作品服务黑盒 | 14 项通过，包括并发、响应重试、重启、损坏记录、零写入旧草稿读取 | `captures/film-document-tests/9241989e1243/result.json` |
| 客户端状态 | 6 项通过，包括编辑恢复和未知结果去重 | `tools/test_document_state.mjs` |
| 工作台完整契约 + FFmpeg | 24 项通过，含版本固定和镜头缓存 | `captures/workbench-contract-tests/b8bf6c1b232c/result.json` |
| 最终 UPM 包导入全新 Unity 工程 | 12/12 Editor 测试通过、0 skipped | `captures/unity-v1-implementation-20260912/candidate-install/editmode.xml` |
| 自包含运行组件 | 移除 SDK 常用路径后启动成功；409 个包文件哈希匹配 | `captures/unity-v1-implementation-20260912/candidate-runtime/result.json` |
| 真实 Unity 拍摄 | 原生端保存 v1、HTTP 客户端保存 v2、旧版本写入 409、两版出片、3 镜头复用 2 个 | `captures/unity-v1-implementation-20260912/real-film-r5/result.json` |
| 源工程保护 | 拍摄前后 174 个源文件内容一致，0 写入 | `real-film-r5/source-before.json`、`source-after.json` |
| 网页人工操作路径 | 两窗口冲突、本地恢复、另存作品、深链接刷新和审片通过；无控制台错误 | `captures/unity-v1-implementation-20260912/browser-validation.json` |
| 媒体回归 | 帧率转换、源片尾、字幕合成、响度保持通过 | `captures/edit/media-tests/dacaf6ded0b7/` |
| 字幕画面 | 重渲染缩略图检查中文可见；最终 Unity 出片字体回执一致 | `font-fixed-delivery/contact-sheet.jpg` 与 `real-film-r5/` |

最终真实 Unity 视频：
`captures/unity-v1-implementation-20260912/real-film-r5/store/jobs/a8b71518b66d4fb998ebdbbd4f59673e/delivery-29d5689d5d3b4d67b71c1eb419fda194/final.mp4`

SHA-256：`223cbec43a13891aa0490cbad3fcc9d7b5f11a398c912974ad7f90680f84d32b`。

安装候选：`dist/unity-v1-candidate-20260912-r3.zip`，约 43 MiB。
SHA-256：`b64f5efc71e7ebf788027d64071837c4711e000acefc076697b32cef942b7829`。
包内 `candidate.json` 记录源工作树和交付文件哈希；`INSTALL.md` 是实际安装入口说明。

## 公开分享前仍需完成

当前交付是已跑通的本地试用候选。全新 Unity 工程导入通过不等于干净机器或陌生用户验收。

- **首装依赖闭合**：FFmpeg（libx264/libass）及 ffprobe 仍需配置；需要确定可再分发来源和许可证、随包安装/诊断、macOS 签名与公证，再把第一次出片变成可靠的一条路径。
- **平台承诺**：本轮只验证 macOS Apple Silicon + Unity 2022.3.34f1。Windows、Intel Mac、其它 Unity 版本、URP/HDRP 仍未验证，发布页不能泛称全部支持。
- **创作体验**：原生 Inspector 已有镜头编辑，复杂表演和声音仍有脚本入口。需要用真实内容补齐常用表演/音轨操作和 Inspector 人工使用验收；胶囊技术样例不代表宣传级样片。
- **项目与素材迁移**：Film GUID 经重命名稳定，复制产生新作品；整个工程搬家仍影响当前 projectId。单个 JSON 不包含外部声音库，完整作品搬家/备份恢复尚未产品化。
- **真实模型与新用户**：本轮不包含真实模型 API 验收；默认手工/样例流程不要求模型。需要陌生用户按安装说明完成首片，以及断线/重启/升级的用户操作验收。

本轮没有修改用户正在使用的 CDREBIRTH 工程，没有提交或对外发布。Inspector 的编译/测试、原生客户端拍摄和网页交互有对应证据；Inspector 完整视觉操作与创作质量仍需真人验收。

复核说明：仓库原有 `GameDirector.sln` 的 CRLF 差异使全量 `git diff --check` 报告空白问题；本轮没有改动该文件。候选运行组件的网页已加载最终 MP4（640×360，3 秒，视频 readyState 4，无媒体错误）。
