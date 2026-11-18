# MiruPlay Windows 电视模式（TV Mode）

> 目标：在大屏 + 遥控器场景下，用 10-foot UI 完成「继续观看 / 浏览库 / 选集播放 / 调字幕音轨倍速 / 返回 / 搜索 / 设置 / 退出」的完整闭环。
> 依据：`artifacts/miruplay-tv-mode/design.md`（本仓库外的工作区交付物）。布局照 Plex TV，功能与数据流照 Android TV 版。

## 入口与退出

| 入口 | 说明 |
|---|---|
| `MiruPlay.exe --tv` | 直接以电视模式启动（桌面窗口保持隐藏，退出电视模式后显示） |
| `MiruPlay.exe --tv --tv-demo` | 同上，并在底部中央显示当前按键的大号角标（演示/验键用） |
| 设置 → 电视模式 → 进入电视模式 | 桌面模式运行时切到电视模式，桌面窗口隐藏 |
| 设置 → 「启动时进入电视模式」 | 写入 `AppSettings.TvModeEnabled`，下次启动进入电视模式 |

退出：长按 Back（默认 2 秒，可在设置里调 1000–4000ms）→ 进度条走完后回到桌面模式；或在左侧导航选中「退出」按 OK。
单按 Back = 逐级返回（关抽屉 → 隐藏 OSD → 退出播放 → 返回上一屏）。

## 代码结构（`src/MiruPlay.Windows/Tv/`）

| 文件 | 职责 |
|---|---|
| `TvShellWindow.xaml(.cs)` | 无边框全屏壳：左侧导航（收起 72px / 焦点进入展开 200px）、屏幕路由、按键分发、长按退出、Toast、底部提示条、演示角标 |
| `TvInput.cs` | `TvInputAction` 归一化、`TvKeyMap` 预设（通用键盘 / MCE RC6 / Fire TV + 并集兜底）、`TvInputService`（350ms/90ms 连发、Back 长按 2s、KeyUp 清理） |
| `TvNavigator.cs` | 屏幕栈（Home 根，Back=pop）、焦点记忆（每屏一个 zone/index 字符串） |
| `TvFocus.cs` | 显式焦点模型：TvZone（水平/垂直/网格）+ TvScreenBase（方向移动、边界回调、焦点恢复），不依赖 WPF 空间导航，行为可单测 |
| `TvAppContext.cs` | 只读复用已配置 MLIP 来源（`MediaSourceRegistry.LoadCatalog`）、进度合并、首页行数据、搜索过滤；不新增/扫描/删除来源 |
| `TvTheme.xaml` + `TvCards.cs` | 10-foot 样式与海报/文本/剧集卡片（焦点环 + 1.06–1.08 缩放动画） |
| `Screens`（TvHomeScreen / TvLibraryScreen / TvDetailScreen / TvSearchScreen / TvSettingsScreen） | 首页（Hero + Continue/Recently added/Categories/All rows）、6×3 海报墙（边缘整页翻）、全幅 Backdrop 详情、屏幕键盘搜索、分组设置 |
| `TvPlayerScreen.cs` + `TvOsdWindow.xaml(.cs)` + `TvMpvHost.cs` | 播放页：libmpv 仍用 `--wid` 嵌入 `TvMpvHost` 子窗口；chrome 全部画在分层 OSD 窗口；OSD 状态机、抽屉、Back 分级、自动下一集 |
| `TvOsdModels.cs` | OSD 状态快照（时间码/缓冲/transport/chips/drawer/focus/status/error） |

## 播放页重建：分层 OSD 窗口

原桌面播放页把 chrome 塞进 `Popup`，因为 mpv 是原生 `HwndHost` 子窗口，WPF 无法直接叠加（airspace）。电视模式的实现：

1. **视频层不变**：`MpvPlayerLauncher` → `LibMpvPlaybackSession`，`--wid=<TvMpvHost.NativeHandle>`，`osc=no`、`input-default-bindings=no`、`input-vo-keyboard=no`。
2. **OSD 层**：`TvOsdWindow` 为 `AllowsTransparency + WindowStyle=None + Topmost + ShowActivated=false` 的无边框透明窗口，在 `SourceInitialized` 追加 `WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT | WS_EX_LAYERED`，因此**不抢焦点、鼠标穿透**，只负责绘制。
3. **几何同步**：`SyncGeometry(host)` 读电视壳窗口的 `GetWindowRect`，按 DPI 换算成 DIP 后设置 OSD 窗口的 Left/Top/Width/Height，并用 `SetWindowPos(HWND_TOPMOST, SWP_NOACTIVATE)` 保证置顶；壳窗口 Resize/DPI 变化时重同步。
4. **输入仍在壳窗口**：遥控器按键由 `TvShellWindow.PreviewKeyDown` / `PreviewKeyUp` 接收；若焦点意外落到 mpv 子窗口，`TvMpvHost.WndProc` 会把 WM_KEYDOWN/WM_KEYUP 转给 `TvInputService`，保证全屏播放时按键不丢。
5. **OSD 状态机**：任意键呼出，3 秒无操作自动隐藏（暂停/抽屉打开/焦点在 OSD 内时不隐藏）；Back 逐级：关抽屉 → 隐藏 OSD → 退出播放；Menu 打开抽屉（字幕/音轨/倍速/信息）；方向键进入焦点模式后左右选按钮、上下换行；播放结束按设置自动下一集或返回详情。
6. **从「从头播放」语义**：`LibMpvPlaybackSession.StartAsync(..., forceStartPosition: true)` 显式写 `start=0`，避免 `startPositionMs=0` 被当成「未指定」而续播断点；「继续播放」显式传进度。桌面播放页默认参数不变，零回归。

