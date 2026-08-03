# Windows Audio DSP Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 将 Android 端的 PEQ、REW 校准导入、逐声道线性相位 FIR、限幅、降混、mpv 播放集成、WPF 设置和 Web API/WebUI 能力同步到 Windows 客户端。

**Architecture:** 配置模型和 REW 解析器保持平台无关；DSP 数学层按声道生成响应、FIR 参数和预览数据；mpv 编译器把这些计划转换成 FFmpeg `lavfi` 滤镜图。只有 MiruPlay 自己启动的 mpv 走 DSP，系统播放器回退不改变。

**Tech Stack:** .NET 8、WPF、System.Text.Json、现有 mpv JSON IPC、FFmpeg `firequalizer`/音频滤镜、xUnit；不新增运行时 UI 或音频依赖。

## Global Constraints

- DSP 默认关闭，已有设置文件缺少 DSP 字段时加载关闭的中性配置。
- REW 文件按分段制表符文本解析，识别 `Generic` 和 `Compound_filters`，不依赖固定列号。
- 左右、中置、LFE、环绕等具体声道拥有独立 PEQ；线性相位时每个声道独立设计响应，但所有声道共用 tap 数和群延迟。
- DSP 只注入 MiruPlay 自己启动的 `mpv.exe`；Windows 系统播放器回退不接收 DSP。
- 开启 DSP 时强制 float PCM、关闭独占输出和编码直通；关闭 DSP 时不添加 DSP 参数。
- 配置/API 校验失败时不持久化、不替换正在工作的 mpv 滤镜。
- 凭据不进入 DSP 配置；滤镜命令只由校验后的数值生成，预设名称必须转义。
- 保留工作区已有未提交改动，不使用 reset、checkout 或全量格式化。
- 每个新行为先写一个会失败的测试，运行确认失败后再写生产代码。

---

### Task 1: 配置模型与 REW 导入解析器

**Files:**
- Create: `src/MiruPlay.Windows/Models/AudioDspModels.cs`
- Create: `src/MiruPlay.Windows/Services/RewEqFileParser.cs`
- Create: `tests/MiruPlay.Windows.Tests/AudioDspModelsTests.cs`
- Create: `tests/MiruPlay.Windows.Tests/RewEqFileParserTests.cs`

**Interfaces:**
- Produces `AudioDspConfig`, `AudioDspPreset`, `AudioDspChannelRule`, `AudioDspBand`, `AudioDspLimiter` and enum storage values for subsequent tasks.
- Produces `RewEqFileParser.Parse(string)` returning `RewEqImportResult`.

- [ ] **Step 1: Write the failing model tests**

```csharp
[Fact]
public void NormalizeCreatesNeutralPresetWhenInputIsEmpty()
{
    var normalized = new AudioDspConfig(Presets: []).Normalize();

    Assert.False(normalized.Enabled);
    Assert.Equal(AudioDspConfig.DefaultPresetId, normalized.SelectedPresetId);
    Assert.Single(normalized.Presets);
    Assert.Empty(normalized.Presets[0].Rules);
}

[Fact]
public void ValidateRejectsDuplicatePresetIdsAndOutOfRangeBands()
{
    var preset = new AudioDspPreset(
        "same",
        "Calibration",
        Rules: [new(AudioDspChannelTarget.Left, [new(FrequencyHz: 5, Q: 99)])]);
    var errors = new AudioDspConfig(
        Enabled: true,
        SelectedPresetId: "same",
        Presets: [preset, preset]).Validate();

    Assert.Contains(errors, error => error.Contains("duplicate", StringComparison.Ordinal));
    Assert.Contains(errors, error => error.Contains("frequency", StringComparison.Ordinal));
    Assert.Contains(errors, error => error.Contains("Q", StringComparison.Ordinal));
}
```

- [ ] **Step 2: Run the model test to verify it fails**

Run: `dotnet test tests/MiruPlay.Windows.Tests/MiruPlay.Windows.Tests.csproj --filter FullyQualifiedName~AudioDspModelsTests --no-restore`

Expected: FAIL because `AudioDspConfig` and its normalization/validation API do not exist.

- [ ] **Step 3: Implement the model minimally**

Implement the Android-compatible storage values and ranges:

