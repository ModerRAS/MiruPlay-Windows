# MiruPlay Windows 兼容状态

## 对照基线

- 最后审计：2026-08-04。
- Android 发布基线：`ModerRAS/MiruPlay` 的 `master@b589fd601402dff51da9411a537085babaec4e6c`，包含 Android 2.4 播放控制、ijkplayer、多版本播放、MLIP v4、字幕透明背景和音频 DSP。
- Android 字幕补充基线：`fix/subtitle-bilingual-overlap@a39dfb2e0f571cd276081ebbc6788b62847cce5e`（基础版本 2.5.0），包含尚未合入 `master` 的 Matroska zlib 字幕读取、密集普通对话 cue 去上限/去重、Media3 span 层和样式/作者定位保真，并补齐单一字幕变换流、`PlayerView` 释放、陈旧监听隔离和控制栏安全区测试；目前没有对应实机验收。
- 上述 Android 两个分支互相分叉，用于覆盖当前功能面，不代表已有一个 APK 同时包含两边的最新提交。
- Windows 基线：`master@d43dd0a871806397b26ff2ebca63a86b29813d70`（MiruPlay Windows 0.1.4）。
- 对照依据是两端当前生产接线、WebControl 契约和自动化测试。仅有核心类、计划或设计文档而没有用户入口时，不记为“对齐”。

状态含义：

- `对齐`：Windows 已提供平台等价的用户入口，并有自动化验证。
- `部分对齐`：已有生产切片，但仍缺 Android 可见能力、共享契约字段或完整用户流程。
- `待实现`：尚无生产用户入口。
- `平台差异`：Windows 使用原生等价方案，不移植 Android 专属实现；这类差异不单独算兼容缺口。

## 媒体库与元数据

| 能力 | Android 当前能力 | Windows 状态 | Windows 当前边界与证据 |
|---|---|---|---|
| MLIP v1-v4 读取 | 通过 WebDAV 只读导入 `library.db`，校验 schema/capability，支持 v4 artwork pack、v1-v3 path 兼容和 MLIP 元数据权威策略 | 对齐 | `MlipLibraryReader` 实现版本、表、capability、路径和 artwork pack 校验；测试覆盖有效 v1-v4、共享 v4 fixture、legacy path 及 pack 哈希/边界安全。Windows 同样保持只读，并额外允许从 Local/SMB 来源读取 MLIP |
| Local/WebDAV/SMB 来源 | Local/SMB 使用 DIRECTORY；WebDAV 可选 DIRECTORY/MLIP；三类来源均支持内容模式、验证、扫描、字幕和播放 | 部分对齐 | 三类来源的 WPF/WebUI CRUD、连接验证、扫描与播放已接通，且 Windows 均可选 DIRECTORY/MLIP；Local/WebDAV 有 registry 生命周期测试，SMB 目前只有路径和 DPAPI 凭据测试，缺 registry 扫描、加载与播放自动化。HTTP 和 HTTPS WebDAV 均允许，凭据只按同一规范化 authority 复用 |
| WebDAV 调度与兼容 | 单消费者队列、lease、405 circuit、认证播放；无用户名 401 时可重试 `Basic anonymous:` | 部分对齐 | `WebDavRequestDispatcher` 与 `WebDavPlaybackProxy` 已统一请求、流式 lease 和 circuit；尚未找到 Android 的 `Basic anonymous:` 兼容重试 |
| DIRECTORY 扫描与自动扫描 | 本地/WebDAV/SMB 增量扫描、取消、删除检测和统一索引；进入 Anime/Drama 媒体库时按 1/6/12/24 小时周期触发到期扫描 | 对齐 | `DirectoryLibraryIndex`、`MediaSourceRegistry`、`MediaSourceAutoScanScheduler` 已接入 WPF/WebAPI/WebUI；Windows 在客户端进程存活期间按相同周期调度，旧文档所写“自动扫描缺失”已失效 |
| 扫描后的库组织 | 同名番合并、按标题/新番季排列，并可从设置调整 | 部分对齐 | Windows 有继续观看、最近播放和类型分组；`/api/settings/scan` 仍明确拒绝同名合并，海报墙只支持 `TITLE` |
| Anime/Drama 模式 | 应用与来源分别标记 ANIME/DRAMA；新状态直接迁移为 Anime，设置决定下次启动模式；Drama 有独立首页/详情及 TMDB/TVMaze 补充 | 部分对齐 | 模式和来源类型可持久化并由 WPF/WebAPI/WebUI 切换；DIRECTORY 仍共用动漫文件名分类器，缺完整 Drama 识别、缓存与详情补充流程 |
| 多版本剧集与特典 | 导入 MLIP 多版本和 extras，支持版本选择策略、详情选择及连续播放 | 部分对齐 | `LibraryModels`、详情页和播放器队列可导入、显示并播放各版本与特典；尚无 Android 的版本选择策略设置和等价 WebUI 流程 |
| 文件名识别与 NFO | 规则与 AniFileBERT ONNX 识别，NFO 参与本地元数据流程 | 部分对齐 | DIRECTORY 与 CloudDrive 整理已共用 `OnnxAnimeFilenameParser`；`NfoDocumentService` 有边界、XML 和写入测试，但尚未接入扫描、匹配或用户入口 |
| 在线元数据搜索与匹配 | Anime 当前用户流程接入 Bangumi；AniList provider 存在但未注入。Drama 接入 TMDB+TVMaze；共享 query plan、聚类、重排和手动/批量流程 | 部分对齐 | Windows 有 Bangumi/TMDB Token、搜索客户端和只读外链；缺 TVMaze、多源聚合、手动匹配写回、批量预览/应用/撤销和 provider-neutral 缓存。未接线的 AniList 不列为当前对齐要求 |
| Bangumi 收藏与观看状态 | 收藏读写、分集状态同步，并在自然播放完成后标记已看 | 部分对齐 | WebAPI 支持身份、作品收藏和分页分集读取，`BangumiPlaybackSyncService` 可在自然完成后标记单集；完整双向状态同步和 WPF 操作入口仍不完整 |
| Bangumi Archive | 下载/导入离线 archive，并用于本地搜索和匹配 | 部分对齐 | `BangumiArchiveStore` 已实现有界下载、导入和本地搜索并有测试；尚未接入 Windows WebAPI、WebUI、WPF 和匹配流程 |

