# MiruPlay Windows

MiruPlay 的原生 Windows 客户端，使用 .NET 10 和 WPF。目标是与 Android TV 版保持功能和视觉语义一致，同时采用正常的 Windows 键鼠、窗口和播放器集成。

## 当前可用

- 管理 Local、HTTP(S) WebDAV 和 Windows SMB/UNC MLIP v1-v4 媒体源；远程凭据由 CurrentUser DPAPI 保护
- 校验协议版本、必需表、capability 和不安全路径
- 按 Anime/Drama 模式隔离媒体源，并提供海报墙、搜索、作品详情和剧集列表
- 使用内置 libmpv 或 Windows 默认播放器播放本地视频
- 可选画质增强：缩放质量预设（低/中/高）、Anime4K SDR 动画着色器（A/A+A/B/C）与插帧方式（off/mpv 内置帧混合，非 MEMC；RIFE 真帧插帧通过可选增强包启用）；HDR 片源自动跳过 Anime4K
- 显示模式支持自动（默认，不改变现有画质）、HDR 直通、映射到 SDR；Windows 需开启系统 HDR，HDR 直通参数需实机验证
- 音频码流直通（关闭/自动/强制 + TrueHD/E-AC3/AC-3/DTS/DTS-HD）可把 Atmos 原样送入 HDMI/AVR；需要独占 WASAPI，失败自动回退 PCM，并与 PEQ/DSP/响度平衡互斥
- 内置 libmpv 已支持 Dolby Vision Profile 7 FEL 播放器侧解码与合成（Windows 输出 HDR10，不输出 DV 信号）；设置页展示运行时 FEL 支持与 libmpv/libplacebo 版本
- 可选画质增强：缩放预设、Anime4K、mpv 内置平滑插帧，以及可下载/随 full 版提供的 RIFE 真帧插帧增强包（SDR 限定，失败自动回退）
- 通过内置 libmpv 枚举内封与 MLIP 外挂字幕，按语言偏好自动选择，并可在 WPF 或 WebUI 中手动切换/关闭
- 通过内置 libmpv 读取播放位置，并以 `%LOCALAPPDATA%\MiruPlay\state.db` 作为唯一续播状态源
- 通过官方 .NET gRPC 客户端连接 CloudDrive2，验证登录或 API Token 成功后才写入 DPAPI 凭据
- 提供默认端口 `9978` 的原生 WebUI 与 WebControl API，支持媒体库/详情/播放/字幕、来源 CRUD/测试/扫描、Anime/Drama 模式、CloudDrive2/RSS、Bangumi/TMDB、播放设置和令牌轮换，并使用 DPAPI 保护 WebControl、媒体源、Bangumi、TMDB 与 CloudDrive 凭据
- 将客户端设置保存在 `%LOCALAPPDATA%\MiruPlay\settings.json`

## TV 新番时间表（AirRadar）

TV 模式的「新番」页提供「时间表 / 季番」两个模式（左右键切换，默认「时间表」）。

- **内置 Bangumi 日历**：未配置数据源时显示上周/本周/下周的 Bangumi 周历，今天高亮；条目按 Bangumi subject id 匹配媒体库，匹配到的可进入作品详情。
- **AirRadar 数据源（可选）**：配置兼容 AirRadar 的时间表 API 后，「时间表」优先显示数据源发布的整季周表（如 `2026-fall`，含 `seasonStart`/`seasonEnd`），按周一到周日分组；播出时刻以数据源的 JST 周几 + `HH:mm` 为准换算到设备本地时区，跨零点会落到正确的本地周几；`time` 为空或 `timeKnown=false` 的条目仍按数据源周几分组并标注「时间未知」；`disputed`、`confidence` 等可选字段会被解析保留。
- **媒体库匹配**：按 Bangumi subject id 匹配；未匹配的卡片显示「未在媒体库」，匹配到的按确定进入详情。
- **缓存与回退**：响应按数据源地址缓存 1 小时（`%LOCALAPPDATA%\MiruPlay\cache`），1 小时内不重复请求；请求超时（10 秒）、网络失败或 404 `no_schedule_data` 时回退到上次缓存并提示「时间表数据源暂不可用，显示缓存数据」，没有缓存则回退内置 Bangumi 日历并提示。数据源地址变化后会自动重新加载。
- **设置路径**：桌面端「设置 → 新番时间表数据源」填写地址，可用「测试连接」验证；WebUI「设置 → 回放 → 时间表数据源 URL」同样可配置（WebControl API 使用 `GET/PUT /api/settings/playback` 的 `scheduleDataSourceUrl` 字段）。TV 端「设置 → 媒体源 → 时间表数据源」只读显示「已配置 / 未配置」，修改需回到桌面端或 WebUI。
- **地址格式**：可填 base URL（如 `https://schedule.example.com`）或完整的 `/api/schedule/latest`，保存时自动去掉接口后缀和末尾 `/`；只接受 http/https，且不允许 query、fragment 或 URL 内嵌凭据。地址只保存在本机设置（`settings.json`）中，仓库不内置任何默认地址。