```csharp
public enum AudioDspPhaseMode { Minimum, Linear }
public enum AudioDspOutputMode { AutoPreserve, StereoDownmix, HrtfBinaural }
public enum AudioDspFirQuality { Low = 1024, Medium = 2048, High = 4096 }
public enum AudioDspFilterType { Peaking, LowShelf, HighShelf, LowPass, HighPass, Notch, BandPass }
public enum AudioDspChannelTarget
{
    All, Front, CenterLfe, Surround, Surround51, Surround71,
    Left, Right, Center, Lfe, LeftSurround, RightSurround,
}

public sealed record AudioDspBand(
    AudioDspFilterType Type = AudioDspFilterType.Peaking,
    double FrequencyHz = 1_000,
    double GainDb = 0,
    double Q = 1,
    bool Enabled = true);

public sealed record AudioDspChannelRule(
    AudioDspChannelTarget Target = AudioDspChannelTarget.All,
    IReadOnlyList<AudioDspBand>? Bands = null,
    double OutputGainDb = 0)
{
    public IReadOnlyList<AudioDspBand> Bands { get; init; } = Bands ?? [];
}

public sealed record AudioDspLimiter(
    bool Enabled = false,
    double CeilingDb = -1,
    double ReleaseMs = 100);

public sealed record AudioDspPreset(
    string Id,
    string Name,
    double PreampDb = 0,
    AudioDspPhaseMode PhaseMode = AudioDspPhaseMode.Minimum,
    AudioDspFirQuality FirQuality = AudioDspFirQuality.Medium,
    AudioDspOutputMode OutputMode = AudioDspOutputMode.AutoPreserve,
    IReadOnlyList<AudioDspChannelRule>? Rules = null,
    AudioDspLimiter? Limiter = null)
{
    public IReadOnlyList<AudioDspChannelRule> Rules { get; init; } = Rules ?? [];
    public AudioDspLimiter Limiter { get; init; } = Limiter ?? new();

    public static AudioDspPreset Neutral() => new("neutral", "Neutral");
}

public sealed record AudioDspConfig(
    bool Enabled = false,
    string SelectedPresetId = DefaultPresetId,
    IReadOnlyList<AudioDspPreset>? Presets = null,
    int SchemaVersion = CurrentSchemaVersion)
{
    public const int CurrentSchemaVersion = 1;
    public const string DefaultPresetId = "neutral";
    public IReadOnlyList<AudioDspPreset> Presets { get; init; } = Presets ?? [AudioDspPreset.Neutral()];

    public static AudioDspConfig Neutral() => new();
    public AudioDspConfig Normalize();
    public IReadOnlyList<string> Validate();
}
```

Add `Normalize()` and `Validate()` with schema version 1, max 32 bands per rule,
frequency 10-24000 Hz, gain -24 to 24 dB, Q 0.1-20, preamp -24 to 12 dB,
limiter ceiling -24 to 0 dB, and release 1-2000 ms. `Normalize()` trims ids and
names, removes duplicate preset ids after the first, clamps numeric values, and
always restores a neutral preset if needed. `Validate()` reports field paths
without mutating the input.

- [ ] **Step 4: Write the failing REW parser tests**

```csharp
[Fact]
public void ParseReadsGenericColumnsByNameAndSkipsDisabledAndNoneRows()
{
    const string text = """
Generic
Number\tEnabled\tControl\tType\tFrequency(Hz)\tGain(dB)\tQ\tBandwidth(Hz)
1\tTrue\tAuto\tPK\t70.00\t-14.7\t10.398\t6.73
2\tFalse\tAuto\tPK\t71.90\t9.0\t6.993\t10.28
3\tTrue\tManual\tLS\t78.30\t5.7\t\t
4\tTrue\tAuto\tNone\t\t\t\t

Compound_filters
Number\tEnabled\tControl\tType
1\tTrue\tAuto\tNone
""";

    var result = RewEqFileParser.Parse(text);

    Assert.Empty(result.Errors);
    Assert.Equal(2, result.Bands.Count);
    Assert.Equal(AudioDspFilterType.Peaking, result.Bands[0].Band.Type);
    Assert.Equal(70, result.Bands[0].Band.FrequencyHz);
    Assert.Equal(AudioDspFilterType.LowShelf, result.Bands[1].Band.Type);
}

[Fact]
public void ParseReportsLineNumberForUnsupportedEnabledFilter()
{
    var result = RewEqFileParser.Parse("Generic\nType\tEnabled\nX\tTrue\n");

    Assert.Contains(result.Errors, error => error.LineNumber == 3);
}
```

- [ ] **Step 5: Run the parser tests to verify they fail**

Run: `dotnet test tests/MiruPlay.Windows.Tests/MiruPlay.Windows.Tests.csproj --filter FullyQualifiedName~RewEqFileParserTests --no-restore`

