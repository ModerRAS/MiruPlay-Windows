using MiruPlay.Windows.Models;

namespace MiruPlay.Windows.Services;

/// <summary>
/// Sweep measurement orchestration: capability probe, dual-sweep play+capture
/// over WinMM, offline WAV import, UMIK-1 calibration download and the
/// fail-closed analysis pipeline. DSP math lives in the RoomMeasure* core;
/// this class owns audio I/O and preset application only.
/// </summary>
public sealed class RoomMeasureRunner
{
    public const int SampleRateHz = 48_000;
    private const double SweepF1Hz = 20.0;
    private const double SweepF2Hz = 20_000.0;
    private static readonly double[] SweepDurationsS = [4.0, 4.17];
    private const double RoomTailS = 1.0;
    private const double IrLengthS = 1.0;
    private const double SweepAmplitude = 0.25;
    private const double NoiseAmplitude = 0.1;
    private const string UmikBaseUrl = "https://www.minidsp.com/scripts/umikcal";

    public sealed record Capabilities(
        bool Available,
        string? Reason,
        string? InputDeviceName,
        IReadOnlyList<WaveAudio.AudioDevice> Mics);

    public sealed record MeasureOutcome(RoomMeasurePipeline.Measurement Measurement, Capabilities Capabilities);

    public sealed class MeasureException(string message) : Exception(message);

    private readonly HttpClient _http;

