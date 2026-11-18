# HDR、杜比全景声码流直通与 Dolby Vision 说明

本文说明 MiruPlay Windows 的显示模式、音频码流直通和 Dolby Vision（DV）支持边界。所有设置都保存在 `%LOCALAPPDATA%\MiruPlay\settings.json`，并可在桌面设置页、WebUI 与电视模式设置页修改。

## 1. 显示模式（HDR）

| 模式 | 行为 | 适用场景 |
|---|---|---|
| 自动（默认） | 不额外传递任何 mpv 色彩选项，保持现有行为（libmpv 默认：`target-colorspace-hint=auto`、`target-colorspace-hint-mode=target`） | 大多数用户；Windows HDR 开或关都能得到合理结果 |
| HDR 直通 | `target-colorspace-hint=yes` + `target-colorspace-hint-mode=source` + `target-peak=10000` + `tone-mapping=clip` + `hdr-compute-peak=no`：把源内容元数据交给 D3D11/DXGI 显示链路，不强制 target-prim/target-trc，也不在播放器内二次 tone map | Windows 系统 HDR 已开启、显示器/链路能处理 HDR10 的环境 |
| 映射到 SDR | 使用 bt.1886 + 色调映射曲线 + 目标 SDR 亮度，把 HDR 内容映射到 SDR 输出 | SDR 显示器，或不想开启 Windows HDR |

**重要限制与验证状态**

- Windows 必须开启“设置 → 系统 → 显示 → HDR”（或游戏/视频 HDR 开关），D3D11 swapchain 才可能切换 HDR 色彩空间；未开启时“HDR 直通”实际仍是 SDR 映射。
- “HDR 直通”组合中的 `target-colorspace-hint-mode=source` / `target-peak=10000` 等参数来自社区实践（Chiphell/Post76 等二手资料），**当前仅验证了参数可被 pinned libmpv 接受**；尚未在真实 HDR 显示链路上做画质验收，文案不承诺“已验证”。
- 默认“自动”模式不改变现有画质；HDR 直通只对显式选择该模式的用户生效。
- Windows 无法输出 Dolby Vision 信号；DV 内容由播放器侧合成后以 **HDR10 PQ** 输出（见 §3）。

## 2. 音频码流直通（Atmos / TrueHD / E-AC3）

Atmos 在消费级片源里是 TrueHD（UHD BD）或 E-AC-3 JOC（流媒体）中的对象/高度元数据。FFmpeg 只能把 bed 解成 PCM 声道，**PCM 解码会丢失 Atmos 高度信息**；要让功放解码 Atmos，必须把压缩码流原样直通（bitstream passthrough）。

设置项：

- **模式**：关闭（默认）/ 自动 / 强制。
- **编解码集合**：TrueHD、E-AC3、AC-3、DTS、DTS-HD（默认全选；实际只会对匹配的源音频生效）。
- **HDMI/功放设备**：Auto（系统默认输出）或指定的 WASAPI 端点（需要设备名称包含 `wasapi/`）。

直通时发送给 libmpv 的选项：

    --ao=wasapi
    --audio-exclusive=yes
    --audio-spdif=truehd,eac3,ac3,dts,dts-hd
    # 指定设备时追加：--audio-device=wasapi/<endpoint>

**硬件前提**

- Windows 默认输出设备必须是 HDMI/DisplayPort 接到支持对应码流的 AVR/电视（或支持 IEC61937 的 S/PDIF 链路）。
- Windows 声音设置中该设备应允许独占模式。
- 功放/电视必须支持 Atmos 解码；否则功放可能只当普通 TrueHD/PCM 处理。

**失败回退**

设备或功放不支持该码流时，mpv 会自动回退到 PCM 解码（日志：`Falling back to PCM output.`），播放继续但 **Atmos 高度信息丢失**。MiruPlay 通过 `audio-out-params/format` 判定实际输出：`spdif-*` 表示码流直通生效，`s32/floatp/...` 表示已回退 PCM。

