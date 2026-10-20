# MiruPlay Windows 兼容状态

## 对照基线

- 最后完整审计：2026-08-04。本节以下为 2026-10-19 对 Android 2.17.0 的增量对照，未重审所有既有条目。
- Android 对照基线：`feat/ani-online-source@5ea3c174dd446cb3ab4945f36942a1a254ea5136`（2026-10-19，2.17.0）。该分支包含通用只读 RSS 在线媒体源；2026-08 后还增加音乐/CUE 播放、五语言切换、外置音轨发现、播放性能诊断、房间测量/校准及 Topping DAC USB HID 控制。
- Android 字幕补充基线：`fix/subtitle-bilingual-overlap@a39dfb2e0f571cd276081ebbc6788b62847cce5e`（基础版本 2.5.0），包含尚未合入上述对照分支的 Matroska zlib 字幕读取、密集普通对话 cue 去上限/去重、Media3 span 层和样式/作者定位保真，并补齐单一字幕变换流、`PlayerView` 释放、陈旧监听隔离和控制栏安全区测试；目前没有对应实机验收。
- Windows 基线：`master@dde9a01da05252fe0403e083d433002e8b30c82a`（2026-08-05）；本次对照包含当前未提交的工作区改动。
- Android 字幕补充基线是独立分支，不代表 2.17.0 对照版本已包含这些补丁。

状态含义：

- `对齐`：Windows 已提供平台等价的用户入口，并有自动化验证。
- `部分对齐`：已有生产切片，但仍缺 Android 可见能力、共享契约字段或完整用户流程。
- `待实现`：尚无生产用户入口。
- `平台差异`：Windows 使用原生等价方案，不移植 Android 专属实现；这类差异不单独算兼容缺口。

## 媒体库与元数据

