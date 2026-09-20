# 底层契约归并：消除硬编码，闭合首装依赖的底层件

2026-09-16。本轮承接 unity-v1-candidate-r3（见 docs/13），做两件事：把散落各层的硬编码逻辑归并成单一契约，以及把"首装依赖闭合"中可在代码层闭合的部分落地。没有提交、切换分支或公开发布；r3 的全部责任与状态约束保持不变。

## 原则：一个事实只在一个地方说

审计发现同一事实被多处复述，改动任何一处都会与其他处漂移：

| 事实 | 原复述处 | 现在的唯一出处 |
|---|---|---|
| 端口 39777/39778/39800 与回环地址 | Unity 桥、CLI、MCP ×2、Workbench、网页、Inspector ×3、共 10+ 处 | `GameDirector.Core` 的 `BridgeDefaults`（网页经 `/api/health` 目录获取） |
| 桥路由 `/take/start` 等 11 条 | Unity 服务器 switch 与 .NET 客户端各自写字面量 | Core 的 `BridgeRoutes`，服务器 switch 用常量表达式绑定 |
| 能力键 `director.mode` 等 | 8 个文件的字面量 | Core 的 `CapabilityKeys` / `DirectorModes` |
| 拍摄几何边界（1–60 fps、64–3840×64–2160、偶数、≤600 s） | FilmCompiler、TakeRecorder、TakeJobs、EditRenderer、Unity DirectorRuntime 共 5 处 | Core 的 `CaptureContract.CheckGeometry` |
| 影片规模边界（32 场/120 镜头/64 音轨/300 字幕/5 MiB 声音/2 MB 脚本） | FilmCompiler、FilmDocuments、EditRenderer、MediaLibrary、网页、Inspector | Core 的 `FilmLimits`；网页与 Inspector 经 health 或 Core 常量共享 |
| 新建影片默认 24 fps / 1280×720 | FilmPlan、网页、Inspector、空资产 JSON | Core 的 `FilmDefaults` |
| 预览尺寸 640×360 | FilmDocuments 字面量 | `PreviewPolicy.Derive`：作品尺寸折半、取偶、不低于采集下限 |
| 镜头类型是否需要目标机位（dolly/crane 要 to） | TimelineCompiler、Unity Preflight、CinematicCameraRig 三处 switch/列表 | Core 的 `ShotVocabulary` 注册表；自定义类型语义归注册方（编译器不强行要求 to） |
| 景别→构图覆盖率 2.8/1.5/1.05/.8/.4 | CinematicCameraRig 内联三元链 | `ShotVocabulary.Coverage` 表，镜头可用 param `coverage` 覆盖 |
| 运动评估（dolly/crane/orbit/tracking 的位移公式） | CinematicCameraRig switch | `CinematicCameraRig.Motions` 注册表；游戏按 manifest id 注册自定义运镜，未注册类型确定性地按静态镜头处理 |
| 编码参数（libx264/fast/crf17/yuv420p、48 kHz、aac 256k、限幅 0.95、淡入淡出、缩略图 320/20/4） | TakeRecorder、EditRenderer、NormalizedAudio 内联 | `MediaProfile` 可注入记录；管线只读 profile，换编码器不改调用点 |
| 可接受音频格式与容器加固（m4a 禁外部引用） | MediaLibrary 两处白名单 + NormalizedAudio switch | `MediaFormats` 一张表：扩展名→demuxer→加固参数 |
| 字幕字体名与文件 | MediaTools 路径 + EditRenderer force_style 字面量 | `SubtitleFontDescriptor`：文件名、字体族名、固定 SHA-256 三合一，渲染与检查同源 |
| SRT 时间戳与转义 | Studio.Produce 内联（HtmlEncode+花括号替换，会把撇号编成实体） | `SubtitleRenderer`：只转义 `& < > { }`、剔除控制字符、折叠空行 |
| 引擎→端口映射 | 网页 `engineEndpoint()` 硬编码 | Workbench `EngineProfiles` 目录，health 下发，网页按目录渲染选项 |
| 请求体 8 MB 上限 | Kestrel 字面量，与 5 MiB 声音 base64 隐式耦合 | 由 `FilmLimits.MaxAudioImportBytes` 推导（4/3 + 余量） |
| 生成式舞台与示例影片的名字（lead/wide/close/side） | DirectorStageFactory 与 DirectorExample 各写一份 | `DirectorStageVocabulary`（修复了模板替换 `$W` 前缀吞掉 `$WIDE` 的顺序缺陷） |
| API 导演策略（brief 2 万字符、90 s、2 次修正） | ApiDirector 内联数字；提示词里再写一遍 120/600 | `ApiDirectorPolicy` 常量；提示词边界由 `FilmLimits` 注入 |

## 首装依赖闭合的底层件

docs/13 遗留"FFmpeg（libx264/libass）及 ffprobe 仍需配置"。本轮把代码层能闭合的部分闭合，并补齐面向陌生用户的一键自举：

