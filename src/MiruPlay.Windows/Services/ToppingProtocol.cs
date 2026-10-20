using MiruPlay.Windows.Models;

namespace MiruPlay.Windows.Services;

/// <summary>
/// Topping DAC USB HID control protocol, ported from the Android
/// implementation (register map hardware-confirmed on DX5 II for
/// volume/gain/PEQ/preamp and on D90 III Discrete for PEQ writes).
/// Frame: 22 33 &lt;op&gt; 01 &lt;b4&gt; &lt;reg&gt; &lt;sub&gt; &lt;int32 BE&gt; &lt;crc16 hi lo&gt; 66 77 00.
/// CRC is CRC-16/MODBUS over bytes [2..10], big-endian.
/// </summary>
public static class ToppingProtocol
{
    public const int VendorId = 0x152A; // Thesycon (XMOS stack) — PID does NOT imply Topping.
    public const int ProductId = 0x8750; // Shared by many Topping models; match on product string.

    public const int RegCtrl = 0x71;
    public const int SubPower = 0x01;
    public const int SubVolume = 0x02;
    public const int SubGain = 0x17;
    public const int SubCommit = 0x34; // Vendor name: Heartbeat.

    public const int PeqFirst = 0x91;
    /// <summary>11 band registers exist; 0x9B is inert but still written so a stale
    /// band left by the vendor app gets cleared.</summary>
    public const int PeqRegisterCount = 11;
    public const int RegPreamp = 0x9C;
    private const long PreampScale = 33554432L; // 2^25 — Q25 fixed-point scale.

    private const int SubType = 1;
    private const int SubFreq = 2;
    private const int SubGainSub = 3;
    private const int SubQ = 4;
    private const int SubOn = 5;
    private const int ChannelOffset = 5;

    public const int TypePk = 1;
    public const int TypeLs = 4;
    public const int TypeHs = 5;

    public const double VolumeMinDb = -99.0;
    public const double VolumeMaxDb = 0.0;
    /// <summary>Above this needs an explicit user confirmation.</summary>
    public const double VolumeWarnDb = -10.0;

    public const double PreampMinDb = -40.0;
    public const double PreampMaxDb = 10.0;

    /// <summary>An unused band as the vendor app writes it: 632 Hz factory default, disabled.</summary>
    public static PeqBand DisabledBand { get; } = new(TypePk, 632, 0, 0.707, false);

    public sealed record PeqBand(int TypeCode, int FreqHz, double GainDb, double Q, bool On);

    /// <summary>Register map + framing + which controls this model actually has.</summary>
    public sealed record DeviceSpec(
        string Key,
        string Name,
        int ProductId,
        IReadOnlyList<string> ProductMatch,
        /// <summary>True only after writes were verified to move the front panel on this model.</summary>
        bool Confirmed,
        /// <summary>D90 III framing: report id 0 + first 15 bytes; DX5 II sends the raw 16-byte frame.</summary>
        bool ReportIdPrefix,
        /// <summary>False when volume is knob/remote only (D90 III: not on the HID interface at all).</summary>
        bool HidVolume);

    public static DeviceSpec Dx5II { get; } = new(
        "dx5ii", "Topping DX5 II", 0x8750,
        ["DX5II", "DX52", "DX5"],
        Confirmed: true, ReportIdPrefix: false, HidVolume: true);

    public static DeviceSpec D90III { get; } = new(
        "d90iii", "Topping D90 III Discrete", 0x8750,
        ["D90III", "D90IIIDISCRETE", "D90"],
        Confirmed: false, ReportIdPrefix: true, HidVolume: false);

    public static IReadOnlyList<DeviceSpec> Devices { get; } = [Dx5II, D90III];

    /// <summary>Match a USB product string against a spec. The PID is shared across
    /// models whose registers collide, so the product string decides.</summary>
    public static DeviceSpec? MatchProduct(string? productName) =>
        string.IsNullOrWhiteSpace(productName)
            ? null
            : Devices.FirstOrDefault(spec =>
            {
                var product = new string(productName.ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray());
                return spec.ProductMatch.Any(fragment => product.StartsWith(fragment, StringComparison.Ordinal) || product.Contains(fragment, StringComparison.Ordinal));
            });

