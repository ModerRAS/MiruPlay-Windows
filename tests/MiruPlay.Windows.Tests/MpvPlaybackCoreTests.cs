using MiruPlay.Windows.Models;
using MiruPlay.Windows.Services;

namespace MiruPlay.Windows.Tests;

public sealed class MpvPlaybackCoreTests
{
    private static readonly string[] DefaultArguments = ["--vo=gpu-next", "--gpu-api=d3d11", "--hwdec=auto-safe"];

    [Fact]
    public void SdrMapperUsesWindowsD3d11ToneMappingAndClampsTarget()
    {
        var arguments = MpvWindowsVideoOptionMapper.BuildArguments(new MpvWindowsVideoOptions(
            MpvDisplayPipeline.SdrToneMap,
            TargetSdrNits: 20,
            ToneMappingCurve: MpvToneMappingCurve.Reinhard,
            ComputeHdrPeak: false));

        Assert.Contains("--vo=gpu-next", arguments);
        Assert.Contains("--gpu-api=d3d11", arguments);
        Assert.Contains("--hwdec=auto-safe", arguments);
        Assert.Contains("--target-prim=bt.709", arguments);
        Assert.Contains("--target-trc=bt.1886", arguments);
        Assert.Contains("--target-peak=80", arguments);
        Assert.Contains("--tone-mapping=reinhard", arguments);
        Assert.Contains("--tone-mapping-param=0.5", arguments);
        Assert.Contains("--hdr-compute-peak=no", arguments);
    }

    [Fact]
    public void HdrPassthroughSignalsSourceColorspaceWithoutForcingTargetPrimaries()
    {
        var arguments = MpvWindowsVideoOptionMapper.BuildArguments(new MpvWindowsVideoOptions(
            MpvDisplayPipeline.HdrPassthrough,
            TargetHdrPeakNits: 50));

        Assert.Contains("--target-colorspace-hint=yes", arguments);
        Assert.Contains("--target-colorspace-hint-mode=source", arguments);
        Assert.Contains("--target-peak=10000", arguments);
        Assert.Contains("--tone-mapping=clip", arguments);
        Assert.Contains("--hdr-compute-peak=no", arguments);
        Assert.DoesNotContain(arguments, argument => argument.StartsWith("--target-prim", StringComparison.Ordinal));
        Assert.DoesNotContain(arguments, argument => argument.StartsWith("--target-trc", StringComparison.Ordinal));
        Assert.DoesNotContain(arguments, argument => argument.Contains("--tone-mapping-param", StringComparison.Ordinal));
    }

    [Fact]
    public void DefaultHdrModeKeepsMpvDefaultsAndAddsNoColorOptions()
    {
        var arguments = MpvWindowsVideoOptionMapper.BuildArguments(new MpvWindowsVideoOptions());

        Assert.Equal(DefaultArguments, arguments);
    }

    [Fact]
    public void SettingsHdrModeMapsToDisplayPipeline()
    {
        Assert.Equal(
            MpvDisplayPipeline.Auto,
            MpvWindowsVideoOptionMapper.FromSettings(new AppSettings()).DisplayPipeline);
        Assert.Equal(
            MpvDisplayPipeline.HdrPassthrough,
            MpvWindowsVideoOptionMapper.FromSettings(new AppSettings(HdrMode: "PASSTHROUGH")).DisplayPipeline);
        Assert.Equal(
            MpvDisplayPipeline.SdrToneMap,
            MpvWindowsVideoOptionMapper.FromSettings(new AppSettings(HdrMode: "sdr-tonemap")).DisplayPipeline);
    }

    [Fact]
    public void VersionQueueGroupsLogicalEpisodesAndChoosesNearestVersion()
    {
        var episodes = new[]
        {
            Episode("s1e2-web", 1, 2, @"web\02.mkv"),
            Episode("s1e1", 1, 1, @"web\01.mkv"),
            Episode("s1e2-bd", 1, 2, @"bd\02.mkv"),
            Episode("s2e1", 2, 1, @"web\01.mkv"),
        };
        var queue = new MpvPlaybackQueue(episodes, "s1e2-web");

        Assert.Equal(3, queue.Entries.Count);
        Assert.True(queue.CanPlayPrevious);
        Assert.True(queue.CanPlayNext);
        Assert.Equal("s1e1", queue.Previous()!.ProgressKey);
        Assert.Equal("s1e2-web", queue.Next()!.ProgressKey);
        Assert.Equal("s1e2-bd", queue.SelectVersion(@"bd\02.mkv")!.ProgressKey);
        Assert.Equal("s1e2-bd", queue.CurrentVersion!.ProgressKey);
        Assert.Equal("s2e1", queue.Next()!.ProgressKey);
        Assert.Null(queue.Next());
    }

    private static LibraryEpisode Episode(string key, int season, double number, string path) => new(
        1,
        key,
        key,
        season,
        number,
        number,
        key,
        path,
        TimeSpan.Zero,
        []);
}
