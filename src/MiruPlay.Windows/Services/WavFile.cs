namespace MiruPlay.Windows.Services;

/// <summary>
/// Minimal RIFF/WAVE 16-bit PCM mono reader/writer for offline measurement
/// import. RIFF is LITTLE-ENDIAN throughout; samples are doubles in [-1, 1].
/// </summary>
public static class WavFile
{
    private const int MaxSamples = 48_000 * 60 * 5;

    public static void Write16BitMono(string path, double[] samples, int sampleRateHz)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRateHz);
        if (samples.Length > MaxSamples) throw new ArgumentException(AppStrings.Get("wav.tooLong"));
        using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var writer = new BinaryWriter(output, System.Text.Encoding.ASCII, leaveOpen: false);
        var dataSize = samples.Length * 2;
        writer.Write("RIFF".ToCharArray());
        writer.Write(36 + dataSize);
        writer.Write("WAVE".ToCharArray());
        writer.Write("fmt ".ToCharArray());
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(sampleRateHz);
        writer.Write(sampleRateHz * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data".ToCharArray());
        writer.Write(dataSize);
        foreach (var value in samples)
        {
            var clamped = Math.Clamp(value, -1.0, 1.0);
            writer.Write((short)Math.Round(clamped * 32767));
        }
    }

    public sealed record WavData(double[] Samples, int SampleRateHz);

    public static WavData Read16BitMono(string path) =>
        Read16BitMono(File.ReadAllBytes(path));

    public static WavData Read16BitMono(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        using var input = new MemoryStream(bytes, writable: false);
        using var reader = new BinaryReader(input, System.Text.Encoding.ASCII, leaveOpen: false);
        bool ReadTag(string tag)
        {
            var got = reader.ReadChars(4);
            return got.Length == 4 && new string(got) == tag;
        }
        if (!ReadTag("RIFF")) throw new InvalidDataException("not a RIFF file");
        _ = reader.ReadInt32();
        if (!ReadTag("WAVE")) throw new InvalidDataException("not a WAVE file");

        var formatCode = -1;
        var channels = -1;
        var sampleRate = -1;
        double[]? samples = null;
        while (samples is null)
        {
            if (input.Position + 8 > input.Length) throw new InvalidDataException("data chunk not found");
            var id = new string(reader.ReadChars(4));
            var size = reader.ReadInt32();
            if (size < 0) throw new InvalidDataException("malformed chunk size");
            switch (id)
            {
                case "fmt ":
                    if (size < 16) throw new InvalidDataException($"unexpected fmt chunk size {size}");
                    formatCode = reader.ReadInt16();
                    channels = reader.ReadInt16();
                    sampleRate = reader.ReadInt32();
                    _ = reader.ReadInt32();
                    _ = reader.ReadInt16();
                    var bits = reader.ReadInt16();
                    if (formatCode != 1 || bits != 16)
                        throw new InvalidDataException($"only 16-bit PCM supported, got fmt={formatCode} bits={bits}");
                    input.Position += Math.Max(0, size - 16);
                    break;
                case "data":
                    if (channels <= 0) throw new InvalidDataException("fmt chunk missing");
                    if (size / 2 / channels > MaxSamples) throw new InvalidDataException(AppStrings.Get("wav.tooLong"));
                    var frames = size / 2 / channels;
                    samples = new double[frames];
                    for (var i = 0; i < frames; i++)
                    {
                        var acc = 0.0;
                        for (var c = 0; c < channels; c++) acc += reader.ReadInt16() / 32768.0;
                        samples[i] = acc / channels;
                    }
                    if ((size & 1) != 0 && input.Position < input.Length) _ = reader.ReadByte();
                    break;
                default:
                    input.Position += size;
                    break;
            }
        }
        return new WavData(samples, sampleRate);
    }
}
