# RIFE 真插帧增强包

本文说明 MiruPlay Windows 的 RIFE（Real-Time Intermediate Flow Estimation）真帧插帧增强包：可选、可由应用内安装器下载或随 full 版内置、失败自动回退。用户文档见设置页；本文面向维护与发布。

## 1. 范围与开关

- 开关是已有的 `FrameInterpolationMode` 字符串，值域 `off | mpv | rife`：`mpv` 是内置帧混合平滑，`rife` 才调用本增强包做真帧插帧。
- RIFE 只在进程内 libmpv 播放路径生效；Windows 默认播放器降级路径没有画质增强。
- 设置下一次播放生效；播放中不改 filter 链。
- 只支持 SDR：HDR 直通、HDR 内容（PQ/HLG/BT.2020）都会跳过 RIFE 并在状态里给出原因；与 PEQ/DSP 无直接耦合，但同机 CPU/GPU 负载需要留余量。
- `Animation4K`/缩放预设可同时开启（SDR 时），它们走不同的 mpv 选项/着色器，不互斥。

### 设置子项（`AppSettings.RifeEnhancement`）

| 字段 | 默认 | 取值 | 说明 |
|---|---|---|---|
| Factor | 2 | 2 / 3 / 4 | 帧率倍数；输出帧率 = 输入帧率 × Factor，超过显示器刷新率时会掉帧 |
| Model | 4.6 | 4.6 / 4.25-lite | RIFE ncnn 模型（4.6 = 插件 model 23，4.25-lite = 70） |
| GpuThread | 2 | 1–4 | ncnn Vulkan 线程数（实测 >2 无收益） |
| ProcessingHeight | auto | auto / 1080 / 720 / 576 / 540 / 480 / 360 | RIFE 前把源缩到该高度；auto 用自检推荐值（780M 推荐 480） |

## 2. 运行时组成

增强包根目录两种来源，内容格式完全相同：

1. **随 full 版内置**：`<install>/runtime/enhance`（tvmode 的双版本发布把该目录打进 full 包，lite 包不含）；
2. **下载安装**：`%LOCALAPPDATA%/MiruPlay/enhance/rife`（设置页“下载并安装”；服务优先使用已下载版本）。

```text
runtime/enhance/rife/     或   %LOCALAPPDATA%/MiruPlay/enhance/rife/
  manifest.json                清单：version + files[]（+ Publish 用的 sourceArchive）
  VSScript.dll, VSPipe.exe     VapourSynth R73 + Python 3.14（Python DLL/zip、Lib/site-packages、vs-coreplugins 等）
  vs-plugins/
    librife_windows_x86-64.dll RIFE ncnn Vulkan 插件（MIT，r9_mod_v33）
    models/rife-v4.6_ensembleFalse/
    models/rife-v4.25-lite_ensembleFalse/
  selftest/selftest_480p.vpy    VSPipe 自检脚本（#11 full 版发布校验也使用）
  licenses/                     VS LGPL-2.1、Python PSF-2.0、librife MIT、ncnn BSD-3、RIFE MIT
```

下载器使用仓库内固定清单 `tools/enhance/rife-enhance-manifest.json`（嵌入 app）：包含 GitHub Release asset URL（`enhance-rife-2026.11` / `rife-2026.11.7z`）、归档 SHA-256/大小、逐文件 SHA-256、模型/许可证元数据；7z 归档由 Windows 自带 tar/bsdtar 解包并逐文件校验。当前示例版本 `2026.11`（asset `rife-2026.11.7z`，7z 归档）。发布流水线按 `tools/enhance/README.md` 重建/上传该 asset；tvmode 的 `tools/Get-EnhancementPack.ps1` 用它生成 `runtime/enhance/manifest.json` 并校验文件。

## 3. 安装、自检与回退

- 安装：SHA-256 + 大小校验 → zip 解包到 staging（拒绝绝对路径/..）→ 逐文件 SHA-256 校验（拒绝清单外文件）→ 原子替换（失败回滚）→ 磁盘余量检查（解包大小 ×1.2 + 128 MB）。
- 自检：子进程运行 `VSPipe.exe -p -c y4m selftest-480p.vpy NUL`（合成 480p 24fps 24 帧，RIFE 2×）。解析 `Output 48 frames in X seconds (Y fps)` 与 GPU 名；要求 Y ≥ 30 fps（保守；780M 实测 67–72）。推荐处理高度：取满足 `Y × (480/h)² × 0.9 ≥ 48×1.1` 的最大高度。结果缓存在 `%LOCALAPPDATA%/MiruPlay/enhance/rife-state.json`，按包版本 + 自检结果失效。
- 播放计划：`ResolvePlaybackRequest(settings, pipeline)` 决定是否启用；不可用时返回原因（未安装 / 未自检 / HDR 直通 / 路径不支持……）。可用时生成 `state/active-<hash>.vpy`：
  - `core.std.LoadPlugin(path="<rife dll>")`；
  - 读 `video_in` 的 `_Matrix` 选择 601/709/2020 矩阵；
  - 按 ProcessingHeight 预缩放 → RGBS → `core.rife.RIFE(model, factor_num, gpu_thread, sc=True)` → 回源格式；
  - libmpv 初始化后执行 `vf add @rife:vapoursynth=file=<vpy>`，并把 `VSSCRIPT_PATH` 指向包内 `VSScript.dll`。