Expected: FAIL because `RewEqFileParser` and `RewEqImportResult` do not exist.

- [ ] **Step 6: Implement the parser**

Implement `RewEqImportResult(IReadOnlyList<RewEqImportedBand> Bands,
IReadOnlyList<RewEqParseError> Errors, IReadOnlyList<string> Warnings)` and
`RewEqFileParser.Parse(string content)`. Split only on line boundaries, recognize
section names after trimming, locate columns from the tab-separated header, and
parse decimal values with `CultureInfo.InvariantCulture`. Map `PK`, `LS`, `HS`,
`LP`, `HP`, `NO`, and `BP`; default missing Q to 1.0 for shelf rows; skip false
`Enabled` and `None`; return one error per unsupported enabled row with the source
line number. `Compound_filters` rows are accepted and skipped when their type is
`None`; a non-empty unsupported compound type is a warning, not an audio filter.

- [ ] **Step 7: Run both focused test classes**

Run: `dotnet test tests/MiruPlay.Windows.Tests/MiruPlay.Windows.Tests.csproj --filter "FullyQualifiedName~AudioDspModelsTests|FullyQualifiedName~RewEqFileParserTests" --no-restore`

Expected: PASS with zero failed tests.

- [ ] **Step 8: Commit the self-contained model/parser change**

```powershell
git add src/MiruPlay.Windows/Models/AudioDspModels.cs src/MiruPlay.Windows/Services/RewEqFileParser.cs tests/MiruPlay.Windows.Tests/AudioDspModelsTests.cs tests/MiruPlay.Windows.Tests/RewEqFileParserTests.cs
git commit -m "feat: add audio DSP config and REW parser"
```

### Task 2: 逐声道响应、FIR 和路由数学

**Files:**
- Create: `src/MiruPlay.Windows/Services/AudioDspSignalMath.cs`
- Create: `tests/MiruPlay.Windows.Tests/AudioDspSignalMathTests.cs`

**Interfaces:**
- Consumes: `AudioDspPreset`, `AudioDspChannelRule`, and `AudioDspBand` from Task 1.
- Produces `BiquadCoefficients`, `AudioDspResponsePoint`, `AudioDspChannelLayout`,
  `AudioDspChannelResponse`, `LinearPhaseFirPlan`, and `LinearPhaseFirDesigner.Design(...)` for Task 3.

- [ ] **Step 1: Write the failing signal-math tests**

```csharp
[Fact]
public void DifferentLeftAndRightRulesProduceDifferentResponses()
{
    var preset = new AudioDspPreset("stereo", "Stereo", Rules: [
        new(AudioDspChannelTarget.Left, [new(GainDb: -6, FrequencyHz: 1_000, Q: 1)]),
        new(AudioDspChannelTarget.Right, [new(GainDb: 6, FrequencyHz: 1_000, Q: 1)]),
    ]);
    var response = AudioDspSignalMath.SampleChannels(preset, AudioDspChannelLayout.Stereo, 48_000);

    Assert.True(response[0].MagnitudeDbAt(1_000) < -5);
    Assert.True(response[1].MagnitudeDbAt(1_000) > 5);
}

[Fact]
public void LinearPhaseFirsAreSymmetricAndShareGroupDelay()
{
    var preset = new AudioDspPreset("stereo", "Stereo", PhaseMode: AudioDspPhaseMode.Linear,
        FirQuality: AudioDspFirQuality.Medium,
        Rules: [new(AudioDspChannelTarget.Left, [new(GainDb: -6)])]);
    var plans = AudioDspSignalMath.BuildLinearPhaseChannels(
        preset, AudioDspChannelLayout.Stereo, 48_000);

    Assert.Equal(2_048, plans[0].Taps.Length);
    Assert.Equal(plans[0].GroupDelaySamples, plans[1].GroupDelaySamples);
    Assert.All(plans, plan => Assert.Equal(plan.Taps.Reverse(), plan.Taps));
}
```

- [ ] **Step 2: Run the signal-math tests to verify they fail**

Run: `dotnet test tests/MiruPlay.Windows.Tests/MiruPlay.Windows.Tests.csproj --filter FullyQualifiedName~AudioDspSignalMathTests --no-restore`

Expected: FAIL because `AudioDspSignalMath` and its response/FIR types do not exist.

- [ ] **Step 3: Implement RBJ coefficients and response sampling**

