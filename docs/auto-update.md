# Windows 自动更新

## 模式（AppSettings.AutoUpdateMode）

- `off`：关闭自动检查（手动「检查更新」仍可用）。
- `check`：启动后自动检查，只提示新版本，不下载。
- `download`（默认）：自动检查 + 后台下载（有界、SHA-256 校验、staged 安装器），不自动安装。
- `install`：在 `download` 基础上，应用正常退出时弹出 10 秒倒计时确认（可取消），确认后交接安装器并重启；运行中绝不静默安装。

间隔：`AutoUpdateIntervalHours`（默认 6，归一化 1–168 小时）。旧 settings.json 没有这两个字段时自动补默认值（download / 6），非法模式回退 download。

## 调度与状态机

`AutoUpdateScheduler`（Services/AutoUpdateScheduler.cs）：

- 启动后延迟 90 秒进行首次检查，之后每 N 小时检查一次；桌面 / `--background` / TV 模式共用（App 启动时创建，MainWindow 隐藏时仍工作）。
- 状态：Idle → Checking → Available（仅 check） / Downloading → Ready（staged）；失败 → Failed（静默记录，不打断播放）。
- `CheckNowAsync()` 供手动检查，模式为 off 时也可用；自动路径遵守模式开关。
- 日志：`%LOCALAPPDATA%\MiruPlay\logs\auto-update.log`（tick/state/version）。
- 不重复下载：已有 staged 安装器时不再下载；同一时刻只允许一个检查/下载任务。

## 安装

- `download`：桌面设置「立即安装」需二次确认；TV 设置「检查更新」显示「新版本已就绪」，按 OK 提示回桌面模式安装。
- `install`：桌面关闭窗口时弹出倒计时确认对话框（默认 10 秒，可取消）；确认后先启动安装交接进程，再正常退出。headless（--background）直接执行交接。运行中不会静默安装。

## UI

- 桌面设置 →「应用更新」：自动更新模式（4 档）、当前版本、状态、上次检查时间、检查更新、立即安装。
- 电视设置 →「关于」→「检查更新」：显示检查/下载/就绪/失败状态；OK 触发检查，Ready 时提示回桌面安装；自动发现新版本时 TV 弹 toast「发现新版本 vX.Y.Z」。

## 兼容与限制

- 复用现有 `WindowsAppUpdater` 的清单校验/受限下载/SHA-256/staged 安装交接，手动 WebControl API 路径不变。
- 安装交接依赖已签名的 `MiruPlay-*-setup.exe`（与既有发布流程一致）；本任务只验证到 staged 与确认流程，未真实安装。
- 自动更新需要设置 `AppUpdateManifestUrl`（发布清单 windows-update.json）；未配置时 IsSupported=false，UI 显示不可用。
- TV toast 依赖 App 级状态回调；安装本身需回桌面（TV 不做系统级弹窗）。