| 能力 | Android 当前能力 | Windows 状态 | Windows 当前边界与证据 |
|---|---|---|---|
| MLIP v1-v4 读取 | 通过 WebDAV 只读导入 `library.db`，校验 schema/capability，支持 v4 artwork pack、v1-v3 path 兼容和 MLIP 元数据权威策略 | 对齐 | `MlipLibraryReader` 实现版本、表、capability、路径和 artwork pack 校验；测试覆盖有效 v1-v4、共享 v4 fixture、legacy path 及 pack 哈希/边界安全。Windows 同样保持只读，并额外允许从 Local/SMB 来源读取 MLIP |
| Local/WebDAV/SMB 来源 | Local/SMB 使用 DIRECTORY；WebDAV 可选 DIRECTORY/MLIP；三类来源均支持内容模式、验证、扫描、字幕和播放 | 对齐 | 三类来源均有 WPF/WebUI CRUD、连接验证、扫描和播放；WebDAV 支持 HTTP/HTTPS，凭据只按同一规范化 authority 复用。Local/WebDAV registry 有自动化覆盖；SMB 已通过现有盘符分别实测 DIRECTORY 与 MLIP 扫描、加载、libmpv 播放，数据库未改写、原库恢复。复现见 `tools/Test-SmbPlayback.mjs` 和 `artifacts/verification/smb-directory-playback.log`、`smb-mlip-playback.log` |
| WebDAV 调度与兼容 | 单消费者队列、lease、405 circuit、认证播放；无用户名 401 时可重试 `Basic anonymous:` | 对齐 | `WebDavRequestDispatcher` 与 `WebDavPlaybackProxy` 统一请求、流式 lease 和 circuit；无凭据的 GET/HEAD/PROPFIND 在 401 后只重试一次 `Basic anonymous:`，保留 Range/Depth，显式凭据失败时不替换身份；有对应回归测试 |
| DIRECTORY 扫描与自动扫描 | 本地/WebDAV/SMB 增量扫描、取消、删除检测和统一索引；进入 Anime/Drama 媒体库时按 1/6/12/24 小时周期触发到期扫描 | 对齐 | `DirectoryLibraryIndex`、`MediaSourceRegistry`、`MediaSourceAutoScanScheduler` 已接入 WPF/WebAPI/WebUI；Windows 在客户端进程存活期间按相同周期调度，旧文档所写“自动扫描缺失”已失效 |
| 扫描后的库组织 | 同名番合并、按标题/新番季排列，并可从设置调整 | 对齐 | `LibraryOrganization` 合并同名作品并保留 episode/version/progress/source identities 和别名 API IDs；支持 TITLE 与依据 AirDate 的 RELEASE_SEASON 分组。WPF、`/api/settings/scan` 和 WebUI 设置共用状态；focused tests、浏览器保存/重载及 WPF UI Automation 已验证。仅合法月分组，不用文件时间推断新番季 |
| Anime/Drama 模式 | 应用与来源分别标记 ANIME/DRAMA；新状态直接迁移为 Anime，设置决定下次启动模式；Drama 有独立首页/详情及 TMDB/TVMaze 补充 | 部分对齐 | 应用模式与来源内容类型可持久化并由 WPF/WebAPI/WebUI 切换；Drama 来源已按 `contentMode=DRAMA` 建库并显示详情。共享元数据聚合服务已按模式构造有界 query plan，跨 Bangumi/TMDB/TVMaze 聚类、重排、返回证据并缓存成功结果；TVMaze 搜索完成真实外网验证。剩余差距是 provider-ref detail lookup、上游本地候选缓存及完整 Drama 季集/艺术图补充；TMDB 真实凭据流程未验收，不要求另加文件名电视剧分类器。 |
| 多版本剧集与特典 | 导入 MLIP 多版本和 extras，支持版本选择策略、详情选择及连续播放 | 对齐 | WPF/WebUI 设置支持自动相近路径与 manual；WPF 手动下一集及自然播放结束显示原生选择器，WebControl 返回下一集版本并由 WebUI 选择后提交不透明 episode ID。WPF 取消不前进、自然结束选定 720p、WebUI 选定 1080p 均以真实临时媒体验证；extras 和版本队列有自动化测试 |
| 文件名识别与 NFO | 规则与 AniFileBERT ONNX 识别，NFO 参与本地元数据流程 | 部分对齐 | DIRECTORY 继续共用 `OnnxAnimeFilenameParser`；安全有界 `NfoDocumentService` 已接入 Local/SMB/WebDAV 扫描和自有索引，覆盖 series/episode metadata 与增量更新测试。Local 真实 NFO、SMB 映射网络共享，以及本地隔离 WebDAV 协议服务均实际扫描 NFO，并验证 override 在重扫后保留、undo 恢复 NFO；WebDAV 验证使用本地协议 fixture，不代表用户外部 WebDAV 服务器认证已验收。 |
| 在线元数据搜索与匹配 | Anime 当前用户流程接入 Bangumi；AniList provider 存在但未注入。Drama 接入 TMDB+TVMaze；共享 query plan、聚类、重排和手动/批量流程 | 部分对齐 | 新增 provider-neutral `/api/metadata/search`：Anime 使用 Bangumi，Drama 使用 TMDB+TVMaze；依据模式、标题/原名/别名、路径样本、年份和季号构造最多 10 条查询，按 provider ID 去重，只跨 provider 合并相容候选，保留同源不同作品和季号冲突，按标题/年份/upstream rank/完整度/多源收敛重排并返回证据与 recommendation，成功结果缓存 10 分钟。WebUI/WPF 提供全部来源搜索，仍需用户确认候选后才预览/应用。Local/SMB/WebDAV DIRECTORY 使用自有 SQLite override；WPF/WebUI 支持批量预览、应用与历史撤销，刷新后重载列表；MLIP/RSS 只读。WPF/WebUI 均可导入/搜索 Archive 并回填。仍缺 provider-ref detail lookup、上游本地候选缓存、完整 Drama 季集/艺术图补充；TMDB 真实凭据及候选匹配质量仍需真实数据验收；AniList 未接线不列为当前要求 |
| Bangumi 收藏与观看状态 | 收藏读写、分集状态同步，并在自然播放完成后标记已看 | 部分对齐 | WebAPI、WebUI 和 WPF 元数据编辑器均支持作品收藏及分集状态读取/写入；WPF 按 episode ID 显示已收藏分集，并允许手动输入未标记 episode ID，状态值符合官方 schema。`BangumiPlaybackSyncService` 在自然完成后标记已看。本机无 Bangumi token，外部账号读写未实测 |
| Bangumi Archive | 下载/导入离线 archive，并用于本地搜索和匹配 | 对齐 | `BangumiArchiveStore` 提供有界下载、JSONL/ZIP 导入和本地搜索；WPF/WebUI 均有导入、搜索、候选回填入口。WebUI 真实上传 236 字节样例、WPF 真实导入 231 字节样例并搜索回填均通过；multipart 和大小边界有 HTTP 集成测试。测试文件已精确清理；未下载完整上游档案做端到端验证 |

