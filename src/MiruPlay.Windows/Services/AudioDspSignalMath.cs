using MiruPlay.Windows.Models;

namespace MiruPlay.Windows.Services;

public sealed record BiquadCoefficients(
    double B0,
    double B1,
    double B2,
    double A1,
    double A2);

public sealed record AudioDspResponsePoint(double FrequencyHz, double MagnitudeDb);

public sealed record AudioDspChannelLayout(string Id, IReadOnlyList<string> Channels)
{
    public static AudioDspChannelLayout Mono { get; } = new("mono", ["FC"]);
    public static AudioDspChannelLayout Stereo { get; } = new("stereo", ["FL", "FR"]);
    public static AudioDspChannelLayout Surround51 { get; } =
        new("5.1", ["FL", "FR", "FC", "LFE", "SL", "SR"]);
    public static AudioDspChannelLayout Surround71 { get; } =
        new("7.1", ["FL", "FR", "FC", "LFE", "SL", "SR", "BL", "BR"]);

    public static AudioDspChannelLayout ForId(string? id) =>
        AudioDspStorage.NormalizeChannelLayoutId(id) switch
        {
            "mono" => Mono,
            "5.1" => Surround51,
            "7.1" => Surround71,
            _ => Stereo,
        };
}

public sealed class AudioDspChannelResponse
{
    private readonly Func<double, double> responseSampler;

    public AudioDspChannelResponse(
        string channelName,
        IReadOnlyList<AudioDspResponsePoint> samples,
        Func<double, double> responseSampler)
    {
        ChannelName = channelName;
        Samples = samples;
        this.responseSampler = responseSampler;
    }

    public string ChannelName { get; }
    public IReadOnlyList<AudioDspResponsePoint> Samples { get; }

    public double MagnitudeDbAt(double frequencyHz) => responseSampler(frequencyHz);
}

public sealed record LinearPhaseFirPlan(float[] Taps, int GroupDelaySamples);

public static class AudioDspSignalMath
{
    private const int ResponseSampleCount = 257;
    private const double MinimumMagnitude = 1e-12;
    private static readonly double Minus3DbAmplitude = Math.Sqrt(0.5);

    public static BiquadCoefficients DesignBiquad(AudioDspBand band, int sampleRateHz)
    {
        ArgumentNullException.ThrowIfNull(band);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRateHz);

        var normalized = band.Normalize();
        var frequencyHz = Math.Clamp(
            normalized.FrequencyHz,
            AudioDspBand.MinFrequencyHz,
            sampleRateHz * 0.5 * (1 - 1e-9));
        var omega = 2 * Math.PI * frequencyHz / sampleRateHz;
        var sine = Math.Sin(omega);
        var cosine = Math.Cos(omega);
        var alpha = sine / (2 * normalized.Q);
        var amplitude = Math.Pow(10, normalized.GainDb / 40);