## 播放与字幕

| 能力 | Android 当前能力 | Windows 状态 | Windows 当前边界与证据 |
|---|---|---|---|
| 内嵌播放器与基础控制 | TV 遥控器优先的时间轴、播放/暂停、跳转、倍速、音轨/字幕、上一集/下一集和信息面板 | 部分对齐 | 固定版本 mpv 已通过 `--wid` 嵌入 WPF，命名管道提供相同基础控制、轨道选择、播放信息和队列；缺 Android 信息/诊断侧栏及专用媒体键交互 |
| 播放后端 | 标准 Exo、实验 GL、内嵌 mpv 和实验 ijkplayer，并按字幕/HDR 能力回退 | 平台差异 | Windows 以固定 mpv 为唯一完整后端；系统默认播放器只作明确降级。不会移植 Exo/ijkplayer，也不会把降级启动报告为完整会话 |
| HDR/SDR 与色调映射 | 按 SDR、HDR10、HDR10+、Dolby Vision、未知 HDR 保存后端与 tone-mapping 规则 | 部分对齐 | mpv 使用 `gpu-next`/D3D11，已有 SDR tone-map、HDR passthrough、曲线和目标亮度参数映射测试；当前只启用默认 `Auto`，缺按信号持久化的 WPF/WebUI 设置与共享 DTO 支持 |
| 内封/外挂字幕与语言偏好 | 轨道自动/手动选择、关闭、透明背景和分层 ASS；补充分支增加 zlib Matroska 解压，移除密集普通对话的固定 cue 上限，保留非默认/作者定位、位图、Ruby/强调/Voice 等 span 与样式差异，并统一字幕变换流、释放旧视图、隔离陈旧监听及为控制栏留出安全区 | 部分对齐 | mpv 原生处理内封/外挂、ASS/libass 与 Matroska zlib，WPF/WebControl 共用轨道状态和语言偏好；缺 `subtitleBackgroundTransparent` 设置。Media3 cue 布局与 `PlayerView` 生命周期属于 Android 专属实现，不移植 |
| 播放进度、播完动作与队列 | 断点续播、完成判定、返回详情/自动下一集及逻辑上一集/下一集 | 部分对齐 | `state.db` 提供 15 秒保存、90% 完成判定和继续观看；进度与播完设置已接入 WPF/WebControl，但逻辑上一集/下一集和自动下一集队列仅接入 WPF/mpv，WebControl 尚无等价命令，系统播放器降级也不记录进度 |
| 音频 PEQ / DSP | Android `master` 支持 PEQ、最小相位/线性相位 FIR、通道规则、limiter、保留/立体声/HRTF 输出、TV 投影和完整 WebUI | 部分对齐 | Windows 0.1.4 对齐模型语义和数值范围，提供 WPF/WebAPI/WebUI、频响预览、REW 导入、mpv `lavfi` 运行时应用及自动化测试；目前只作用于 mpv，WebUI 缺完整预设 CRUD/JSON 导入导出，且 C# 枚举的设置/API 序列化尚未对齐 Android 字符串存储值 |