    public RoomMeasureRunner(HttpMessageHandler? handler = null)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = TimeSpan.FromSeconds(30);
    }

    public static string NormalizeUmikSerial(string input)
    {
        var digits = new string((input ?? "").Where(char.IsDigit).ToArray());
        if (digits.Length != 7) throw new MeasureException("序列号需要恰好 7 位数字（如 700-1234）。");
        return digits;
    }

    public static string BuildUmikUrl(string serial, MicCalibration.Incidence incidence)
    {
        var sn = NormalizeUmikSerial(serial);
        return incidence == MicCalibration.Incidence.ZeroDeg
            ? $"{UmikBaseUrl}/umik.php/{sn}.txt"
            : $"{UmikBaseUrl}/umik90.php/{sn}_90deg.txt";
    }

    public static bool IsUmikErrorBody(string text) =>
        text.Contains("Unable to locate calibration data", StringComparison.OrdinalIgnoreCase);

    public async Task<string> DownloadUmikCalibrationAsync(
        string serial,
        MicCalibration.Incidence incidence,
        CancellationToken cancellationToken = default)
    {
        var url = BuildUmikUrl(serial, incidence);
        string text;
        try
        {
            using var response = await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new MeasureException($"下载校准文件失败（HTTP {(int)response.StatusCode}）。");
            text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException error)
        {
            throw new MeasureException($"下载校准文件失败：{error.Message}");
        }
        if (text.Length > MicCalibration.MaxTextChars) throw new MeasureException("校准文件过大。");
        if (IsUmikErrorBody(text))
            throw new MeasureException($"miniDSP 上没有找到该序列号（{serial}）的校准数据，请核对序列号。");
        var parsed = MicCalibration.Parse(text, incidence);
        if (parsed.Calibration.CoverageWarning() is { } warning) throw new MeasureException(warning);
        return text;
    }

    public static Capabilities Probe(IReadOnlyList<WaveAudio.AudioDevice>? captureDevices = null)
    {
        IReadOnlyList<WaveAudio.AudioDevice> mics;
        try
        {
            mics = captureDevices ?? WaveAudio.ListCaptureDevices();
        }
        catch (Exception error) when (error is InvalidOperationException or DllNotFoundException or EntryPointNotFoundException)
        {
            return new Capabilities(false, $"枚举麦克风失败：{error.Message}", null, []);
        }
        if (mics.Count == 0)
            return new Capabilities(false, "未找到可用的麦克风输入设备", null, []);
        var preferred = PickPreferredMicrophone(mics);
        return new Capabilities(true, null, preferred.Name, mics);
    }

    internal static WaveAudio.AudioDevice PickPreferredMicrophone(IReadOnlyList<WaveAudio.AudioDevice> mics)
    {
        foreach (var mic in mics)
        {
            if (mic.Name.Contains("UMIK", StringComparison.OrdinalIgnoreCase)) return mic;
        }
        foreach (var mic in mics)
        {
            if (mic.Name.Contains("USB", StringComparison.OrdinalIgnoreCase)) return mic;
        }
        return mics[0];
    }

    public static string? DescribeDefaultOutput()
    {
        try
        {
            var outputs = WaveAudio.ListRenderDevices();
            return outputs.Count == 0 ? null : outputs[0].Name;
        }
        catch (Exception error) when (error is InvalidOperationException or DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    /// <summary>Pink noise (Paul Kellet approximation) stereo preview for level matching.</summary>
    public static short[] BuildPinkNoisePreview(int sampleRateHz, double seconds)
    {
        var frames = (int)(sampleRateHz * seconds);
        var output = new short[frames * 2];
        var random = new Random();
        double b0 = 0, b1 = 0, b2 = 0, b3 = 0, b4 = 0, b5 = 0, b6 = 0;
        for (var i = 0; i < frames; i++)
        {
            var white = random.NextDouble() * 2 - 1;
            b0 = 0.99886 * b0 + white * 0.0555179;
            b1 = 0.99332 * b1 + white * 0.0750759;
            b2 = 0.96900 * b2 + white * 0.1538520;
            b3 = 0.86650 * b3 + white * 0.3104856;
            b4 = 0.55000 * b4 + white * 0.5329522;
            b5 = -0.7616 * b5 - white * 0.0168980;
            var pink = (b0 + b1 + b2 + b3 + b4 + b5 + b6 + white * 0.5362) * 0.11;
            b6 = white * 0.115926;
            var sample = (short)Math.Clamp(Math.Round(pink * NoiseAmplitude * 32767), short.MinValue, short.MaxValue);
            output[i * 2] = sample;
            output[i * 2 + 1] = sample;
        }
        return output;
    }

    public static void PlayPreview(short[] stereoInterleaved, CancellationToken cancellationToken = default) =>
        WaveAudio.Play(-1, stereoInterleaved, SampleRateHz, cancellationToken);

    public static short[] BuildSweepPlayback(double[] sweepMono)
    {
        var output = new short[sweepMono.Length * 2];
        for (var i = 0; i < sweepMono.Length; i++)
        {
            var sample = (short)Math.Clamp(Math.Round(sweepMono[i] * SweepAmplitude * 32767), short.MinValue, short.MaxValue);
            output[i * 2] = sample;
            output[i * 2 + 1] = sample;
        }
        return output;
    }

    public static async Task<MeasureOutcome> MeasureRoomAsync(
        string? calibrationText = null,
        int? preferredMicId = null,
        Action<string>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        var capabilities = Probe();
        if (!capabilities.Available) throw new MeasureException(capabilities.Reason ?? "microphone unavailable");
        var calibration = calibrationText is null ? null : MicCalibration.Parse(calibrationText).Calibration;
        var micId = preferredMicId ?? capabilities.Mics[0].Id;
        var sweeps = SweepDurationsS
            .Select(duration => RoomMeasureSweep.Generate(SweepF1Hz, SweepF2Hz, duration, SampleRateHz))
            .ToList();
        var recordings = new List<double[]>();
        for (var i = 0; i < sweeps.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            onProgress?.Invoke($"播放扫频 {i + 1}/{sweeps.Count}（{SweepDurationsS[i]}s）…");
            recordings.Add(await PlayAndCaptureAsync(sweeps[i].Sweep, micId, cancellationToken).ConfigureAwait(false));
        }
        onProgress?.Invoke("分析房间响应…");
        var measurement = await Task.Run(() => RoomMeasurePipeline.Measure(
            recordings, sweeps, SampleRateHz, irLengthS: IrLengthS, calibration: calibration),
            cancellationToken).ConfigureAwait(false);
        return new MeasureOutcome(measurement, capabilities);
    }

    public static async Task<MeasureOutcome> ImportWavAsync(
        byte[] wavBytes,
        string? calibrationText = null,
        Action<string>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(wavBytes);
        onProgress?.Invoke("解析 WAV…");
        var (samples, fs) = await Task.Run(() => WavFile.Read16BitMono(wavBytes), cancellationToken).ConfigureAwait(false);
        var calibration = calibrationText is null ? null : MicCalibration.Parse(calibrationText).Calibration;
        var candidates = SweepDurationsS.Where(duration => samples.Length >= (duration + 0.5) * fs).ToList();
        if (candidates.Count == 0)
            throw new MeasureException($"WAV 太短：需要 ≥ {(int)SweepDurationsS[0] + 1} 秒的标准扫频录音（含房间尾音）。");
        onProgress?.Invoke("分析房间响应…");
        RoomMeasurePipeline.Measurement? best = null;
        var bestSharpness = -1.0;
        foreach (var duration in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sweep = RoomMeasureSweep.Generate(SweepF1Hz, SweepF2Hz, duration, fs);
            var measurement = await Task.Run(() => RoomMeasurePipeline.Measure(
                [samples], [sweep], fs, irLengthS: IrLengthS,
                assumeSharedClock: true, calibration: calibration),
                cancellationToken).ConfigureAwait(false);
            var sharpness = RoomMeasureSweep.IrSharpness(measurement.Ir);
            if (sharpness > bestSharpness)
            {
                bestSharpness = sharpness;
                best = measurement;
            }
        }
        if (best is null) throw new MeasureException("WAV 分析失败。");
        return new MeasureOutcome(best, new Capabilities(true, null, "shared-clock import", []));
    }

    public static AudioDspConfig ApplyMeasurement(AudioDspConfig config, RoomMeasurePipeline.Measurement measurement, long timestampMs)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(measurement);
        if (!measurement.Valid)
            throw new MeasureException(measurement.InvalidReason ?? "measurement is invalid");
        return RoomMeasureAutoEq.ApplyToConfig(
            config,
            new RoomMeasureAutoEq.MeasuredBands(measurement.Fit.Bands, true),
            timestampMs);
    }

    private static async Task<double[]> PlayAndCaptureAsync(double[] sweep, int micId, CancellationToken cancellationToken)
    {
        var playback = BuildSweepPlayback(sweep);
        var captureDuration = TimeSpan.FromSeconds(sweep.Length / (double)SampleRateHz + RoomTailS);
        var captureTask = Task.Run(() => WaveAudio.Capture(micId, SampleRateHz, captureDuration, cancellationToken), cancellationToken);
        // Lead the capture slightly so the sweep onset is never clipped.
        await Task.Delay(120, cancellationToken).ConfigureAwait(false);
        await Task.Run(() => WaveAudio.Play(-1, playback, SampleRateHz, cancellationToken), cancellationToken).ConfigureAwait(false);
        var recorded = await captureTask.ConfigureAwait(false);
        return recorded;
    }
}