Implement `AudioDspSignalMath.DesignBiquad(AudioDspBand, int sampleRateHz)` for
peaking, low/high shelf, low/high pass, notch, and band pass using RBJ formulas.
Implement `AudioDspSignalMath.SampleChannels(AudioDspPreset,
AudioDspChannelLayout, int)` so each channel resolves the most specific matching
rule and multiplies that channel's biquad magnitudes. Keep response samples in dB
and use a finite lower bound of `1e-12` before `log10`.

- [ ] **Step 4: Implement the linear-phase FIR designer**

Implement `LinearPhaseFirDesigner.Design(IReadOnlyList<double> magnitudeDb,
int sampleRateHz, int taps)` with frequency sampling and inverse real DFT. Apply a
window, force `h[i] == h[taps - 1 - i]` by averaging mirrored coefficients, and
return `LinearPhaseFirPlan(float[] Taps, int GroupDelaySamples)`. Build one plan per
channel, including flat channels, and use `(taps - 1) / 2` as the shared delay.

Define `AudioDspChannelLayout` as a record with `Id` and ordered `Channels`, and
provide `AudioDspChannelLayout.Mono`, `.Stereo`, `.Surround51`, and `.Surround71`
static values. Define `AudioDspChannelResponse` with `ChannelName`, a response
sampler, and `MagnitudeDbAt(double frequencyHz)` so compiler tests can inspect
left/right results without reimplementing DSP math.

- [ ] **Step 5: Implement channel layouts and standard downmix math**

Define stereo, mono, 5.1 and 7.1 layouts with stable labels `FL`, `FR`, `FC`,
`LFE`, `SL`, `SR`, `BL`, and `BR`. Implement `AudioDspSignalMath.ResolveTarget`
for each `AudioDspChannelTarget` and the Android ITU downmix matrix. Add HRTF as a
compiler route marker; the Windows graph will use FFmpeg's `headphone` filter.

- [ ] **Step 6: Run the focused tests to verify they pass**

Run: `dotnet test tests/MiruPlay.Windows.Tests/MiruPlay.Windows.Tests.csproj --filter FullyQualifiedName~AudioDspSignalMathTests --no-restore`

Expected: PASS, including the independent L/R response and common-delay checks.

- [ ] **Step 7: Commit the signal-math change**

```powershell
git add src/MiruPlay.Windows/Services/AudioDspSignalMath.cs tests/MiruPlay.Windows.Tests/AudioDspSignalMathTests.cs
git commit -m "feat: add per-channel DSP response and FIR math"
```

### Task 3: mpv/FFmpeg 滤镜图编译器

**Files:**
- Create: `src/MiruPlay.Windows/Services/AudioDspFilterGraphCompiler.cs`
- Create: `tests/MiruPlay.Windows.Tests/AudioDspFilterGraphCompilerTests.cs`

**Interfaces:**
- Consumes: Task 1 models and Task 2 channel responses/FIR metadata.
- Produces `AudioDspFilterGraphCompiler.Compile(AudioDspConfig,
AudioDspChannelLayout, int)` returning `AudioDspFilterGraph` with `AfValue`,
`MpvArguments`, `EffectiveRoute`, and `Warnings`.

Define `AudioDspFilterGraph` as a record with
`string AfValue`, `IReadOnlyList<string> MpvArguments`, `string EffectiveRoute`,
and `IReadOnlyList<string> Warnings`. `MpvArguments` contains complete
`--audio-*` and `--af=...` arguments and is empty for disabled DSP.

- [ ] **Step 1: Write the failing compiler tests**

```csharp
[Fact]
public void DisabledConfigProducesNoAudioDspArguments()
{
    var graph = AudioDspFilterGraphCompiler.Compile(
        AudioDspConfig.Neutral(), AudioDspChannelLayout.Stereo, 48_000);

    Assert.Empty(graph.MpvArguments);
    Assert.Equal("disabled", graph.EffectiveRoute);
}

[Fact]
public void LinearStereoGraphContainsIndependentFirequalizerBranchesAndSharedDelay()
{
    var config = new AudioDspConfig(true, "stereo", [new(
        "stereo", "Stereo", PhaseMode: AudioDspPhaseMode.Linear,
        Rules: [new(AudioDspChannelTarget.Left, [new(GainDb: -6)])])]);
    var graph = AudioDspFilterGraphCompiler.Compile(
        config, AudioDspChannelLayout.Stereo, 48_000);

    Assert.Contains("channelsplit", graph.AfValue, StringComparison.Ordinal);
    Assert.Equal(2, graph.AfValue.Split("firequalizer", StringSplitOptions.None).Length - 1);
    Assert.Equal(2, Regex.Matches(graph.AfValue, "delay=").Count);
    Assert.Contains("--audio-format=float", graph.MpvArguments);
    Assert.Contains("--audio-spdif=no", graph.MpvArguments);
}
```