        return normalized.Type switch
        {
            AudioDspFilterType.LowShelf => Normalize(
                amplitude * ((amplitude + 1) - (amplitude - 1) * cosine + 2 * Math.Sqrt(amplitude) * alpha),
                2 * amplitude * ((amplitude - 1) - (amplitude + 1) * cosine),
                amplitude * ((amplitude + 1) - (amplitude - 1) * cosine - 2 * Math.Sqrt(amplitude) * alpha),
                (amplitude + 1) + (amplitude - 1) * cosine + 2 * Math.Sqrt(amplitude) * alpha,
                -2 * ((amplitude - 1) + (amplitude + 1) * cosine),
                (amplitude + 1) + (amplitude - 1) * cosine - 2 * Math.Sqrt(amplitude) * alpha),
            AudioDspFilterType.HighShelf => Normalize(
                amplitude * ((amplitude + 1) + (amplitude - 1) * cosine + 2 * Math.Sqrt(amplitude) * alpha),
                -2 * amplitude * ((amplitude - 1) + (amplitude + 1) * cosine),
                amplitude * ((amplitude + 1) + (amplitude - 1) * cosine - 2 * Math.Sqrt(amplitude) * alpha),
                (amplitude + 1) - (amplitude - 1) * cosine + 2 * Math.Sqrt(amplitude) * alpha,
                2 * ((amplitude - 1) - (amplitude + 1) * cosine),
                (amplitude + 1) - (amplitude - 1) * cosine - 2 * Math.Sqrt(amplitude) * alpha),
            AudioDspFilterType.LowPass => Normalize(
                (1 - cosine) / 2,
                1 - cosine,
                (1 - cosine) / 2,
                1 + alpha,
                -2 * cosine,
                1 - alpha),
            AudioDspFilterType.HighPass => Normalize(
                (1 + cosine) / 2,
                -(1 + cosine),
                (1 + cosine) / 2,
                1 + alpha,
                -2 * cosine,
                1 - alpha),
            AudioDspFilterType.Notch => Normalize(
                1,
                -2 * cosine,
                1,
                1 + alpha,
                -2 * cosine,
                1 - alpha),
            AudioDspFilterType.BandPass => Normalize(
                alpha,
                0,
                -alpha,
                1 + alpha,
                -2 * cosine,
                1 - alpha),
            _ => Normalize(
                1 + alpha * amplitude,
                -2 * cosine,
                1 - alpha * amplitude,
                1 + alpha / amplitude,
                -2 * cosine,
                1 - alpha / amplitude),
        };
    }

    public static IReadOnlyList<AudioDspChannelResponse> SampleChannels(
        AudioDspPreset preset,
        AudioDspChannelLayout layout,
        int sampleRateHz)
    {
        ArgumentNullException.ThrowIfNull(preset);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRateHz);

        var normalized = preset.Normalize();
        var responses = new List<AudioDspChannelResponse>(layout.Channels.Count);
        foreach (var channel in layout.Channels)
        {
            var rule = ResolveRule(normalized.Rules ?? [], layout, channel);
            var coefficients = (rule?.Bands ?? [])
                .Where(band => band.Enabled)
                .Select(band => DesignBiquad(band, sampleRateHz))
                .ToArray();
            var fixedGainDb = rule?.OutputGainDb ?? 0;
            double Sampler(double frequencyHz) =>
                fixedGainDb + coefficients.Sum(coefficient =>
                    MagnitudeDb(coefficient, frequencyHz, sampleRateHz));

            responses.Add(new AudioDspChannelResponse(
                channel,
                SampleResponse(Sampler, sampleRateHz),
                Sampler));
        }

        return responses;
    }

    public static IReadOnlyList<LinearPhaseFirPlan> BuildLinearPhaseChannels(
        AudioDspPreset preset,
        AudioDspChannelLayout layout,
        int sampleRateHz)
    {
        var normalized = preset.Normalize();
        var taps = (int)normalized.FirQuality;
        var responses = SampleChannels(normalized, layout, sampleRateHz);
        var frequencyBinCount = taps / 2 + 1;

        return responses.Select(response =>
        {
            var magnitudeDb = Enumerable.Range(0, frequencyBinCount)
                .Select(index => response.MagnitudeDbAt(
                    index * sampleRateHz * 0.5 / (frequencyBinCount - 1)))
                .ToArray();
            return LinearPhaseFirDesigner.Design(magnitudeDb, sampleRateHz, taps);
        }).ToArray();
    }

    public static IReadOnlyList<string> ResolveTarget(
        AudioDspChannelTarget target,
        AudioDspChannelLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);

        IEnumerable<string> candidates = target switch
        {
            AudioDspChannelTarget.All => layout.Channels,
            AudioDspChannelTarget.Front => ["FL", "FR"],
            AudioDspChannelTarget.CenterLfe => ["FC", "LFE"],
            AudioDspChannelTarget.Surround => ["SL", "SR", "BL", "BR"],
            AudioDspChannelTarget.Surround51 when layout.Id == AudioDspChannelLayout.Surround51.Id =>
                layout.Channels,
            AudioDspChannelTarget.Surround71 when layout.Id == AudioDspChannelLayout.Surround71.Id =>
                layout.Channels,
            AudioDspChannelTarget.Left => ["FL"],
            AudioDspChannelTarget.Right => ["FR"],
            AudioDspChannelTarget.Center => ["FC"],
            AudioDspChannelTarget.Lfe => ["LFE"],
            AudioDspChannelTarget.LeftSurround => ["SL", "BL"],
            AudioDspChannelTarget.RightSurround => ["SR", "BR"],
            _ => [],
        };
        var channelSet = layout.Channels.ToHashSet(StringComparer.Ordinal);
        return candidates.Where(channelSet.Contains).ToArray();
    }

    public static double[,] BuildStereoDownmixMatrix(AudioDspChannelLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        var matrix = new double[2, layout.Channels.Count];
        for (var input = 0; input < layout.Channels.Count; input++)
        {
            switch (layout.Channels[input])
            {
                case "FL":
                    matrix[0, input] = 1;
                    break;
                case "FR":
                    matrix[1, input] = 1;
                    break;
                case "FC" when layout.Id == AudioDspChannelLayout.Mono.Id:
                    matrix[0, input] = Minus3DbAmplitude;
                    matrix[1, input] = Minus3DbAmplitude;
                    break;
                case "FC":
                    matrix[0, input] = Minus3DbAmplitude;
                    matrix[1, input] = Minus3DbAmplitude;
                    break;
                case "LFE":
                    matrix[0, input] = 0.5;
                    matrix[1, input] = 0.5;
                    break;
                case "SL":
                case "BL":
                    matrix[0, input] = Minus3DbAmplitude;
                    break;
                case "SR":
                case "BR":
                    matrix[1, input] = Minus3DbAmplitude;
                    break;
            }
        }

        return matrix;
    }

    public static string ResolveOutputRoute(AudioDspOutputMode outputMode) => outputMode switch
    {
        AudioDspOutputMode.StereoDownmix => "stereo-downmix",
        AudioDspOutputMode.HrtfBinaural => "hrtf",
        _ => "preserve",
    };

    private static AudioDspChannelRule? ResolveRule(
        IReadOnlyList<AudioDspChannelRule> rules,
        AudioDspChannelLayout layout,
        string channel)
    {
        return rules
            .Select((rule, index) => new
            {
                Rule = rule,
                Index = index,
                Matches = ResolveTarget(rule.Target, layout).Contains(channel, StringComparer.Ordinal),
                Specificity = TargetSpecificity(rule.Target),
            })
            .Where(candidate => candidate.Matches)
            .OrderByDescending(candidate => candidate.Specificity)
            .ThenByDescending(candidate => candidate.Index)
            .Select(candidate => candidate.Rule)
            .FirstOrDefault();
    }

    private static int TargetSpecificity(AudioDspChannelTarget target) => target switch
    {
        AudioDspChannelTarget.Left or
        AudioDspChannelTarget.Right or
        AudioDspChannelTarget.Center or
        AudioDspChannelTarget.Lfe => 500,
        AudioDspChannelTarget.Front or
        AudioDspChannelTarget.CenterLfe or
        AudioDspChannelTarget.LeftSurround or
        AudioDspChannelTarget.RightSurround => 400,
        AudioDspChannelTarget.Surround => 300,
        AudioDspChannelTarget.Surround51 or AudioDspChannelTarget.Surround71 => 200,
        AudioDspChannelTarget.All => 100,
        _ => 0,
    };

    private static AudioDspResponsePoint[] SampleResponse(
        Func<double, double> sampler,
        int sampleRateHz)
    {
        var maximumFrequencyHz = sampleRateHz * 0.5;
        var minimumFrequencyHz = Math.Min(AudioDspBand.MinFrequencyHz, maximumFrequencyHz);
        var points = new AudioDspResponsePoint[ResponseSampleCount];
        points[0] = new(0, sampler(0));
        for (var index = 1; index < ResponseSampleCount; index++)
        {
            var position = (double)(index - 1) / (ResponseSampleCount - 2);
            var frequencyHz = minimumFrequencyHz *
                Math.Pow(maximumFrequencyHz / minimumFrequencyHz, position);
            points[index] = new(frequencyHz, sampler(frequencyHz));
        }

        return points;
    }

    private static double MagnitudeDb(
        BiquadCoefficients coefficients,
        double frequencyHz,
        int sampleRateHz)
    {
        var frequency = double.IsFinite(frequencyHz)
            ? Math.Clamp(frequencyHz, 0, sampleRateHz * 0.5)
            : 0;
        var omega = 2 * Math.PI * frequency / sampleRateHz;
        var cosine1 = Math.Cos(omega);
        var sine1 = -Math.Sin(omega);
        var cosine2 = Math.Cos(2 * omega);
        var sine2 = -Math.Sin(2 * omega);
        var numeratorReal = coefficients.B0 + coefficients.B1 * cosine1 + coefficients.B2 * cosine2;
        var numeratorImaginary = coefficients.B1 * sine1 + coefficients.B2 * sine2;
        var denominatorReal = 1 + coefficients.A1 * cosine1 + coefficients.A2 * cosine2;
        var denominatorImaginary = coefficients.A1 * sine1 + coefficients.A2 * sine2;
        var numerator = Math.Sqrt(numeratorReal * numeratorReal + numeratorImaginary * numeratorImaginary);
        var denominator = Math.Max(
            MinimumMagnitude,
            Math.Sqrt(denominatorReal * denominatorReal + denominatorImaginary * denominatorImaginary));
        var magnitude = Math.Max(MinimumMagnitude, numerator / denominator);
        return 20 * Math.Log10(magnitude);
    }

    private static BiquadCoefficients Normalize(
        double b0,
        double b1,
        double b2,
        double a0,
        double a1,
        double a2) => new(b0 / a0, b1 / a0, b2 / a0, a1 / a0, a2 / a0);
}

