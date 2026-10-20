using MiruPlay.Windows.Services;

namespace MiruPlay.Windows.Tests;

public sealed class RoomMeasurePipelineTests
{
    [Fact]
    public void MicCalibrationParsesThreeColumnUmikFiles()
    {
        const string text = """
            Sens Factor =-0.5dB, SERNO: 7001234
            # comment
            "Auto-generated 90-degree calibration file"
            20.0 -1.5 -2.0
            100.0, -0.5, -0.7
            20000.0; 0.5; 0.3
            """;

        var parsed = MicCalibration.Parse(text);
        Assert.Equal(3, parsed.ColumnCount);
        var zero = parsed.Calibration;
        Assert.Equal([20.0, 100.0, 20000.0], zero.FrequenciesHz);
        var ninety = MicCalibration.Parse(text, MicCalibration.Incidence.NinetyDeg).Calibration;
        var corrected = ninety.Correct([10.0, 10.0, 10.0], [20.0, 100.0, 20000.0]);
        Assert.Equal(10.0 - -2.0, corrected[0], 9);
        Assert.Null(zero.CoverageWarning());
    }

    [Fact]
    public void MicCalibrationRejectsGarbageAndWarnsOnTruncation()
    {
        Assert.Throws<ArgumentException>(() => MicCalibration.Parse("hello\nworld\n"));
        var truncated = MicCalibration.Parse("20 0\n1000 0\n");
        Assert.NotNull(truncated.Calibration.CoverageWarning());
    }