## 播放与字幕

| 能力 | Android 当前能力 | Windows 状态 | Windows 当前边界与证据 |
|---|---|---|---|
| 内嵌播放器与基础控制 | TV 遥控器优先的时间轴、播放/暂停、跳转、倍速、音轨/字幕、上一集/下一集和信息面板 | 部分对齐 | 进程内 libmpv 通过 HWND 嵌入 WPF；WPF/WebControl/WebUI 共享播放、暂停、寻道、倍速、音量、静音和版本队列。实机验证 RSS 播放、外置音轨、双击全屏及来源删除；缺 Android 性能诊断侧栏和完整专用媒体键交互 |
| 播放后端 | 标准 Exo、实验 GL、内嵌 mpv 和实验 ijkplayer，并按字幕/HDR 能力回退 | 平台差异 | Windows 以固定 mpv 为唯一完整后端；系统默认播放器只作明确降级。不会移植 Exo/ijkplayer，也不会把降级启动报告为完整会话 |
| HDR/SDR 与色调映射 | 按 SDR、HDR10、HDR10+、Dolby Vision、未知 HDR 保存后端与 tone-mapping 规则 | 部分对齐 | mpv 使用 `gpu-next`/D3D11，已有 SDR tone-map、HDR passthrough、曲线和目标亮度参数映射测试；当前只启用默认 `Auto`，缺按信号持久化的 WPF/WebUI 设置与共享 DTO 支持 |
| 内封/外挂字幕与语言偏好 | 轨道自动/手动选择、关闭、透明背景和分层 ASS；补充分支增加 zlib Matroska 解压，移除密集普通对话的固定 cue 上限，保留非默认/作者定位、位图、Ruby/强调/Voice 等 span 与样式差异，并统一字幕变换流、释放旧视图、隔离陈旧监听及为控制栏留出安全区 | 部分对齐 | mpv 原生处理内封/外挂、ASS/libass 与 Matroska zlib，WPF/WebControl 共用轨道状态和语言偏好；缺 `subtitleBackgroundTransparent` 设置。Media3 cue 布局与 `PlayerView` 生命周期属于 Android 专属实现，不移植 |
| 播放进度、播完动作与队列 | 断点续播、完成判定、返回详情/自动下一集及逻辑上一集/下一集 | 对齐 | `state.db` 提供 15 秒保存、90% 完成判定和继续观看；WPF/WebControl/WebUI 共享逻辑上一集/下一集，WPF 会话在自然完成后按设置自动下一集。实机验证前后集和只读状态轮询；系统播放器降级不具备这些会话能力 |
| 音频 PEQ / DSP | Android `master` 支持 PEQ、最小相位/线性相位 FIR、通道规则、limiter、保留/立体声/HRTF 输出、TV 投影和完整 WebUI | 部分对齐 | Windows 0.1.4 对齐模型语义和数值范围，提供 WPF/WebAPI/WebUI、频响预览、REW 导入、mpv `lavfi` 运行时应用及自动化测试；目前只作用于 mpv，WebUI 缺完整预设 CRUD/JSON 导入导出，且 C# 枚举的设置/API 序列化尚未对齐 Android 字符串存储值 |