## WebControl、自动化与发布

| 能力 | Android 当前能力 | Windows 状态 | Windows 当前边界与证据 |
|---|---|---|---|
| WebControl/WebUI | 默认关闭；令牌认证下覆盖媒体库、来源、扫描、播放、Cloud/RSS、元数据、日志、更新和设置 | 部分对齐 | Windows 已覆盖主要媒体与运维流程并轮换 DPAPI 令牌；当前默认开启，“扫描全部”由 WebUI 逐源调用，仍缺服务器端 `sources/scan-all`、统一出站代理、Bangumi Archive、Android HDR 设置和原生播放诊断等共享路由 |
| CloudDrive2/RSS | 官方 gRPC 登录/Token、根目录约束、RSS/Atom 预览、过滤去重、torrent staging、整理、回扫与周期任务 | 部分对齐 | Windows 已有上述进程内流程和成功门控回扫；调度依赖客户端进程存活，尚无退出后继续运行的每用户后台机制 |
| 统一出站代理 | Bangumi API、Bangumi Archive 与 RSS 共用 HTTP 代理设置 | 部分对齐 | Windows 只把代理接入 RSS/torrent 请求，尚未覆盖 Bangumi API 与 Archive 下载，也没有独立的统一代理页面/接口 |
| 本地日志与 OpenObserve | 有界本地日志、下载、平台加密 Token 和自动/手动上报 | 部分对齐 | `RotatingLocalLogStore`、`OpenObserveLogService`、CurrentUser DPAPI Token、WebAPI/WebUI 下载与手动上报已有安全测试；当前仅记录 WebControl HTTP 请求，尚未覆盖播放器、扫描和 WPF 生命周期，且没有自动上报触发器或调度器 |
| 应用更新与远程生命周期 | 检查、下载、安装授权/系统安装器，以及远程重启/退出 | 待实现 | `WindowsAppUpdater` 有有界清单和下载核心测试，但生产构造未配置 manifest，清单模型也没有资产 SHA-256 字段，WebUI 目前只能报告“不支持”；安装接口明确 unsupported，WPF 也未向 WebControl 注入重启/退出处理器 |
| 安装包与发布 | Android 生成并校验签名 APK、静态更新清单与设备安装流程 | 平台差异 | Windows 生成自包含 win-x64 便携包、每用户 Inno Setup、符号包、manifest、SHA-256 和许可清单；固定 mpv 来源并支持可选 Authenticode，详见 [distribution.md](distribution.md) |

## 明确不伪装支持

- Android 的 APK 安装授权、Media3/Exo、ijkplayer、Android Surface/字幕层和原生播放器诊断接口不会在 Windows 上返回假成功；不适用接口返回明确的 unsupported 结果。
- Windows 的系统播放器回退不具备进度、轨道、DSP、HDR 策略或 WebControl 会话能力，必须继续标记为降级模式。
- 两端不共享可执行播放器代码。可复用的一致性依据是 MLIP fixtures、WebControl DTO/存储值、行为测试和用户可观察结果；Windows UI 遵循 Fluent、键鼠与窗口行为，不复制 TV 布局。
- HTTP WebDAV 是受支持场景，不限于 loopback；UI 应继续提示 HTTP 凭据只适用于可信网络，凭据不得进入设置 JSON、日志、URL 或命令行。

## 验证入口

本次基线验证（2026-08-04）：

- 聚焦测试：自动扫描、mpv 核心、音频 DSP、运维服务和 WebControl，61/61 通过。
- 完整测试：`dotnet test MiruPlay.Windows.slnx --nologo`，207/207 通过。
- Release 构建：`dotnet build MiruPlay.Windows.slnx -c Release --nologo`，0 警告、0 错误。

后续兼容状态更新至少运行：

```powershell
dotnet test MiruPlay.Windows.slnx
dotnet build MiruPlay.Windows.slnx -c Release
```

涉及真实播放、WebDAV 或发布产物时，还需分别运行固定 mpv 集成测试、真实 Local/WebDAV/SMB smoke 和 `tools/Test-ReleaseArtifacts.ps1`；仅有单元测试不能把这些场景升级为“对齐”。