    [Fact]
    public void WavFileRoundTripsMonoSamples()
    {
        var path = Path.Combine(Path.GetTempPath(), $"miruplay-measure-{Guid.NewGuid():N}.wav");
        try
        {
            var samples = Enumerable.Range(0, 4_800).Select(i => Math.Sin(i * 0.1) * 0.5).ToArray();
            WavFile.Write16BitMono(path, samples, 48_000);
            var read = WavFile.Read16BitMono(path);
            Assert.Equal(48_000, read.SampleRateHz);
            Assert.Equal(samples.Length, read.Samples.Length);
            for (var i = 0; i < samples.Length; i += 377)
                Assert.True(Math.Abs(samples[i] - read.Samples[i]) < 1 / 32767.0 + 1e-9);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void WavFileRejectsNonRiffInput()
    {
        Assert.Throws<InvalidDataException>(() => WavFile.Read16BitMono("not a wav"u8.ToArray()));
    }

    [Fact]
    public void WaveAudioEnumeratesDevicesWithoutThrowing()
    {
        var render = WaveAudio.ListRenderDevices();
        var capture = WaveAudio.ListCaptureDevices();
        Assert.All(render, device => Assert.False(string.IsNullOrWhiteSpace(device.Name)));
        Assert.All(capture, device => Assert.False(string.IsNullOrWhiteSpace(device.Name)));
        Assert.Distinct(render.Select(device => device.Id));
    }

    [Fact]
    public void DualSweepSyntheticMeasurementIsValid()
    {
        const int fs = 16_000;
        var random = new Random(42);
        var ir = new double[2_000];
        ir[0] = 1;
        for (var i = 1; i < ir.Length; i++) ir[i] = (random.NextDouble() - 0.5) * Math.Exp(-i / 300.0);
        var sweeps = new[]
        {
            RoomMeasureSweep.Generate(20, 8_000, 1.0, fs),
            RoomMeasureSweep.Generate(20, 8_000, 1.07, fs),
        };
        var recordings = sweeps
            .Select(sweep => RoomMeasureFft.FftConvolve(sweep.Sweep, ir))
            .ToList();

        var measurement = RoomMeasurePipeline.Measure(
            recordings, sweeps, fs,
            gridPerOctave: 12, gridLoHz: 20, gridHiHz: 8_000);

        Assert.True(measurement.Valid, measurement.InvalidReason);
        Assert.True(measurement.Drift.Consistent);
        Assert.Equal(2, measurement.Drift.Estimates.Count);
        Assert.NotEmpty(measurement.Fit.Bands);
    }

    [Fact]
    public void SingleSweepMeasurementIsInvalidByConstruction()
    {
        const int fs = 16_000;
        var sweep = RoomMeasureSweep.Generate(20, 8_000, 1.0, fs);
        var recorded = RoomMeasureFft.FftConvolve(sweep.Sweep, [1.0, 0.5, 0.25]);

        var measurement = RoomMeasurePipeline.Measure(
            [recorded], [sweep], fs,
            gridPerOctave: 12, gridLoHz: 20, gridHiHz: 8_000);

        Assert.False(measurement.Valid);
        Assert.Contains("dual sweep", measurement.InvalidReason);
    }

    [Fact]
    public void DriftedDualSweepPassesTheConsistencyGate()
    {
        const int fs = 16_000;
        var random = new Random(7);
        var ir = new double[2_000];
        ir[0] = 1;
        for (var i = 1; i < ir.Length; i++) ir[i] = (random.NextDouble() - 0.5) * Math.Exp(-i / 300.0);
        var sweeps = new[]
        {
            RoomMeasureSweep.Generate(20, 8_000, 2.0, fs),
            RoomMeasureSweep.Generate(20, 8_000, 2.07, fs),
        };
        var recordings = sweeps
            .Select(sweep => RoomMeasureSweep.ApplyDrift(RoomMeasureFft.FftConvolve(sweep.Sweep, ir), 2.0))
            .ToList();

        var measurement = RoomMeasurePipeline.Measure(
            recordings, sweeps, fs,
            gridPerOctave: 12, gridLoHz: 20, gridHiHz: 8_000);

        // Both sweeps must see the same drift for the gate to pass; absolute
        // estimator accuracy is covered by the DSP-core tests with longer sweeps.
        Assert.Equal(2, measurement.Drift.Estimates.Count);
        Assert.True(measurement.Drift.PpmSpread < 1.0, $"spread={measurement.Drift.PpmSpread}");
        Assert.True(measurement.Drift.Consistent);
        Assert.True(measurement.Valid, measurement.InvalidReason);
    }

    [Fact]
    public void UmikSerialAndUrlHelpersBehave()
    {
        Assert.Equal("7001234", RoomMeasureRunner.NormalizeUmikSerial("700-1234"));
        Assert.Throws<RoomMeasureRunner.MeasureException>(() => RoomMeasureRunner.NormalizeUmikSerial("123"));
        Assert.EndsWith("/umik.php/7001234.txt",
            RoomMeasureRunner.BuildUmikUrl("7001234", MicCalibration.Incidence.ZeroDeg));
        Assert.EndsWith("/umik90.php/7001234_90deg.txt",
            RoomMeasureRunner.BuildUmikUrl("700 1234", MicCalibration.Incidence.NinetyDeg));
        Assert.True(RoomMeasureRunner.IsUmikErrorBody("<html>Unable to locate calibration data</html>"));
        Assert.False(RoomMeasureRunner.IsUmikErrorBody("20.0 -1.5 -2.0\n"));
    }

    [Fact]
    public void ProbePrefersUmikThenUsbMicrophones()
    {
        var mics = new[]
        {
            new WaveAudio.AudioDevice(1, "Built-in Microphone"),
            new WaveAudio.AudioDevice(2, "USB Audio Device"),
            new WaveAudio.AudioDevice(3, "UMIK-1 Gain: 18dB"),
        };
        Assert.Equal(3, RoomMeasureRunner.PickPreferredMicrophone(mics).Id);
        Assert.Equal(2, RoomMeasureRunner.PickPreferredMicrophone(mics[..2]).Id);
        Assert.Equal(1, RoomMeasureRunner.PickPreferredMicrophone(mics[..1]).Id);
    }
}