## WebControl、自动化与发布

| 能力 | Android 当前能力 | Windows 状态 | Windows 当前边界与证据 |
|---|---|---|---|
| WebControl/WebUI | 默认关闭；令牌认证下覆盖媒体库、来源、扫描、播放、Cloud/RSS、元数据、日志、更新和设置 | 部分对齐 | Windows 已覆盖主要媒体与运维流程并轮换 DPAPI 令牌；提供串行全源扫描、持久任务查询/取消、可配置更新清单、更新安装确认和远程重启/退出。后台可用 `--background` 隐藏窗口；`tools/Register-BackgroundTask.ps1` 可选注册当前用户登录启动，不会自动修改任务计划。Bangumi Archive 支持上传/导入/搜索和匹配回填。仍缺 Android HDR 设置和原生播放诊断等共享路由 |
| CloudDrive2/RSS | 官方 gRPC 登录/Token、根目录约束、RSS/Atom 预览、过滤去重、torrent staging、整理、回扫与周期任务 | 部分对齐 | Windows 保留成功门控回扫和原有 RSS 专用代理配置；周期调度可在桌面或 `--background` 进程内运行，登录启动需用户显式运行 `tools/Register-BackgroundTask.ps1`，退出后由 Windows Task Scheduler 下次登录重新启动 |
| 统一出站代理 | Bangumi API、Bangumi Archive 与 RSS 共用 HTTP 代理设置 | 部分对齐 | 复用现有 CloudDrive/RSS HTTP 代理设置，动态接入 Bangumi、TMDB、TVMaze、Archive 与 RSS 在线目录/订阅请求；保留 CloudDrive RSS 提交的按请求配置，不触碰 WebDAV dispatcher 身份边界。代理仅支持无凭据 HTTP 主机和端口，通过 WebUI CloudDrive 设置调整 |
| 本地日志与 OpenObserve | 有界本地日志、下载、平台加密 Token 和自动/手动上报 | 部分对齐 | `RotatingLocalLogStore` 有界轮转并脱敏；记录 WebControl 请求耗时/状态、播放器生命周期、扫描结果与启动事件。CurrentUser DPAPI Token 支持手动及 opt-in 每小时后台上报；上报失败留在本地并写入持久任务状态，不影响播放/扫描。外部 OpenObserve 账号未验收 |
| 应用更新与远程生命周期 | 检查、下载、安装授权/系统安装器，以及远程重启/退出 | 部分对齐 | `tools/Publish-Release.ps1` 生成与 updater schema 一致的 `windows-update.json`，`Test-ReleaseArtifacts.ps1` 校验其 HTTPS 资产 URL/长度/SHA-256。WebUI 可配置无凭据清单；下载有界并强制 hash，安装交接前重验。用户确认后，Windows 隐藏 PowerShell 交接进程等待应用安全退出、静默运行 `*-setup.exe` 并重启当前客户端；WebControl 远程重启/退出已在 Release `--background` 进程上实际验证，重启保持隐藏，退出可正常关闭。安装器脚本只做校验/构造测试，未实际运行安装器或发布；Windows 安装权限 API仍不适用 |
| 安装包与发布 | Android 生成并校验签名 APK、静态更新清单与设备安装流程 | 平台差异 | Windows 生成自包含 win-x64 便携包、每用户 Inno Setup、符号包、manifest、SHA-256 和许可清单；固定 mpv 来源并支持可选 Authenticode，详见 [distribution.md](distribution.md) |

## Android 2.17.0 增量对照