    /// <summary>CRC-16/MODBUS: poly 0xA001 reflected, init 0xFFFF, no final XOR.</summary>
    public static int Crc16Modbus(byte[] data)
    {
        var crc = 0xFFFF;
        foreach (var b in data)
        {
            crc ^= b;
            for (var i = 0; i < 8; i++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xA001 : crc >> 1;
            crc &= 0xFFFF;
        }
        return crc;
    }

    public static byte[] Frame(int reg, int sub, long value, int opcode = 0x20, int b4 = 0x01, bool crc = false)
    {
        var v = (int)(value & 0xFFFFFFFFL);
        var frame = new byte[]
        {
            0x22, 0x33, (byte)opcode, 0x01, (byte)b4, (byte)reg, (byte)sub,
            (byte)((v >> 24) & 0xFF), (byte)((v >> 16) & 0xFF),
            (byte)((v >> 8) & 0xFF), (byte)(v & 0xFF),
            0, 0, 0x66, 0x77, 0x00,
        };
        if (crc)
        {
            var checksum = Crc16Modbus(frame[2..11]);
            frame[11] = (byte)((checksum >> 8) & 0xFF); // Stored HIGH byte first — reverse of Modbus framing.
            frame[12] = (byte)(checksum & 0xFF);
        }
        return frame;
    }

    /// <summary>All 10 frames for one band: five parameters x two channels.</summary>
    public static IReadOnlyList<byte[]> BandFrames(int index, PeqBand band)
    {
        var reg = PeqFirst + index;
        var gainTenths = (long)Math.Round(band.GainDb * 10);
        var qScaled = (long)Math.Round(band.Q * 10_000);
        var frames = new List<byte[]>(10);
        foreach (var channel in new[] { 0, ChannelOffset })
        {
            frames.Add(Frame(reg, SubType + channel, band.TypeCode));
            frames.Add(Frame(reg, SubFreq + channel, band.FreqHz));
            frames.Add(Frame(reg, SubGainSub + channel, gainTenths));
            frames.Add(Frame(reg, SubQ + channel, qScaled));
            frames.Add(Frame(reg, SubOn + channel, band.On ? 1L : 0L));
        }
        return frames;
    }

    /// <summary>Both channels, matching the vendor app's 9c 01..04 sequence.</summary>
    public static IReadOnlyList<byte[]> PreampFrames(double db)
    {
        var value = DbToQ25(db);
        return [Frame(RegPreamp, 0x01, value), Frame(RegPreamp, 0x02, 1), Frame(RegPreamp, 0x03, value), Frame(RegPreamp, 0x04, 1)];
    }

    /// <summary>Preamp is LINEAR gain in Q25 fixed point: value = 10^(dB/20) · 2^25.</summary>
    public static long DbToQ25(double db) => (long)Math.Round(Math.Pow(10, db / 20) * PreampScale);

    public static double Q25ToDb(long value) => value > 0 ? 20 * Math.Log10((double)value / PreampScale) : double.NegativeInfinity;

    public static IReadOnlyList<byte[]> VolumeFrames(double db, double stepDb = 0.5)
    {
        var steps = (long)Math.Round(-db / stepDb);
        return [Frame(RegCtrl, SubVolume, steps)];
    }

    public static byte[] GainFrame(bool on) => Frame(RegCtrl, SubGain, on ? 1L : 0L);

    /// <summary>Power needs a real checksum (b4=0, inside the CRC).</summary>
    public static byte[] PowerFrame(bool on) => Frame(RegCtrl, SubPower, on ? 1L : 0L, b4: 0x00, crc: true);

    public static byte[] CommitFrame() => Frame(RegCtrl, SubCommit, 1);

    /// <summary>Bytes as they go on the wire: D90 III needs report id 0 + 15-byte payload.</summary>
    public static byte[] WireBytes(DeviceSpec spec, byte[] frame) =>
        spec.ReportIdPrefix ? [.. new byte[] { 0x00 }, .. frame[..15]] : frame;

    public sealed record PeqMapping(IReadOnlyList<PeqBand> Bands, double? PreampDb, IReadOnlyList<string> Warnings);

    /// <summary>
    /// Map an in-app <see cref="AudioDspPreset"/> to device PEQ bands. Only PK / LS / HS are
    /// confirmed on the hardware; other filter types become warnings, never silent wrong curves.
    /// </summary>
    public static PeqMapping MapPreset(AudioDspPreset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        var warnings = new List<string>();
        var usable = new List<PeqBand>();
        foreach (var band in (preset.Rules ?? []).SelectMany(rule => rule.Bands ?? []).Where(band => band.Enabled))
        {
            int? code = band.Type switch
            {
                AudioDspFilterType.Peaking => TypePk,
                AudioDspFilterType.LowShelf => TypeLs,
                AudioDspFilterType.HighShelf => TypeHs,
                _ => null,
            };
            if (code is null)
            {
                warnings.Add($"滤波类型 {band.Type} @ {(int)band.FrequencyHz} Hz 设备不支持，已跳过");
                continue;
            }
            if (usable.Count >= 10)
            {
                warnings.Add("预设超过 10 段，多余的滤波被忽略");
                break;
            }
            usable.Add(new PeqBand(code.Value, (int)band.FrequencyHz, band.GainDb, band.Q, true));
        }
        warnings.AddRange(Validate(usable));
        // All 11 registers are written so nothing stale survives underneath.
        var padded = usable.Concat(Enumerable.Repeat(DisabledBand, PeqRegisterCount - usable.Count)).ToList();
        return new PeqMapping(padded, Math.Abs(preset.PreampDb) < 1e-9 ? null : preset.PreampDb, warnings);
    }

    /// <summary>Reject nonsense before it reaches the DSP.</summary>
    public static IReadOnlyList<string> Validate(IReadOnlyList<PeqBand> bands)
    {
        var errors = new List<string>();
        for (var i = 0; i < bands.Count; i++)
        {
            var band = bands[i];
            if (band.TypeCode is not (TypePk or TypeLs or TypeHs)) errors.Add($"滤波 {i + 1}: 不支持的类型代码 {band.TypeCode}");
            if (band.FreqHz is < 10 or > 22_000) errors.Add($"滤波 {i + 1}: 频率 {band.FreqHz} Hz 超出范围");
            if (band.GainDb is < -40 or > 40) errors.Add($"滤波 {i + 1}: 增益 {band.GainDb} dB 超出范围");
            if (band.Q is < 0.01 or > 100) errors.Add($"滤波 {i + 1}: Q {band.Q} 超出范围");
        }
        return errors;
    }
}