- [ ] **Step 2: Run the compiler tests to verify they fail**

Run: `dotnet test tests/MiruPlay.Windows.Tests/MiruPlay.Windows.Tests.csproj --filter FullyQualifiedName~AudioDspFilterGraphCompilerTests --no-restore`

Expected: FAIL because the compiler and graph records do not exist.

- [ ] **Step 3: Implement minimum-phase graph generation**

Resolve the selected preset and produce filters in this order: preamp, per-channel
rule filters/gain, output route, limiter. Use invariant-culture formatting with
at most six decimal places. Map REW/internal filter types to FFmpeg filter names,
and escape all `;`, `|`, `,`, `=` and `:` separators generated inside the `lavfi`
expression. A graph with no active band still emits the neutral audio route when
DSP is enabled.

- [ ] **Step 4: Implement independent linear-phase branches**

For each known channel, emit a `channelsplit` branch and one `firequalizer` with
that channel's sampled `gain_entry`. Set `fixed=true`, `zero_phase=false`, and
the same `delay=(taps - 1) / (2 * sampleRateHz)` and quality-derived `accuracy`
on every branch, including flat channels. Merge branches in original channel
order. This makes all channel EQ filters linear phase with the same group delay;
it does not attempt to reconstruct an absent REW measurement phase curve.

- [ ] **Step 5: Implement downmix, HRTF, limiter and output arguments**

Emit the Android-compatible stereo `pan` matrix for `StereoDownmix`, emit
`headphone=map=FL|FR|FC|BL|BR` for `HrtfBinaural`, and emit
`alimiter=limit=<linear-ceiling>:release=<release-ms>` when enabled. Add
`--audio-format=float`, `--audio-spdif=no`, `--audio-exclusive=no`,
`--audio-channels=auto`, and `--af=lavfi=[...]` only when enabled. For unknown
layouts, apply only `All` rules and return a warning for target-specific rules.

- [ ] **Step 6: Run compiler and math tests**

Run: `dotnet test tests/MiruPlay.Windows.Tests/MiruPlay.Windows.Tests.csproj --filter "FullyQualifiedName~AudioDspFilterGraphCompilerTests|FullyQualifiedName~AudioDspSignalMathTests" --no-restore`

Expected: PASS with finite graph values, independent branches, common delay, and
no arguments for the disabled configuration.

- [ ] **Step 7: Commit the compiler change**

```powershell
git add src/MiruPlay.Windows/Services/AudioDspFilterGraphCompiler.cs tests/MiruPlay.Windows.Tests/AudioDspFilterGraphCompilerTests.cs
git commit -m "feat: compile per-channel mpv DSP filters"
```

### Task 4: 设置持久化与 mpv 会话集成

**Files:**
- Modify: `src/MiruPlay.Windows/Services/AppSettingsStore.cs`
- Modify: `src/MiruPlay.Windows/Services/MpvPlayerLauncher.cs`
- Modify: `src/MiruPlay.Windows/Services/MpvPlaybackSession.cs`
- Modify: `tests/MiruPlay.Windows.Tests/MpvPlayerLauncherTests.cs`
- Modify: `tests/MiruPlay.Windows.Tests/MpvPlaybackCoreTests.cs`
- Create: `tests/MiruPlay.Windows.Tests/AudioDspRuntimeTests.cs`

**Interfaces:**
- Consumes: `AudioDspFilterGraphCompiler.Compile` from Task 3.
- Produces `MpvPlaybackSession.ApplyAudioDspAsync(AudioDspFilterGraph)` and a
  settings update path that applies the graph before saving.

- [ ] **Step 1: Write failing launcher and persistence tests**