**与 PEQ / DSP / 响度平衡互斥**

码流是压缩帧，mpv 的音频滤镜链不能处理 spdif 帧。开启直通时 MiruPlay 会跳过 DSP/PEQ/响度平衡（设置页会置灰并提示）；修改在下次播放生效。直通时 mpv 软件音量对压缩码流无效，请用功放音量。

## 3. Dolby Vision（含 Profile 7 FEL）

当前固定运行时（`runtime/libmpv/libmpv-2.dll`，shinchiro mpv 20261021-git-e471496c56）的实测能力：

- 版本：mpv **v0.41.0-1227-ge471496c5**；libplacebo **v7.377.0**（PL_API_VER 377）；FFmpeg **N-127893-g9a861e968**（含 `dovi_split` 位流过滤器）。
- 上游 DV Profile 7 FEL 支持已合入 mpv master（PR #17932，2026-07-01；mpv v0.42.0 的 release notes 将其列为 gpu-next 能力）。
- pinned 源码包含 `demux/dovi_split.c`、`filters/f_enhancement_pair.c`、`vf=format=enhancement-layer`、`vo_gpu_next` 的 EL 映射，且 libplacebo 的 FEL NLQ 合成 API（API 366/367/369）在 377 中可用；设置页“播放能力”会显示当前运行时的探测结果（FEL 支持：是/否 + libmpv/libplacebo 版本）。
- DV P5/P8（单层）与 P7 FEL（双层增强层）都由 gpu-next/libplacebo 在播放器内合成；**Windows 不能向显示器发送 DV 信号**，最终输出为 HDR10 PQ。
- 增强层解码/合成失败时 mpv 会回退基础层并打印 warn；MiruPlay 会将诊断状态标记为“增强层已回退（BL）”，在播放诊断接口中可查。
- 运行时要求：FEL 需要 libplacebo 具备 enhancement-layer/NLQ API（本仓库以 PL_API_VER ≥ 370 作为保守判定）与带 `dovi_split` 的 FFmpeg。更换 libmpv 构建后，设置页能力探测会自动变为“不支持”，不会假装支持。
- 测试片源：FEL 效果需用户自备双层 DV 片源（例如社区 BL_EL_Dolby Vision FEL Test Samples，约 85 秒处画面差异可判据）；**本仓库不分发任何受版权保护的测试片源**。

## 4. ASIO / Omniphony（P2，不在当前版本）

- 上游 mpv 没有 ASIO 音频输出，也没有对象音频（Atmos/DTS:X）实时渲染能力；后者需要 [mpv-omniphony](https://github.com/mgth/mpv-omniphony) fork（`ad_orender` + `ao_asio`）+ liborender + harletty-bridge + ASIO 驱动。
- 该路线的 Windows FEL 版 libmpv SDK 未发布（FEL 轨只发播放器 zip），若需要“FEL + ASIO”同一运行时需自行构建；omniphony 组件为 GPL-3.0-or-later（与 MiruPlay 的 GPL-3.0-or-later 兼容），harletty-bridge 为 Apache-2.0 但上游明确不随包分发，需用户自行提供。
- 当前版本只做官方运行时的 WASAPI 码流直通；ASIO/空间音频作为可选 sidecar/自建构建是后续独立事项。

## 5. 验证状态

- 已自动化验证：选项映射（HDR 三模式、直通编解码集合、互斥/回退判定）、设置归一化与持久化、旧 settings.json 兼容、WebControl API 校验、电视设置页选项、i18n 五语言。
- 已在本机（无 AVR/无 HDR 电视/无 DV 片源）验证：WASAPI 直通失败时 mpv 自动回退 PCM 的路径、pinned libmpv 的版本/能力探测。
- 未实测：真实 HDMI→AVR 的 Atmos/TrueHD/E-AC3 直通、真实 HDR 显示链路下的三种模式画质、DV P7 FEL 片源的实际合成效果与性能、ASIO/omniphony 链路。这些需要在具备相应硬件的环境做 P1 验证。
