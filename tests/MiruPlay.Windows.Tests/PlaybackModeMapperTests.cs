using MiruPlay.Windows.Models;
using MiruPlay.Windows.Services;

namespace MiruPlay.Windows.Tests;

public sealed class PlaybackModeMapperTests
{
    private static readonly string[] ExpectedHdrModes = ["auto", "passthrough", "sdr_tonemap"];
    private static readonly string[] DefaultPassthroughArguments =
        ["--ao=wasapi", "--audio-exclusive=yes", "--audio-spdif=truehd,eac3,ac3,dts,dts-hd"];

    [Fact]
    public void HdrModeValuesAndNormalizationAreStable()
    {
        Assert.Equal(ExpectedHdrModes, MpvPlaybackModeSettings.HdrModeValues);
        Assert.Equal("auto", MpvPlaybackModeSettings.NormalizeHdrMode(null));
        Assert.Equal("auto", MpvPlaybackModeSettings.NormalizeHdrMode("bogus"));
        Assert.Equal("passthrough", MpvPlaybackModeSettings.NormalizeHdrMode(" HDR_PASSTHROUGH "));
        Assert.Equal("sdr_tonemap", MpvPlaybackModeSettings.NormalizeHdrMode("sdr-tonemap"));
        Assert.Equal(MpvDisplayPipeline.HdrPassthrough, MpvPlaybackModeSettings.ToDisplayPipeline("passthrough"));
        Assert.Equal(MpvDisplayPipeline.SdrToneMap, MpvPlaybackModeSettings.ToDisplayPipeline("sdr_tonemap"));
        Assert.Equal(MpvDisplayPipeline.Auto, MpvPlaybackModeSettings.ToDisplayPipeline("unknown"));
    }

    [Fact]
    public void AudioPassthroughOffEmitsNoNativeOptions()
    {
        Assert.Empty(MpvAudioPassthroughOptionMapper.BuildArguments(MpvAudioPassthroughMode.Off));
        Assert.False(MpvAudioPassthroughOptionMapper.IsEnabled(MpvAudioPassthroughMode.Off));
    }

    [Fact]
    public void AudioPassthroughForceUsesExclusiveWasapiAndDefaultCodecSet()
    {
        var arguments = MpvAudioPassthroughOptionMapper.BuildArguments(MpvAudioPassthroughMode.Force);

        Assert.Equal(DefaultPassthroughArguments, arguments);
        Assert.DoesNotContain(arguments, argument => argument.StartsWith("--ao-wasapi-exclusive", StringComparison.Ordinal));
    }

    [Fact]
    public void CustomCodecSetIsCanonicalizedDedupedAndValidated()
    {
        var arguments = MpvAudioPassthroughOptionMapper.BuildArguments(
            MpvAudioPassthroughMode.Auto,
            "EAC3, truehd,bogus,truehd");

        Assert.Equal("--audio-spdif=truehd,eac3", arguments.Single(argument => argument.StartsWith("--audio-spdif=", StringComparison.Ordinal)));
        Assert.Equal("truehd,eac3", MpvAudioPassthroughOptionMapper.ToStorageCodecs("eac3,truehd,truehd"));
        Assert.Equal("truehd,eac3,ac3,dts,dts-hd", MpvAudioPassthroughOptionMapper.ToStorageCodecs(string.Empty));
    }

    [Fact]
    public void DeviceOptionOnlyEmitsForNonDefaultWaysapiEndpoints()
    {
        var auto = MpvAudioPassthroughOptionMapper.BuildArguments(MpvAudioPassthroughMode.Force, "truehd", "auto");
        Assert.DoesNotContain(auto, argument => argument.StartsWith("--audio-device=", StringComparison.Ordinal));

        var invalid = MpvAudioPassthroughOptionMapper.BuildArguments(MpvAudioPassthroughMode.Force, "truehd", "Speakers");
        Assert.DoesNotContain(invalid, argument => argument.StartsWith("--audio-device=", StringComparison.Ordinal));

        var endpoint = MpvAudioPassthroughOptionMapper.BuildArguments(
            MpvAudioPassthroughMode.Force,
            "truehd",
            "wasapi/{00000000-0000-0000-0000-000000000000}");
        Assert.Contains("--audio-device=wasapi/{00000000-0000-0000-0000-000000000000}", endpoint);
    }

    [Fact]
    public void PassthroughModeAndFormatParsingAreCaseInsensitive()
    {
        Assert.Equal(MpvAudioPassthroughMode.Off, MpvAudioPassthroughOptionMapper.ParseMode(null));
        Assert.Equal(MpvAudioPassthroughMode.Auto, MpvAudioPassthroughOptionMapper.ParseMode(" AUTO "));
        Assert.Equal(MpvAudioPassthroughMode.Force, MpvAudioPassthroughOptionMapper.ParseMode("force"));
        Assert.Equal("off", MpvAudioPassthroughOptionMapper.ToStorageMode(MpvAudioPassthroughMode.Off));
        Assert.Equal("auto", MpvAudioPassthroughOptionMapper.ToStorageMode(MpvAudioPassthroughMode.Auto));
        Assert.True(MpvAudioPassthroughOptionMapper.IsPassthroughFormat("spdif-truehd"));
        Assert.False(MpvAudioPassthroughOptionMapper.IsPassthroughFormat("floatp"));
        Assert.False(MpvAudioPassthroughOptionMapper.IsPassthroughFormat(null));
    }
}