- 运行期回退：监控 mpv 日志（`could not init VS`、`Script evaluation failed`、`Disabling filter vapoursynth` 等）与运行状态（frame-drop-count 增量 ≥8/2s 或 |avsync|>0.2s，或检测到 HDR 内容）→ 执行 `vf remove @rife` 并按需设置 mpv 内置插帧选项，状态 `RifeState=failed` + 原因，播放不中断。
- 路径限制：RIFE 插件/模型加载不支持非 ASCII 路径；若用户目录不是纯 ASCII，服务会把 `rife/` 镜像到 ASCII 缓存（`%LOCALAPPDATA%/MiruPlay/enhance/rife-ascii/<version>`，不可用时依次尝试 ProgramData/Public/TEMP）并在 vpy 中加载镜像路径。

## 4. 状态与 API

`MpvQualityEnhancementStatus` 增加 `RifeState`、`RifeDegradedReasonKey`、`RifeFactor`、`RifeProcessingHeight`；播放状态接口 `/api/playback/status` 的 `quality` 字段会带出。设置接口：

- `GET/PUT /api/settings/playback`：`frameInterpolationMode` 增加 `rife`；新增 `rifeEnhancement{factor,model,gpuThread,processingHeight}` 与只读 `rifeEnhancementStatus`、各选项列表；
- `GET /api/enhance/rife/status`：安装/自检/可用性/原因；
- `POST /api/enhance/rife/install`：下载并安装（阻塞直到完成，前端按钮加载态）；
- `POST /api/enhance/rife/selftest`：重跑自检并返回结果；
- `POST /api/enhance/rife/uninstall`：卸载已下载版本（内置 full 包不受影响）。

## 5. 性能与建议（实测 AMD Radeon 780M / Ryzen 7 7840HS / 60Hz）

| RIFE 处理分辨率 | 原始吞吐（输出 fps） | 24→48 需求 | 结论 |
|---|---|---|---|
| 480p | 77.7 | 48 | 实时，推荐 |
| 576p | ~60（mpv 端到端 0 掉帧） | 48 | 实时，接近上限 |
| 720p | 34.0 | 48 | 不实时（掉帧/失步） |
| 1080p | 13.4 | 48 | 不实时 |

- 1080p 源建议 `ProcessingHeight=auto`（该机会选 480）或手动 480/576，由 mpv gpu-next 放大，端到端 0 掉帧。
- 24fps×3/×4 在 60Hz 显示器上会被显示刷新率限制，建议只用于高刷/低帧率源。
- 首帧会多 1–2s 初始化（Python/模型/Vulkan），属于预期。

## 6. 验证状态（2026-11-18，运行时 v0.42.0-38-gbcde6b93c）

- 单元/接口测试：设置归一化、清单结构、vpy 生成、服务安装/校验/回滚/卸载、缺包与 HDR 降级、Web 设置 PUT/GET、i18n 五语言。
- 实机：真实增强包归档安装 → VSPipe 自检（480p 67–72 fps）→ libmpv 播放 480p RIFE 2×（估计 48fps、0 掉帧）→ 卸载；坏归档/缺文件拒绝；HDR 直通/内容回退普通播放。证据见交付目录 `artifacts/miruplay-video-fx/rife-spike/evidence-enhance/`。
- 未实测：NVIDIA/Intel 独显、vs-mlrt DirectML 后端、真实 HDR 显示链路、4K/高刷性能、真实片源主观画质。

## 7. 许可证

增强包包含 VapourSynth（LGPL-2.1，动态链接，独立 DLL）、Python（PSF-2.0）、RIFE ncnn 插件（MIT）、ncnn（BSD-3-Clause）、RIFE 模型（MIT）。完整文本随包 `licenses/` 分发，并在仓库 `licenses/` 与 `THIRD-PARTY-NOTICES.md` 登记。