```csharp
[Fact]
public void CreateStartInfoAddsDspArgumentsOnlyWhenEnabled()
{
    var episode = CreateEpisode(CreateFile("episode.mkv"));
    var settings = new AppSettings(AudioDsp: new AudioDspConfig(
        true, AudioDspConfig.DefaultPresetId, [AudioDspPreset.Neutral()]));

    var startInfo = MpvPlayerLauncher.CreateStartInfo(
        "mpv.exe", "pipe", episode, settings, null);

    Assert.Contains("--audio-format=float", startInfo.ArgumentList);
    Assert.Contains(startInfo.ArgumentList, argument => argument.StartsWith("--af=", StringComparison.Ordinal));
}

[Fact]
public void MissingAudioDspFieldLoadsNeutralConfiguration()
{
    File.WriteAllText(_settingsPath, "{\"LibraryRoot\":null}");

    var settings = new AppSettingsStore(_settingsPath).Load();

    Assert.False(settings.AudioDsp.Enabled);
    Assert.Equal(AudioDspConfig.DefaultPresetId, settings.AudioDsp.SelectedPresetId);
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/MiruPlay.Windows.Tests/MiruPlay.Windows.Tests.csproj --filter "FullyQualifiedName~MpvPlayerLauncherTests|FullyQualifiedName~AudioDspRuntimeTests" --no-restore`

Expected: FAIL because `AppSettings.AudioDsp` and the DSP launch integration do not exist.

- [ ] **Step 3: Add persisted DSP configuration**

Add `AudioDspConfig AudioDsp = null` to `AppSettings` with a constructor-safe
neutral property value. Normalize it in `Load()` after the existing settings
migrations and save only when normalization changed the serialized value. Keep
the one-megabyte check and atomic temp-file replacement unchanged.

- [ ] **Step 4: Add startup mpv arguments**

In `CreateStartInfo`, compile `settings.AudioDsp` before adding the media path.
Append graph arguments only when the selected mpv path exists and DSP is enabled.
If compilation returns errors, throw `InvalidOperationException` before starting
mpv. Leave the existing system-player fallback branch untouched, so it never sees
DSP arguments.

- [ ] **Step 5: Add runtime IPC filter updates**

Add `Task ApplyAudioDspAsync(AudioDspFilterGraph graph)` to
`MpvPlaybackSession`. Under `_ipcLock`, send
`["set_property", "af", graph.AfValue]`; update the public DSP status only after
mpv acknowledges the request. Catch IPC/timeout errors, retain the previous status,
and rethrow so the caller can keep the stored configuration unchanged. Add a pure
command serialization helper test if the existing session test fixture cannot
start a real named-pipe mpv process.

- [ ] **Step 6: Run focused playback tests**

Run: `dotnet test tests/MiruPlay.Windows.Tests/MiruPlay.Windows.Tests.csproj --filter "FullyQualifiedName~MpvPlayerLauncherTests|FullyQualifiedName~MpvPlaybackCoreTests|FullyQualifiedName~AudioDspRuntimeTests" --no-restore`

Expected: PASS; existing subtitle/progress/audio-track tests must remain green.

- [ ] **Step 7: Commit the mpv integration change**

```powershell
git add src/MiruPlay.Windows/Services/AppSettingsStore.cs src/MiruPlay.Windows/Services/MpvPlayerLauncher.cs src/MiruPlay.Windows/Services/MpvPlaybackSession.cs tests/MiruPlay.Windows.Tests/MpvPlayerLauncherTests.cs tests/MiruPlay.Windows.Tests/MpvPlaybackCoreTests.cs tests/MiruPlay.Windows.Tests/AudioDspRuntimeTests.cs
git commit -m "feat: apply DSP to MiruPlay mpv sessions"
```

### Task 5: WPF DSP 设置和 REW 导入

**Files:**
- Create: `src/MiruPlay.Windows/AudioDspDialog.xaml`
- Create: `src/MiruPlay.Windows/AudioDspDialog.xaml.cs`
- Create: `src/MiruPlay.Windows/Services/AudioDspEditorState.cs`
- Modify: `src/MiruPlay.Windows/MainWindow.xaml`
- Modify: `src/MiruPlay.Windows/MainWindow.xaml.cs`
- Create: `tests/MiruPlay.Windows.Tests/AudioDspEditorStateTests.cs`

**Interfaces:**
- Consumes: persisted `AppSettings.AudioDsp`, `RewEqFileParser`, and mpv runtime
  apply operation from Tasks 1 and 4.
- Produces a native WPF editor that returns an `AudioDspConfig` only after the
  user applies a valid edit.

- [ ] **Step 1: Write the failing editor-state tests**

