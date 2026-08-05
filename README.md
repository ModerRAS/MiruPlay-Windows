# MiruPlay Windows

MiruPlay 的原生 Windows 客户端，使用 .NET 10 和 WPF。目标是与 Android TV 版保持功能和视觉语义一致，同时采用正常的 Windows 键鼠、窗口和播放器集成。

## 当前可用

- 管理 Local、HTTP(S) WebDAV 和 Windows SMB/UNC MLIP v1-v4 媒体源；远程凭据由 CurrentUser DPAPI 保护
- 校验协议版本、必需表、capability 和不安全路径
- 按 Anime/Drama 模式隔离媒体源，并提供海报墙、搜索、作品详情和剧集列表
- 使用内置 libmpv 或 Windows 默认播放器播放本地视频
- 通过内置 libmpv 枚举内封与 MLIP 外挂字幕，按语言偏好自动选择，并可在 WPF 或 WebUI 中手动切换/关闭
- 通过内置 libmpv 读取播放位置，并以 `%LOCALAPPDATA%\MiruPlay\state.db` 作为唯一续播状态源
- 通过官方 .NET gRPC 客户端连接 CloudDrive2，验证登录或 API Token 成功后才写入 DPAPI 凭据
- 提供默认端口 `9978` 的原生 WebUI 与 WebControl API，支持媒体库/详情/播放/字幕、来源 CRUD/测试/扫描、Anime/Drama 模式、CloudDrive2/RSS、Bangumi/TMDB、播放设置和令牌轮换，并使用 DPAPI 保护 WebControl、媒体源、Bangumi、TMDB 与 CloudDrive 凭据
- 将客户端设置保存在 `%LOCALAPPDATA%\MiruPlay\settings.json`

## 开发

需要 Windows 10/11 和 .NET 10 SDK。

```powershell
dotnet build MiruPlay.Windows.slnx
dotnet test MiruPlay.Windows.slnx
dotnet run --project src/MiruPlay.Windows
```

播放优先使用发布包内的 `runtime/libmpv/libmpv-2.dll`。如果内置运行时不可用，本地文件才会交给 Windows 默认视频应用；WebDAV 播放会明确提示内置播放器不可用。开发机可运行 `tools/Get-LibMpvRuntime.ps1` 获取固定版本的 libmpv。`tools/Test-WebDavPlayback.mjs` 可对运行中的客户端执行临时 WebDAV MLIP 导入、鉴权播放、停止、删除和本地库恢复 smoke；`tools/Test-SmbPlayback.mjs` 使用现有 Windows 网络映射执行等价的真实 SMB smoke。

WebControl 默认监听 `9978`。在浏览器打开设置页显示的地址，输入访问令牌即可使用内嵌 WebUI；也可首次使用 `?token=<访问令牌>`，页面会立即移除 URL 参数并将令牌保存在浏览器本地。令牌通过当前 Windows 用户的 DPAPI 加密保存在 `%LOCALAPPDATA%\MiruPlay\web-control-token.bin`。

## 发布包

Windows x64 发布由同一个自包含目录生成便携 ZIP、每用户 Inno Setup 安装包、符号包、发布清单和 `SHA256SUMS.txt`。本地生成并验证：

```powershell
.\tools\Publish-Release.ps1 -Version 0.1.0
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
