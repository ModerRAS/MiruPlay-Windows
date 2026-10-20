using MiruPlay.Windows.Models;
using MiruPlay.Windows.Services;

namespace MiruPlay.Windows.Tests;

public sealed class ToppingProtocolTests
{
    [Fact]
    public void Crc16ModbusMatchesTheStandardCheckVector()
    {
        Assert.Equal(0x4B37, ToppingProtocol.Crc16Modbus("123456789"u8.ToArray()));
    }

    [Fact]
    public void FrameLayoutMatchesTheHardwareConfirmedFormat()
    {
        var frame = ToppingProtocol.Frame(0x71, 0x02, 5);

        Assert.Equal(
            [0x22, 0x33, 0x20, 0x01, 0x01, 0x71, 0x02, 0x00, 0x00, 0x00, 0x05, 0x00, 0x00, 0x66, 0x77, 0x00],
            frame);
    }

    [Fact]
    public void PowerFrameCarriesARealChecksumInsideTheCrc()
    {
        var frame = ToppingProtocol.PowerFrame(true);

        Assert.Equal(0x00, frame[4]); // b4=0 participates in the checksum.
        var stored = (frame[11] << 8) | frame[12];
        Assert.NotEqual(0, stored);
        Assert.Equal(stored, ToppingProtocol.Crc16Modbus(frame[2..11]));
    }

    [Fact]
    public void VolumeFramesEncodeAttenuationSteps()
    {
        var frame = Assert.Single(ToppingProtocol.VolumeFrames(-3, 0.5));

        Assert.Equal([0x22, 0x33, 0x20, 0x01, 0x01, 0x71, 0x02, 0x00, 0x00, 0x00, 0x06, 0x00, 0x00, 0x66, 0x77, 0x00], frame);
    }

    [Fact]
    public void PreampScaleIsLinearGainInQ25()
    {
        Assert.Equal(33554432L, ToppingProtocol.DbToQ25(0));
        Assert.Equal(0, ToppingProtocol.Q25ToDb(33554432L), 9);
        Assert.Equal(4, ToppingProtocol.PreampFrames(0).Count);
    }

    [Fact]
    public void BandFramesCoverFiveParametersOnBothChannels()
    {
        var frames = ToppingProtocol.BandFrames(0, new ToppingProtocol.PeqBand(ToppingProtocol.TypePk, 1_000, -3, 1, true));

        Assert.Equal(10, frames.Count);
        Assert.Equal(0x91, frames[0][5]);
        Assert.Equal(1, frames[0][6]); // Left type.
        Assert.Equal(6, frames[5][6]); // Right type (1 + channel offset 5).
        Assert.Equal(7, frames[6][6]); // Right frequency.
    }

    [Fact]
    public void WireBytesFollowPerModelFraming()
    {
        var frame = ToppingProtocol.Frame(0x71, 0x02, 5);

        Assert.Equal(frame, ToppingProtocol.WireBytes(ToppingProtocol.Dx5II, frame));
        var d90 = ToppingProtocol.WireBytes(ToppingProtocol.D90III, frame);
        Assert.Equal(16, d90.Length);
        Assert.Equal(0x00, d90[0]);
        Assert.Equal(frame[..15], d90[1..]);
    }

    [Theory]
    [InlineData("TOPPING DX5 II", "dx5ii")]
    [InlineData("Topping DX5II", "dx5ii")]
    [InlineData("Topping D90III Discrete", "d90iii")]
    public void ProductStringDecidesTheModelNeverThePid(string product, string key)
    {
        Assert.Equal(key, ToppingProtocol.MatchProduct(product)?.Key);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Some Other DAC")]
    public void UnknownProductStringsNeverQualify(string? product)
    {
        Assert.Null(ToppingProtocol.MatchProduct(product));
    }

    [Fact]
    public void MapPresetPadsToElevenRegistersAndWarnsOnUnsupportedTypes()
    {
        var preset = new AudioDspPreset(
            "test",
            "Test",
            Rules:
            [
                new AudioDspChannelRule(Bands:
                [
                    new AudioDspBand(AudioDspFilterType.Peaking, 1_000, -3, 1),
                    new AudioDspBand(AudioDspFilterType.LowPass, 200, 0, 0.7),
                ]),
            ]);

        var mapping = ToppingProtocol.MapPreset(preset);

        Assert.Equal(11, mapping.Bands.Count);
        Assert.True(mapping.Bands[0].On);
        Assert.Equal(ToppingProtocol.TypePk, mapping.Bands[0].TypeCode);
        Assert.All(mapping.Bands.Skip(1), band => Assert.False(band.On));
        Assert.Null(mapping.PreampDb);
        Assert.Contains(mapping.Warnings, warning => warning.Contains("LowPass"));
    }

    [Fact]
    public void MapPresetKeepsNonzeroPreamp()
    {
        var mapping = ToppingProtocol.MapPreset(new AudioDspPreset(
            "test", "Test", PreampDb: -6,
            Rules: [new AudioDspChannelRule(Bands: [new AudioDspBand()])]));

        Assert.Equal(-6, mapping.PreampDb);
    }

    [Fact]
    public void ValidateRejectsNonsenseBeforeItReachesTheDsp()
    {
        var errors = ToppingProtocol.Validate(
            [new ToppingProtocol.PeqBand(9, 5, 99, 0, false)]);

        Assert.Equal(4, errors.Count);
    }
}