```csharp
[Fact]
public void ImportRewReplacesOnlyTheSelectedChannelRule()
{
    var config = new AudioDspConfig(true, "p", [new(
        "p", "Preset", Rules: [
            new(AudioDspChannelTarget.Left, [new(GainDb: 2)]),
            new(AudioDspChannelTarget.Right, [new(GainDb: 3)]),
        ])]);
    var imported = new[] { new AudioDspBand(GainDb: -14.7, FrequencyHz: 70, Q: 10.398) };

    var updated = AudioDspEditorState.ReplaceChannelBands(
        config, "p", AudioDspChannelTarget.Left, imported);

    Assert.Equal(-14.7, updated.Presets[0].Rules[0].Bands[0].GainDb, 3);
    Assert.Equal(3, updated.Presets[0].Rules[1].Bands[0].GainDb);
}
```

- [ ] **Step 2: Run the editor-state test to verify it fails**

Run: `dotnet test tests/MiruPlay.Windows.Tests/MiruPlay.Windows.Tests.csproj --filter FullyQualifiedName~AudioDspEditorStateTests --no-restore`

Expected: FAIL because the channel replacement helper and editor do not exist.

- [ ] **Step 3: Implement the pure editor state helper**

Add `AudioDspEditorState.ReplaceChannelBands(AudioDspConfig, string presetId,
AudioDspChannelTarget target, IReadOnlyList<AudioDspBand>)`. Replace only the
matching rule's bands, preserve its output gain, preserve all other rules, and
create the target rule when absent. Return a normalized config and reject a
preset id that does not exist.

- [ ] **Step 4: Build the WPF editor dialog**

Use standard `CheckBox`, `ComboBox`, `TextBox`, `DataGrid`, and buttons. Expose
AutomationProperties names for enable, preset, target channel, phase mode, FIR
quality, output mode, limiter, REW import, apply, and cancel. Keep stable widths
for numeric columns. The REW import flow opens `OpenFileDialog`, reads UTF-8 text,
parses it, asks for a target channel in the dialog, previews the mapped rows, and
replaces only that target after confirmation.

- [ ] **Step 5: Connect WPF apply atomically**

Add a settings-page summary and open-editor button. On apply, compile the new
config, call the active session's `ApplyAudioDspAsync` when a MiruPlay mpv session
exists, then update `_settings` and call `_settingsStore.Save`. If compile or IPC
fails, show the error in `StatusText`, leave `_settings` and the dialog's source
config unchanged, and keep the old mpv graph.

- [ ] **Step 6: Run the editor and existing UI-adjacent tests**

Run: `dotnet test tests/MiruPlay.Windows.Tests/MiruPlay.Windows.Tests.csproj --filter "FullyQualifiedName~AudioDspEditorStateTests|FullyQualifiedName~MpvPlayerLauncherTests" --no-restore`

Expected: PASS with no changes to existing settings behavior.

- [ ] **Step 7: Commit the WPF change**

```powershell
git add src/MiruPlay.Windows/AudioDspDialog.xaml src/MiruPlay.Windows/AudioDspDialog.xaml.cs src/MiruPlay.Windows/MainWindow.xaml src/MiruPlay.Windows/MainWindow.xaml.cs tests/MiruPlay.Windows.Tests/AudioDspEditorStateTests.cs
git commit -m "feat: add Windows DSP settings and REW import"
```

### Task 6: Web API 与 WebUI 对等能力

**Files:**
- Modify: `src/MiruPlay.Windows/Services/WebControlServer.cs`
- Modify: `src/MiruPlay.Windows/MainWindow.xaml.cs`
- Modify: `src/MiruPlay.Windows/Web/index.html`
- Modify: `src/MiruPlay.Windows/Web/app.js`
- Modify: `src/MiruPlay.Windows/Web/app.css`
- Create: `tests/MiruPlay.Windows.Tests/AudioDspWebControlTests.cs`

**Interfaces:**
- Consumes: `AudioDspConfig`, `RewEqFileParser`, `AudioDspFilterGraphCompiler`,
  and the WPF-owned atomic apply delegate.
- Produces `GET/PUT /api/audio-dsp`, `POST /api/audio-dsp/preview`, and
  `POST /api/audio-dsp/import-rew`.

- [ ] **Step 1: Write failing Web API tests**

```csharp
[Fact]
public async Task InvalidAudioDspPutDoesNotChangeStoredConfig()
{
    var before = harness.Settings.AudioDsp;

    var response = await harness.PutAsync("/api/audio-dsp", """
        {"config":{"schemaVersion":1,"enabled":true,"selectedPresetId":"missing","presets":[]}}
        """);

    Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    Assert.Equal(before, harness.Settings.AudioDsp);
}

[Fact]
public async Task RewImportReturnsMappedBandsWithoutPersisting()
{
    var response = await harness.PostAsync("/api/audio-dsp/import-rew", """
        {"target":"left","content":"Generic\nType\tEnabled\tFrequency(Hz)\tGain(dB)\tQ\nPK\tTrue\t70\t-14.7\t10.398"}
        """);

    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    Assert.Contains("peaking", await response.Content.ReadAsStringAsync());
    Assert.Empty(harness.Settings.AudioDsp.Presets[0].Rules);
}
```

