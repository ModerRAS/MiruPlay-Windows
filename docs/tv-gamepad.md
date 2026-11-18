# 电视模式手柄输入（XInput + SDL3）

> 电视模式除键盘/遥控器外的第二输入源。遥控器动作仍统一走 `TvInputAction`，手柄只是新增设备源。

## 架构

```
XInput (xinput1_4 / xinput9_1_0)  ─┐
                                  ├─> TvGamepadState ─> TvGamepadService ─> TvInputAction ─> TvShellWindow.DispatchAction
SDL3 Gamepad API (runtime/sdl3)  ─┘                         │
                                                            ├─ 350ms/90ms 连发
                                                            ├─ B 长按 2s = 退出确认（与键盘 Back 一致）
                                                            └─ 与键盘共用 TvActionCoalescer（60ms 跨设备去重）
```

- `TvXInput.cs`：`IXInputSource`、`TvGamepadState`、`XInputSource`（P/Invoke 动态加载 xinput1_4.dll，回退 xinput9_1_0.dll）、`FakeXInputSource`。
- `TvSdlGamepad.cs`：`SdlGamepadSource`（动态加载 SDL3.dll，SDL_Init(SDL_INIT_GAMEPAD)，SDL 内置 mapping 数据库）与 `AutoGamepadSource`（SDL3 优先；无 SDL3/无手柄时用 XInput；同一时刻只让一个后端报告已连接，避免双触发）。
- `TvGamepadService.cs`：边沿检测、方向/摇杆死区、连发、LT/RT seek、B 长按、热插拔复位、`Enabled` 总开关。完全注入时钟，可单测。
- `TvActionCoalescer.cs`：键盘与手柄同一动作 60ms 内去重（同时按 ← 不会走两步）。
- 生命周期：电视壳 Loaded/Activated 后启动 16ms 轮询（约 60Hz），Deactivated/退出停止；失焦即重置不残留按住状态。SDL3/XInput 句柄在电视壳 Dispose 时释放（SDL_Quit 按引用计数）。
- `--tv-pad-demo`：仅演示用脚本化虚拟手柄（`TvGamepadDemoSource`），驱动真实 UI，配合 `--tv-demo` 底部大号角标显示「🎮 A/←/B」；生产路径不依赖该开关。

## 映射

| 手柄输入 | TvInputAction | 说明 |
|---|---|---|
| D-pad / 左摇杆 | Up/Down/Left/Right | 死区默认 0.5（设置 0.2–0.8）；左摇杆取主方向轴；350ms/90ms 连发 |
| A（Xbox）/ Cross（PS）/ SDL SOUTH | Select | Nintendo 布局下与 B 交换 |
| B（Xbox）/ Circle（PS）/ SDL EAST | Back | 单击=返回；长按 2s=退出确认；Nintendo 布局下与 A 交换 |
| Back/View（XInput）/ SDL BACK | Back | 直接返回 |
| X（Xbox）/ Square（PS）/ SDL WEST | Menu | |
| Y（Xbox）/ Triangle（PS）/ SDL NORTH | Info | |
| Start | PlayPause | |
| LB / RB | PrevEpisode / NextEpisode | |
| LT / RT（阈值 0.6） | SeekBack / SeekForward | 按住重复（350ms 后每 200ms） |
| 右摇杆水平（>0.5） | SeekBack / SeekForward | 可选 seek |
| Guide 键 | 忽略 | 防误触 |

Nintendo 脸键布局：SDL 按物理位置映射（下方=SOUTH），Switch Pro 的物理 A 在右、B 在下；设置 `TvGamepadFaceLayout=nintendo` 时交换 Select/Back，使确认键仍是物理 A。默认 `xbox`（Xbox/PS/大多数 8BitDo XInput 模式）。

## 设置

- `TvGamepadEnabled`（默认开；无手柄零副作用）
- `TvGamepadDeadzonePercent`（20–80，默认 50）
- `TvGamepadFaceLayout`（`xbox`/`nintendo`）
- 电视设置页「电视与遥控器」分组新增：手柄输入、摇杆死区、手柄脸键布局、输入后端（SDL3 → XInput）及说明。左右键调整、OK 切换。

## 支持矩阵

| 手柄 | 后端 | 状态 |
|---|---|---|
| Xbox 系列（有线/无线适配器/蓝牙 XInput 模式） | XInput 或 SDL3 | ✅ |
| DualSense / DualShock 4（USB + 蓝牙） | SDL3 | ✅（SDL3.dll 存在时；Windows 上原生 HID） |
| Switch Pro / Joy-Con | SDL3 | ✅（需设置 Nintendo 脸键布局） |
| 8BitDo（XInput/Switch 模式） | SDL3 或 XInput | ✅（Switch 模式用 Nintendo 布局） |
| 其他 XInput 兼容手柄 | XInput | ✅ |
| DirectInput 老手柄 | 不支持 | ❌ 后续可评估 DInput/SDL joystick 适配 |

- SDL3.dll 来自 `runtime/sdl3`（由 `tools/Get-Sdl3Runtime.ps1` 下载并校验 SHA-256，git 忽略二进制；缺失时自动回退 XInput，不影响电视模式）。
- 无实体手柄时可用 `--tv-pad-demo` 演示/验收。
- SDL3 后端没有实体手柄的现场验证；插入手柄后每 1s 内自动枚举/打开（热插拔），无需重启。

## 验证与证据

- 单测：`TvGamepadServiceTests`（映射、死区边界、连发、B 长按、触发器、右摇杆、断连/热插拔、禁用）、`TvActionCoalescerTests`、`SdlGamepadBackendTests`（SDL3 缺失/无设备不崩、非法索引、脸键布局归一化）、`TvLaunchArgumentsTests`（--tv-pad-demo）。
- 演示：`artifacts/miruplay-tv-mode/demo/pad-01-home.png` … `pad-06-settings-gamepad.png` + `tv-gamepad-demo.mp4`（30s，脚本化虚拟手柄驱动真实 UI，含按键角标）。