数据格式与发布策略见 AirRadar 仓库的 README（`schemaVersion` 1；客户端固定请求 `/api/schedule/latest`）。

## 发布版本（lite / full）

Windows 安装包分两个 edition：

- **lite（默认）**：应用 + libmpv + SDL3 + 基础 Anime4K Fast 等轻量资产，零额外依赖。
- **full**：lite + 可选增强包（portable VapourSynth + RIFE ncnn + 模型、可选大 shader）；增强包从 `runtime/enhance/rife/` 打包，不进 git，版本/哈希由 `tools/Get-EnhancementPack.ps1` 固定。增强包未交付前 CI 只发布 lite。

产物文件名包含 edition：`MiruPlay-<ver>-win-x64-<edition>-portable.zip` / `-setup.exe` / `-symbols.zip`；manifest 与自动更新清单也按 edition 区分，lite/full 不会互相升级。详见 [docs/distribution.md](docs/distribution.md)。

## 开发

需要 Windows 10/11 和 .NET 10 SDK。

```powershell
dotnet build MiruPlay.Windows.slnx
dotnet test MiruPlay.Windows.slnx
dotnet run --project src/MiruPlay.Windows
```

播放优先使用发布包内的 `runtime/libmpv/libmpv-2.dll`。如果内置运行时不可用，本地文件才会交给 Windows 默认视频应用；WebDAV 播放会明确提示内置播放器不可用。开发机可运行 `tools/Get-LibMpvRuntime.ps1` 获取固定版本的 libmpv（画质增强还需 `tools/Get-Anime4KShaders.ps1` 获取固定版本 Anime4K 着色器）；电视模式手柄的 SDL3 后端运行 `tools/Get-Sdl3Runtime.ps1` 获取固定版本的 SDL3（缺失时自动回退 XInput，发布脚本会强制校验/补齐）。`tools/Test-WebDavPlayback.mjs` 可对运行中的客户端执行临时 WebDAV MLIP 导入、鉴权播放、停止、删除和本地库恢复 smoke；`tools/Test-SmbPlayback.mjs` 使用现有 Windows 网络映射执行等价的真实 SMB smoke。

WebControl 默认监听 `9978`。在浏览器打开设置页显示的地址，输入访问令牌即可使用内嵌 WebUI；也可首次使用 `?token=<访问令牌>`，页面会立即移除 URL 参数并将令牌保存在浏览器本地。令牌通过当前 Windows 用户的 DPAPI 加密保存在 `%LOCALAPPDATA%\MiruPlay\web-control-token.bin`。

## 发布包

Windows x64 发布由同一个自包含目录生成便携 ZIP、每用户 Inno Setup 安装包、符号包、发布清单和 `SHA256SUMS.txt`。本地生成并验证：

```powershell
.\tools\Publish-Release.ps1 -Version 0.1.6
.\tools\Test-ReleaseArtifacts.ps1 -ReleaseDirectory .\artifacts\release
```

安装包默认写入 `%LOCALAPPDATA%\Programs\MiruPlay`，卸载时保留 `%LOCALAPPDATA%\MiruPlay` 内的设置、DPAPI 凭据和播放进度。签名策略、CI 发布条件和完整产物说明见 [docs/distribution.md](docs/distribution.md)。

## 项目结构

```text
src/MiruPlay.Windows/          WPF 应用、MLIP 读取和播放集成
tests/MiruPlay.Windows.Tests/ 协议与行为测试
tools/                        Windows UI、发布与产物验证脚本
installer/                    Inno Setup 每用户安装包定义
docs/                         功能兼容与发布说明
```

功能一致性进度见 [docs/compatibility.md](docs/compatibility.md)。
Playback uses the bundled in-process `runtime/libmpv/libmpv-2.dll`. If that
native runtime is unavailable, local files use the Windows system player with
degraded controls; WebDAV playback reports that the embedded player is unavailable.