| Android 更新 | Windows 当前状态 | 差距 |
|---|---|---|
| 通用只读 RSS 在线媒体源：虚拟剧集目录、直链播放、快照缓存 | `RSS` 来源已接入 registry、WPF/WebUI CRUD、测试、手动刷新、十分钟缓存和独立进度键；与 RSS 下载提交并存 | 对齐；默认 Feed 实测 53 集/52 部作品，HTTP 直链在 WPF/libmpv 中实际播放 |
| 音乐模式：CUE/整轨播放及 SRC bypass | CUE 解析/虚拟曲目起止、目录音乐扫描（内存缓存）、libmpv start/end、SRC 输出策略（system/software/direct，direct 独占 WASAPI 且 DSP 启用时回退软件）、WebControl albums/play API 并复用统一播放入口 | 部分对齐；WPF 音乐库视图、整轨队列连播未做，无真实整轨录音验收 |
| 五语言本地化与应用内语言切换 | AppLanguage 设置（system + zh-CN/zh-TW/zh-HK/en-US/en-GB）持久化/归一化、WebControl locale API、WPF 设置 combo 与 WebUI 选择器、启动与切换时应用 culture | 部分对齐；翻译字符串表未做，切换目前只影响数字/日期格式 |
| 外置音轨发现与轨道选择 | 本地/DIRECTORY/WebDAV 扫描落自有索引、WebDAV 代理 scoped URL、播放时本地同级发现与 WebDAV 远端 depth-1 发现（fail-closed、15 秒超时），显式列表关闭 fuzzy | 部分对齐；SMB MLIP 播放时远端发现未做（UNC 跳过），远程音轨选择以认证 fixture 验证，非外部服务器账号验收 |
| 播放性能监测 | libmpv warn 日志 80 条有界脱敏队列、白名单属性按需采集、`/api/playback/diagnostics`、WebUI 诊断按钮、WPF 状态 tooltip | 部分对齐；无持续帧率/缓冲曲线视图，按需读取不落盘 |
| 麦克风扫频、房间响应测量与自动校准 | Farina sweep/正则反卷积/漂移搜索/双扫 tail gate/REW 平滑与目标曲线/cut-only 自动拟合/有效测量预设工厂已移植，合成数据测试覆盖 | 部分对齐；采集/播放/UMIK 下载/校准向导 UI 未做，无硬件与声学实测 |
| Topping DAC USB HID 音量、增益、电源与 PEQ 推送 | 硬件确认协议移植（CRC/帧/11 寄存器/Q25）、原生 HID 传输（产品字符串匹配、仅确认型号可写）、写缓存控制器（确认门限/pace/commit）、WebControl 全套路由、WebUI 与 WPF DSP 对话框推送入口 | 部分对齐；无 DAC 硬件实测，未连接路径已测，不向未知 VID 相同设备写命令 |


- Android 的 APK 安装授权、Media3/Exo、ijkplayer、Android Surface/字幕层和原生播放器诊断接口不会在 Windows 上返回假成功；不适用接口返回明确的 unsupported 结果。
- Windows 的系统播放器回退不具备进度、轨道、DSP、HDR 策略或 WebControl 会话能力，必须继续标记为降级模式。
- 两端不共享可执行播放器代码。可复用的一致性依据是 MLIP fixtures、WebControl DTO/存储值、行为测试和用户可观察结果；Windows UI 遵循 Fluent、键鼠与窗口行为，不复制 TV 布局。
- HTTP WebDAV 是受支持场景，不限于 loopback；UI 应继续提示 HTTP 凭据只适用于可信网络，凭据不得进入设置 JSON、日志、URL 或命令行。

## 验证入口

本次运维增量验证（2026-10-19）：

- 必需真实 libmpv/DSP 环境下完整测试 **297/297 通过**，见 `artifacts/verification/operations-full-tests.log`；Release 构建 **0 警告、0 错误**，见 `artifacts/verification/operations-release-build.log`。包括持久任务取消后读回终态、更新 SHA-256 暂存/tamper 拒绝、API 安装显式确认、动态 HTTP 代理切换的本地 TCP 协议 fixture。
- 新 Release 实际以 `--background` 启动，确认主窗口隐藏、WebControl capabilities 可用；API 远程 restart 后仍隐藏，远程 exit 正常关闭。随后恢复正常可见客户端。媒体状态保持 IDLE、来源 1、344 部/7681 集，`settings.json` 最终 SHA-256 与基线相同。进程启动执行了真实来源 1 扫描并刷新唯一的 `media-source-scan-1` 持久状态为 SUCCEEDED；没有创建测试任务/来源或修改播放进度。未注册 Windows Task Scheduler、未启动真实安装器。
- WebUI 运维视图用隔离 Playwright API mock 检查 1280px 与 390px 布局：安装确认按钮、运行任务取消按钮及四个区块可见，`scrollWidth` 与 viewport 相同。截图：`output/playwright/windows-operations-1280.png`、`windows-operations-390.png`。此为 mock UI 验证，不是外部更新源或账号验收。

