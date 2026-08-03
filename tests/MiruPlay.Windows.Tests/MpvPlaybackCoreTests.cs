using System.Text.Json;
using MiruPlay.Windows.Models;
using MiruPlay.Windows.Services;

namespace MiruPlay.Windows.Tests;

public sealed class MpvPlaybackCoreTests
{
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
    public void HdrMapperKeepsBt2020PqOutputAndDoesNotAddSdrToneMapTarget()
    {
        var arguments = MpvWindowsVideoOptionMapper.BuildArguments(new MpvWindowsVideoOptions(
            MpvDisplayPipeline.HdrPassthrough,
            TargetHdrPeakNits: 50));

        Assert.Contains("--target-prim=bt.2020", arguments);
        Assert.Contains("--target-trc=pq", arguments);
        Assert.Contains("--target-peak=203", arguments);
        Assert.Contains("--tone-mapping=clip", arguments);
        Assert.DoesNotContain(arguments, argument => argument == "--target-trc=bt.1886");
    }

    [Fact]
    public void AudioParserIgnoresVideoAndPreservesSelectedExternalState()
    {
        using var document = JsonDocument.Parse("""
            [
              { "id": 1, "type": "video", "selected": true },
              { "id": 2, "type": "audio", "lang": "jpn", "title": "日本語", "codec": "aac", "selected": true },
              { "id": 3, "type": "audio", "lang": "eng", "codec": "opus", "external": true, "selected": false }
            ]
            """);

        var tracks = MpvPlaybackSession.ParseAudioTracks(document.RootElement);

        Assert.Equal(2, tracks.Count);
        Assert.Equal(2, tracks[0].Id);
        Assert.Equal("日本語", tracks[0].DisplayLabel);
        Assert.True(tracks[0].IsSelected);
        Assert.True(tracks[1].IsExternal);
        Assert.Equal("eng", tracks[1].Language);
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

    [Fact]
    public void CreateStartInfoAddsNativeWindowIdOnlyForEmbeddedPlayback()
    {
        var episode = Episode("embedded.mkv", 1, 1, Path.Combine(Path.GetTempPath(), "embedded.mkv"));
        var embedded = MpvPlayerLauncher.CreateStartInfo(
            "mpv.exe",
            "pipe",
            episode,
            new AppSettings(),
            null,
            windowHandle: new IntPtr(1234));
        var headless = MpvPlayerLauncher.CreateStartInfo(
            "mpv.exe",
            "pipe",
            episode,
            new AppSettings(),
            null,
            headless: true,
            windowHandle: new IntPtr(1234));

        Assert.Contains("--wid=1234", embedded.ArgumentList);
        Assert.Contains("--vo=gpu-next", embedded.ArgumentList);
        Assert.DoesNotContain("--wid=1234", headless.ArgumentList);
        Assert.Contains("--vo=null", headless.ArgumentList);
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