## 焦点

进入屏幕后不按额外键即显示焦点环：屏幕挂树时立即设置逻辑焦点，`TvScreenBase.ApplyFocus` 在 `Focus()` 失败时按 Loaded/Input 优先级重试，Shell 在屏幕布局完成后再补一次 `RefocusActive()`；子屏 Back 返回走同一套 `RestoreFocus` 路径。

## 手柄输入

电视模式支持 XInput（Xbox 系）与 SDL3（DualSense/DualShock/Switch Pro/8BitDo 等 HID 手柄）两种后端，SDL3 优先、XInput 兜底；映射/设置/支持矩阵见 [tv-gamepad.md](tv-gamepad.md)。`--tv-pad-demo` 可在无实体手柄时演示。

## 输入映射（默认并集，可在设置切换预设）

| 动作 | 通用键盘 | MCE RC6 | 其他 |
|---|---|---|---|
| 方向 / OK | ↑↓←→ / Enter、Space | 方向 / OK | Fire TV 方向环 |
| Back | Esc / Backspace | BrowserBack / Esc | 蓝牙遥控返回 |
| Home / Menu | Home / M | BrowserHome / M | — |
| 播放暂停 | MediaPlayPause / P / K | MediaPlayPause | — |
| 快退/快进 | Shift+←/→、J/L | —（播放页内 ←→ 调 OSD） | — |
| 上一/下一集 | MediaPrevious/Next | 上一曲/下一曲 | — |
| 音量 | VolumeUp/Down/Mute | 同左 | 默认交给系统/功放 |

## 已知限制（本版如实记录）

1. **启动加载耗时**：电视模式直接复用用户已配置的 MLIP 来源（本机为 `S:\动漫`，CloudDrive2 的 CloudFS 云盘）。实测读取 `library.db` + 抽取海报 artwork pack 到本地缓存约 **19.3 秒**（339 部 / 8183 集）。原因在 CloudFS 上读取 MLIP 数据库与 artwork pack，而不是扫描或元数据请求（电视模式**从不触发扫描**）。后续可做本地 catalog 快照 + 后台刷新（本次未做）。
2. **手机遥控页 /remote**：未实现（P1 未完成），搜索中文仍依赖实体键盘或后续手机输入；屏幕键盘只覆盖 ASCII。
3. **收藏**：详情页「收藏」按钮目前只切换本机显示状态，未写 Bangumi 收藏（需要令牌与在线的 Bangumi 写接口）。
4. **焦点音效**：设置项保留，但本版未播放音效（P2）。
5. **遥控器诊断页**：未实现独立页面；`--tv-demo` 角标 + 设置里的按键诊断提示用于验键（P2）。
6. **多显示器/4K**：当前固定主屏全屏（`SystemParameters.PrimaryScreenWidth/Height`），未做显示器选择与 4K 缩放档位。
7. **音乐库**：电视模式未映射音乐（Android 的音乐页为二期）。
8. **桌面播放页**：保持原有 Popup chrome 不变（零回归），电视播放页是独立实现；`PlaybackSessionController` 抽取只落在电视侧（TvPlayerScreen），未反哺桌面页。

## 验证

- 单元/冒烟测试：`tests/MiruPlay.Windows.Tests/Tv*Tests.cs`（TvNavigator / TvKeyMap / TvInputService / TvZone / TvPresentation / TvSettingsStore / TvShellSmoke），以及 `LibMpvPlaybackSessionTests` 中「从头播放 vs 续播」三条用例。
- 手工/证据：`artifacts/miruplay-tv-mode/demo/` 截图 + `tv-mode-demo.mp4`（72.5s，真实 MLIP 视频 + 大号按键角标）。
- 全量：`dotnet test`、`dotnet build -c Release`、`node tools/i18n/build.js --check`。