本次目录/元数据及版本策略验证（2026-10-19）：

- 必需真实 libmpv/DSP 环境下完整测试 **286/286 通过，0 跳过，45 秒**；结果见 `artifacts/verification/catalog-metadata-batch-full-tests.log`。随后 Release 构建成功，0 警告、0 错误；见 `artifacts/verification/catalog-metadata-batch-release-build.log`。`node --check src/MiruPlay.Windows/Web/app.js` 和 `git diff --check` 通过。WPF 批量编辑实机证据记录于 `artifacts/verification/catalog-metadata-ui-smoke.md`。
- SMB DIRECTORY 与 MLIP 均实际扫描、加载并由内嵌 libmpv 播放，结果见 `artifacts/verification/smb-directory-playback.log` 和 `smb-mlip-playback.log`。另外，SMB mapped share `Z:` 的 NFO 来源扫描、字段/TVMaze ID 导入、override→重扫保留、undo→恢复 NFO 已实测；WebDAV 的同一 NFO 工作流通过 Windows HTTP/WebDAV 客户端对本地隔离 Node 协议 fixture 实测，不声称外部服务器认证验收。NFO/索引/WebDAV focused tests 为 23/23；真实 Local `tvshow.nfo` 元数据页面、手动预览/应用/撤销均完成。
- WPF 批量编辑器实际多选两部作品、预览/应用，并从 APPLIED 历史撤销；另一个 DIRECTORY smoke 验证 apply 后标题立即刷新、undo 后 NFO 标题立即恢复。真实单作品和 WebUI 预览/应用/撤销也通过。
- 新增 `/api/metadata/search` provider-neutral 聚合流程后，focused tests 为 15/15；包括 query planning、按 Anime/Drama 路由、跨 provider 聚类/排序/缓存、provider failure isolation，以及 Drama API 聚合契约。Anime 仅查询 Bangumi，Drama 查询 TMDB 与 TVMaze。WPF 真实编辑器显示四个来源选项；WebUI 使用 mock 回填 Bangumi ID，未调用真实聚合 provider，也未应用该次变更。
- 真实临时媒体验证 manual next：WebUI 提供 1080p/720p 两个选项并播放所选 1080p；WPF 原生 picker 取消不前进；自然播放结束后 WPF picker 选 720p 并实际启动该文件。远程选择 API 和设置有 `WebControlServerTests` 覆盖。
- 清理后 settings 与原始备份 SHA-256 均为 `922F397D989BAA16B2173D38825BA4913DAC1B4CEFB016A386ED14D87C3936C6`；仅来源 1，最新实际载入 **344 部作品、7681 集**，本轮较早载入时为 7679 集；无测试标题，播放 `IDLE`。Archive store 及 smoke 文件、临时媒体目录均为空/已删除；本轮没有播放媒体，未新增进度键。整体兼容任务仍未完成。

本次 Windows 播放与 RSS 验证（2026-10-19）：