- [ ] **Step 2: Run the Web API tests to verify they fail**

Run: `dotnet test tests/MiruPlay.Windows.Tests/MiruPlay.Windows.Tests.csproj --filter FullyQualifiedName~AudioDspWebControlTests --no-restore`

Expected: FAIL because the routes and request DTOs do not exist.

- [ ] **Step 3: Add Web API DTOs and routes**

Add dedicated request/response records in `WebControlServer.cs`. `GET` returns
config, supported layouts/sample rates, `effectiveRoute`, and warnings. `PUT`
deserializes a complete config, calls the same compile/apply delegate used by WPF,
and persists only after apply succeeds. `preview` compiles a supplied preset at
48 kHz and returns finite frequency, magnitude, and phase arrays. `import-rew`
accepts `{ target, content }`, parses the content, returns mapped bands/errors,
and never changes settings.

- [ ] **Step 4: Add the WebUI editor**

Add a playback navigation item and a dedicated view with enable/preset controls,
phase/FIR/output/limiter fields, target-channel selection, PEQ rows, REW file input,
response preview, and apply status. Use `File.text()` then send the content as JSON
to `import-rew`; merge returned bands into the selected target in the browser and
send the complete config to `PUT`. Escape imported names and server values with the
existing `escapeHtml` helper. Keep the existing playback settings view unchanged.

- [ ] **Step 5: Run Web API and existing WebControl tests**

Run: `dotnet test tests/MiruPlay.Windows.Tests/MiruPlay.Windows.Tests.csproj --filter "FullyQualifiedName~AudioDspWebControlTests|FullyQualifiedName~WebControlServerTests" --no-restore`

Expected: PASS, including authorization, existing playback routes, invalid DSP
atomicity, preview, and REW import.

- [ ] **Step 6: Commit the Web API/WebUI change**

```powershell
git add src/MiruPlay.Windows/Services/WebControlServer.cs src/MiruPlay.Windows/MainWindow.xaml.cs src/MiruPlay.Windows/Web/index.html src/MiruPlay.Windows/Web/app.js src/MiruPlay.Windows/Web/app.css tests/MiruPlay.Windows.Tests/AudioDspWebControlTests.cs
git commit -m "feat: expose DSP configuration through WebControl"
```

### Task 7: 完整验证与发布前检查

**Files:**
- Modify: `docs/compatibility.md` only if the existing compatibility matrix
  needs a DSP/mpv note after the implementation is verified.
- Test: all files changed by Tasks 1-6.

- [ ] **Step 1: Run the full test suite**

Run: `dotnet test tests/MiruPlay.Windows.Tests/MiruPlay.Windows.Tests.csproj --no-restore`

Expected: exit code 0 and zero failed tests.

- [ ] **Step 2: Run a Release build**

Run: `dotnet build src/MiruPlay.Windows/MiruPlay.Windows.csproj -c Release --no-restore`

Expected: exit code 0 with no compiler errors.

- [ ] **Step 3: Validate FFmpeg filter syntax locally**

Generate a stereo 48 kHz one-second sine sweep and run the compiled `lavfi`
expression through the installed `ffmpeg` binary. Verify the command accepts two
independent linear-phase branches, the same delay value appears in both branches,
and the output remains finite. The current environment has FFmpeg but no `mpv.exe`,
so this check is the available local evidence for filter syntax.

- [ ] **Step 4: Validate MiruPlay mpv behavior when mpv is available**

Start a local deterministic test media file through `MpvPlayerLauncher`, read the
JSON IPC `af` property, and verify DSP arguments are present only for the MiruPlay
mpv process. Run once with DSP disabled and once with a left/right REW profile; do
not treat the system-player fallback as DSP evidence.

- [ ] **Step 5: Recheck the working tree and report residual gaps**

Run: `git status --short; git diff --check; git log -8 --oneline --decorate`

Confirm the only intentional changes are the DSP implementation plus the design/
plan documents, preserve unrelated user edits, and explicitly report if no mpv
executable was available for the final process-level check.
