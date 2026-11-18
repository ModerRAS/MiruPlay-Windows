# 画质增强（缩放 / Anime4K / 内置插帧）

本文说明 MiruPlay Windows 的可选画质增强能力、限制和验证状态。设置保存在
%LOCALAPPDATA%/MiruPlay/settings.json，并可在桌面设置页、WebUI 和电视模式设置页修改。

## 1. 范围

| 能力 | 实现 | 默认 |
|---|---|---|
| 缩放质量预设 | off / 低 / 中 / 高 -> mpv scale/cscale/dscale/correct-downscaling/linear-downscaling/sigmoid-upscaling | off（参数与今天完全相同） |
| Anime4K 预设 | off / A / A+A / B / C -> 官方 v4.0.1 Fast 链（9 个 GLSL 文件，MIT） | off |
| 插帧方式 | `off` / `mpv`（内置帧混合：video-sync=display-resample + interpolation=yes + tscale=oversample + hwdec-extra-frames=8）；`rife` 为后续增强包预留，v1 不接受 | off |

## 2. 行为与互斥

- 只在进程内 libmpv 播放时生效；Windows 默认播放器降级路径没有画质增强。
- 设置下一次播放生效，不做运行中热切换。
- Anime4K 只适合 SDR 动画。运行时读取 video-params/gamma、video-params/primaries、
  video-params/sig-peak：HDR（PQ/HLG/BT.2020/信号峰值 >1）或选择 HDR 直通时自动跳过
  Anime4K（缩放/插帧仍生效），原因写入播放状态与诊断。
- 着色器在 file-loaded 后按内容应用，不在 mpv_initialize 前加载。
- 资产缺失、选项被运行时拒绝、shader 编译失败都不中断播放：清空 user shader 并回退到内置缩放。
- 插帧方式为可扩展字符串存储值：v1 支持 `off`、`mpv`；`rife` 已预留为后续增强包的取值，v1 设置/API 会将其安全回退为 off。
- `mpv` 模式是 mpv 的时间轴重采样/帧混合（smoothmotion 类），不是 MEMC、不是 RIFE 真插帧；
  主要收益是降低 24p 在 60Hz 上的 judder，代价是轻微运动模糊。它需要 display-* 同步与 vsync；
  VFR 内容、VRR/G-Sync 显示器可能使其自动失效（mpv 自动回退，不报错）。
- 高刷/VRR 建议：使用插帧时优先固定刷新率；否则可能看到重复帧或插帧不生效。

## 3. Anime4K 预设（官方 Fast 链）

| 预设 | 着色器链 | 适用 |
|---|---|---|
| A | Clamp_Highlights -> Restore_CNN_M -> Upscale_CNN_x2_M -> AutoDownscalePre_x2 -> AutoDownscalePre_x4 -> Upscale_CNN_x2_S | 1080p 动画 |
| A+A | A 链 + 在两次放大之间加入 Restore_CNN_S | 放大 >=2x、追求更强感知 |
| B | Clamp_Highlights -> Restore_CNN_Soft_M -> Upscale_CNN_x2_M -> AutoDownscalePre_x2 -> AutoDownscalePre_x4 -> Upscale_CNN_x2_S | 720p 动画 |
| C | Clamp_Highlights -> Upscale_Denoise_CNN_x2_M -> AutoDownscalePre_x2 -> AutoDownscalePre_x4 -> Upscale_CNN_x2_S | 480p 动画 |

来源：Anime4K v4.0.1 官方 Low-end 模板（md/Template/GLSL_Windows_Low-end）。
文件清单与 SHA-256 见 runtime/shaders/README.md 和 tools/Get-Anime4KShaders.ps1。

## 4. 资产与发布

- tools/Get-Anime4KShaders.ps1：固定 v4.0.1，校验下载归档 SHA-256（139cd282...d7ed7）
  与每个文件的 SHA-256，输出到 runtime/shaders/anime4k/；该目录在 Git 中只保留 README。
- MiruPlay.Windows.csproj 在文件存在时条件复制到输出/发布目录；Publish-Release.ps1
  会补齐/校验资产，release-manifest.json 记录 shaders.anime4k 的版本、归档哈希和逐文件哈希；
  Test-ReleaseArtifacts.ps1 校验便携包和安装包都包含着色器与 MIT 许可。
- 发布包缺资产时功能自动降级为 off 并在设置页/诊断提示，不会半挂。

## 5. 验证状态（2026-11-18，运行时 v0.42.0-38-gbcde6b93c / libplacebo v7.391.0）

- 已自动化验证（690 个测试全绿）：选项映射（off 输出与旧版逐字节一致、低/中/高精确参数、插帧参数）、
  Anime4K 预设文件顺序、HDR/直通/资产缺失降级规则、设置归一化与旧 JSON 兼容、
  WebControl GET/PUT 与非法值 400、插帧方式 off/mpv 与预留 rife 的安全回退、
  i18n 五语言（1590 键 x 5）、发布产物校验。
- 只读 libmpv 探针（固定版 v0.42.0-38-gbcde6b93c，gpu-next + D3D11 + wid）：Anime4K Fast A 链全部
  pass 编译并逐帧执行；ravu compute shader pass 可执行；插帧 display-sync-active=yes；
  缺失 shader 不影响播放；WS_CHILD 子窗口下 display-sync-active=yes 已实测。
- 真实动画片源 A/B（S: 库内 1080p HEVC-10bit，窗口 2560x1440，同帧 seek 95s）：
  Anime4K A 链 vs baseline 的 8-bit 平均绝对差 3.64、最大 150，约 83% 像素变化、35 万像素差异 >10；
  裁剪对比图为可见的线条锐化/去噪差异（证据：artifacts/miruplay-video-fx/evidence/real-content/）。
- WPF 实机 smoke（隔离用户数据、真实 libmpv 会话、新运行时）：SDR 播放中 `quality.shaderState=applied`、
  `glsl-shaders` 指向打包资产、`display-sync-active=yes`、`frame-drop-count=0`；
  HDR10（pq/bt.2020/sig-peak 4.93）+ HDR 直通时 `quality.shaderState=skipped_hdr`、
  `degradedReasonKey=quality.reason.hdr`、未加载任何 shader（证据：evidence/gui-smoke/）。
- `Publish-Release.ps1 -SkipInstaller` + `Test-ReleaseArtifacts.ps1 -SkipInstaller` 通过：便携包含 9 个
  shader + Anime4K-MIT 许可，release-manifest.json 记录逐文件 SHA-256，便携包可启动。
- 未实测：4K/高刷性能、真实 HDR 显示链路上的画质观感、真人内容上的 Anime4K（官方也不建议）。
  需要用户提供片源与 GPU/显示器信息后做 P1 验收。

## 6. 已知限制

- 仅 SDR 动画使用 Anime4K；不做 HDR 着色器链。
- 插帧不是运动补偿，不产生真实中间帧；无法替代 SVP/RIFE。
- 不提供自定义 shader 导入、不下载运行时模型；VapourSynth/RIFE 不随包分发。
- 默认全部关闭，避免任何画质/性能回归。