- 基线实际日志位于 `artifacts/verification/baseline-tests.log`：251/251 通过。监控通知中另一条日志路径不存在、缓冲区为 0 行，不代表测试失败。
- 完整测试：设置必需 libmpv 与 DSP 环境变量后 **257/257 通过，0 跳过，41 秒**；真实播放、暂停/寻道/速度、进度、EOF、停止、远程鉴权、DSP 及外置音轨集成测试实际运行。结果见 `artifacts/verification/full-tests-final.log`。
- Release 构建：`dotnet build MiruPlay.Windows.slnx -c Release --nologo --verbosity quiet`，结果见 `artifacts/verification/release-current.log`。
- 默认 RSS Feed 在 WebUI 完成测试、保存、编辑和扫描：53 集/52 部作品；真实 WPF/libmpv 播放 1080p H.264/AAC，进度推进 16.4 秒，暂停和倍速命令成功。截图：`artifacts/verification/windows-rss-playing.png`。
- 真实鼠标双击完成 1200×780 → 1920×1080 → 1200×780。`STATIC` 视频宿主使用 `SS_NOTIFY` 以接收输入；WPF 回归测试检查样式和双击动作。
- 真实 HTTP WebDAV 显式凭据和匿名 401 重试均通过进程内 libmpv 播放，进度分别推进 1625/1708 ms（总长 18000 ms）；匿名场景触发 3 次 401，Range 均为 `bytes=0-`。来源缓存及临时鉴权文件已清理。可通过 `node tools/Test-WebDavPlayback.mjs` 和附加 `--anonymous` 复现；结果见 `artifacts/verification/webdav-credential-playback.log`、`webdav-anonymous-playback.log`。
- WebUI 实际操作验证音量、静音、1.25× 倍速及前后集切换；连续状态轮询保持当前队列位置。1280px/390px 浏览器验证无横向溢出，播放栏实际高度与预留空间一致，长标题省略显示。截图为 `output/playwright/rss-player-1280.png`、`rss-player-390.png`。
- 删除正在播放的 RSS 来源后回到 IDLE，原媒体库恢复；测试来源、临时媒体和精确定位的测试进度均已清理。原设置已逐字节恢复，重启后只保留来源 1，当前读取 343 部作品且没有测试标题；见 `artifacts/verification/restored-user-state.log`。未提交、推送或发布。

后续兼容状态更新至少运行：

```powershell
dotnet test MiruPlay.Windows.slnx
dotnet build MiruPlay.Windows.slnx -c Release
```

本次 Android 增量验证（2026-10-20）：

- 必需真实 libmpv/DSP 环境下完整测试 **351/351 通过，0 跳过**；结果见 `artifacts/verification/android-additions-full-tests.log`。Release 构建 **0 警告、0 错误**；见 `artifacts/verification/android-additions-release-build.log`。`node --check Web/app.js` 与 `git diff --check` 通过。
- 为构建新 Release，确认旧客户端 IDLE 后经主窗口 WM_CLOSE 优雅关闭（不强杀），构建完成后重新拉起可见客户端并核对：IDLE、来源 1（LOCAL）、344 部/7681 集→333 部/7689 集（单真实来源、零测试标题，计数变化来自真实库本身）、`settings.json` 18→25 键且全部为新增默认键（`AudioSrcBypass`、`AudioOutputSampleRate`、`AppLanguage` 等）、原值零改动。新客户端 `/api/topping/status` 报告未连接（无硬件）、`/api/settings/locale` 报告 `system`。
- 本轮 7 个阶段已逐个提交并推送到 `origin/master`（`dde9a01..294f231` 起始 5 个阶段，外加远端 sidecar、Topping 核心/API/UI、音乐/SRC、校准 DSP、语言设置）。仍未做：WPF 音乐库视图与队列连播、翻译字符串表、测量采集/播放/UMIK/向导 UI、SMB MLIP 播放时远端发现；未执行真实安装、发布、Task Scheduler 注册。

涉及真实播放、WebDAV 或发布产物时，还需分别运行固定 mpv 集成测试、真实 Local/WebDAV/SMB smoke 和 `tools/Test-ReleaseArtifacts.ps1`；仅有单元测试不能把这些场景升级为“对齐”。
# In-process libmpv playback

Windows playback uses the pinned `runtime/libmpv/libmpv-2.dll` backend, which
embeds libmpv in the MiruPlay process and uses the existing WPF video window
handle. If the native library is absent, incompatible, or fails during
initialization, local files use the Windows system player with degraded
controls; WebDAV reports that the embedded playback backend is unavailable.