- `MediaTools.Resolve` 按固定顺序解析工具：环境变量覆盖 → 随包运行组件布局 → **当前用户媒体目录（自举安装落点，包升级不丢）** → 包管理器常见安装位置（macOS Homebrew；Windows 增加 WinGet 用户 shim 目录，因为 Unity 启动的子进程看不到 Unity 启动后改的 PATH）→ PATH。工作台以最小服务环境运行时仍能发现 Homebrew 安装——本轮黑盒测试正是因此从失败转为通过。
- `MediaToolInstaller` + `GET/POST /api/media/install`：平台过滤的安装策略。包管理器策略（Windows winget `Gyan.FFmpeg`、macOS brew `ffmpeg`）把签名/哈希交给系统渠道；直接下载策略走 `PinnedDownload`——发布钉定的 SHA-256 是构件身份，每一跳必须 HTTPS（仅回环测试 fixture 允许 HTTP）、跳转有界、体积有上限、不匹配则拒绝且不留残余。安装只在用户明确点击后执行，幂等（编码契约已满足即空操作），完成后重跑完整编码检查并持久化回执。
- 准备检查的媒体项失败时带 `actionId: "install-media"`，网页渲染"安装编码组件…"按钮，对话框列出本机可用方式与许可证说明（FFmpeg GPL / libx264 GPL / libass ISC）。
- 字幕字体改为固定哈希校验：缺失或被替换会在准备检查失败，而不是出片后才发现方框。
- 新增 `GET /api/doctor`（CLI `gd doctor` 同源）：报告每个工具的解析路径与来源、编码器/滤镜/字体检查结果。准备检查的媒体项附带当前工具解析，告诉用户具体缺什么、在哪里找。
- 跨平台打包验证：同一打包工具产出 osx-arm64 与 win-x64 两个自包含候选（构建级闭合；Windows 运行时行为仍未实测）。新增 `tools/windows-preflight.ps1`（只读预检：ffmpeg/ffprobe 解析、libx264、libass 字幕滤镜、39777 urlacl 提示），供 Windows 实测前确认环境。

直接下载源表当前只有 osx-arm64 ffmpeg 一项（imageio 上游地址 + 本仓库媒体契约实际验证过的二进制哈希）。win-x64 的钉定下载项待发布工程在能访问 GitHub 的网络验证 URL 与哈希后补充；在此之前 Windows 走 winget 策略。哈希不匹配时安装器失败关闭并明确报错，不会装出来历不明的二进制。

签名、公证、Windows/Intel Mac/其它 Unity 版本的运行时验收、陌生用户首装仍不闭合，与 docs/13 一致。

## 验证证据

| 验证 | 结果 | 证据 |
|---|---|---|
| .NET 内核/契约（13 项契约 + 5 项钉死下载测试） | 58/58 通过 | 本轮 `dotnet test GameDirector.sln` 输出 |
| 作品服务黑盒 | 14 项通过 | `captures/film-document-tests/015149521ee8` |
| 工作台完整契约 + FFmpeg | 24 项通过 | `captures/workbench-contract-tests/11429dfee001` |
| 媒体回归（EOF、帧率、字幕、响度） | 通过 | `captures/edit/media-tests/15345181a603` |
| 拍摄恢复回归 | 通过 | `captures/recovery-tests/a7ed313e1d57` |
| 客户端状态 | 6 项通过 | `tools/test_document_state.mjs` 输出 |
| 安装器回归 | 5 项通过 | `tools/test_unity_installer.py` 输出 |
| r4 候选导入全新 Unity 工程 | 12/12 EditMode 测试通过、0 skipped | `captures/unity-v1-r4-validation/editmode.xml`（首次失败因新脚本缺 .meta 与误加 `-quit`，已分别修正并记录） |
| 工作台 health/doctor 实跑 | 引擎目录、默认值、限制下发正确；ffprobe 经 system install 解析 | 本轮 curl 记录 |
| 媒体自举端点实跑 | options 平台过滤正确（brew 不在 PATH 也按实际安装位置发现）；不可用方式明确拒绝；已满足时空操作 | 本轮 curl 记录 |
| 网页脚本语法 | node --check 通过 | app.js / document-state.mjs |
| win-x64 候选打包 | 自包含发布成功 | `dist/unity-v1-candidate-20260916-r4-win/` |

本轮新增契约测试固定了归并的行为语义：内建运镜类型是否需要目标机位、景别覆盖率排序、缓动表完备性、字幕转义只编码 `& < > { }`、格式表与导入上限一致、预览折半取偶有下限。有一处测试写法教训值得记录：xunit 的 `Contains/DoesNotContain` 使用区域性比较，控制字符在语言学比较中权重为零会"到处都能找到"；控制字符断言必须按码位进行。

安装候选（r5 取代同日 r4；Unity 侧 C# 与 r4 相同，12/12 EditMode 结论沿用）：`dist/unity-v1-candidate-20260916-r5.zip`（osx-arm64，SHA-256 `8fcf04fa7e8ac30fa712ca6d9745b4c236a0b745ba846dba8f3666380f1588f7`）与 `dist/unity-v1-candidate-20260916-r5-win.zip`（win-x64，SHA-256 `41b9f64f6c0c5ff067eba44e7eb2205516d4454516fb940f0d7b5f5c1dfc5721`）。

## 复核说明

一个有意放宽的行为：manifest 声明的自定义镜头类型不再被编译器强制要求 `shot.to`（内建 dolly/crane 仍然要求），因为自定义类型的运动语义属于注册它的引擎；`ShotVocabulary.RequiresTarget` 对未知类型返回 null，三层（编译、预检、机位装配）共享这一判定。`ShotIds` 注册顺序与原 GenericSceneAdapter 列表不同（crane 提前），仅影响 manifest 展示顺序。

Unity 侧验证发现了两个流程问题并已修正：新 Unity 脚本必须随包带 `.cs.meta`（PackageCache 不生成 meta，缺 meta 的文件不参与编译）；`-runTests` 不能与 `-quit` 同用（quit 会在测试运行前退出编辑器）。

仍未闭合：真实 Unity 人工 Inspector 验收、CDREBIRTH 工程回归、作品搬家、真实模型 API、签名公证与陌生用户首装，同 docs/13 清单。网页端到端浏览器人工操作路径本轮未重跑（app.js 改动限于启动目录读取与表单默认值，契约由 health 字段下发并有黑盒覆盖）。