public static class LinearPhaseFirDesigner
{
    public static LinearPhaseFirPlan Design(
        IReadOnlyList<double> magnitudeDb,
        int sampleRateHz,
        int taps)
    {
        ArgumentNullException.ThrowIfNull(magnitudeDb);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRateHz);
        if (magnitudeDb.Count == 0)
            throw new ArgumentException("At least one magnitude sample is required.", nameof(magnitudeDb));
        if (taps < 2)
            throw new ArgumentOutOfRangeException(nameof(taps), "At least two taps are required.");

        var half = taps / 2;
        var frequencyMagnitudes = new double[half + 1];
        for (var index = 0; index <= half; index++)
        {
            var sourcePosition = half == 0
                ? 0
                : (double)index * (magnitudeDb.Count - 1) / half;
            var lower = (int)Math.Floor(sourcePosition);
            var upper = Math.Min(lower + 1, magnitudeDb.Count - 1);
            var fraction = sourcePosition - lower;
            var db = magnitudeDb[lower] + (magnitudeDb[upper] - magnitudeDb[lower]) * fraction;
            frequencyMagnitudes[index] = Math.Pow(10, Math.Clamp(db, -240, 240) / 20);
        }

        var coefficients = new double[taps];
        var center = (taps - 1) / 2d;
        for (var tap = 0; tap < taps; tap++)
        {
            var offset = tap - center;
            var sum = frequencyMagnitudes[0];
            var pairedLimit = taps % 2 == 0 ? half - 1 : half;
            for (var bin = 1; bin <= pairedLimit; bin++)
                sum += 2 * frequencyMagnitudes[bin] *
                    Math.Cos(2 * Math.PI * bin * offset / taps);
            if (taps % 2 == 0)
                sum += frequencyMagnitudes[half] * Math.Cos(Math.PI * offset);

            var window = 0.54 - 0.46 * Math.Cos(2 * Math.PI * tap / (taps - 1));
            coefficients[tap] = sum / taps * window;
        }

        ForceSymmetry(coefficients);
        var coefficientSum = coefficients.Sum();
        if (Math.Abs(coefficientSum) > 1e-20)
        {
            var scale = frequencyMagnitudes[0] / coefficientSum;
            for (var index = 0; index < coefficients.Length; index++)
                coefficients[index] *= scale;
        }

        var result = new float[taps];
        for (var index = 0; index < (taps + 1) / 2; index++)
        {
            var mirror = taps - 1 - index;
            var value = (float)((coefficients[index] + coefficients[mirror]) * 0.5);
            result[index] = value;
            result[mirror] = value;
        }

        return new LinearPhaseFirPlan(result, (taps - 1) / 2);
    }

    private static void ForceSymmetry(double[] coefficients)
    {
        for (var index = 0; index < coefficients.Length / 2; index++)
        {
            var mirror = coefficients.Length - 1 - index;
            var average = (coefficients[index] + coefficients[mirror]) * 0.5;
            coefficients[index] = average;
            coefficients[mirror] = average;
        }
    }
}
