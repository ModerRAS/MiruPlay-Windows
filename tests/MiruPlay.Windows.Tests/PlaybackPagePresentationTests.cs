using MiruPlay.Windows.Models;
using MiruPlay.Windows.Services;

namespace MiruPlay.Windows.Tests;

public sealed class PlaybackPagePresentationTests
{
    private static readonly string[] TrackOptions = ["track"];
    private static readonly string[] ExpectedProgressKeys = ["bd", "web", "next"];

    [Fact]
    public void FormatTimeUsesMinuteAndHourForms()
    {
        Assert.Equal("0:00", PlaybackPagePresentation.FormatTime(0));
        Assert.Equal("1:05", PlaybackPagePresentation.FormatTime(65_000));
        Assert.Equal("1:01:01", PlaybackPagePresentation.FormatTime(3_661_000));
    }

    [Fact]
    public void QueueCapabilitiesMirrorPlaybackQueue()
    {
        var first = Episode("first", 1);
        var second = Episode("second", 2);
        var queue = new MpvPlaybackQueue([first, second], first.ProgressKey);

        Assert.Equal(new PlaybackQueueCapabilities(false, true), PlaybackPagePresentation.GetCapabilities(queue));

        queue.Next();

        Assert.Equal(new PlaybackQueueCapabilities(true, false), PlaybackPagePresentation.GetCapabilities(queue));
    }

    [Fact]
    public void EmptyTrackListsRemainDisabledOptions()
    {
        Assert.False(PlaybackPagePresentation.HasTrackOptions(Array.Empty<string>()));
        Assert.True(PlaybackPagePresentation.HasTrackOptions(TrackOptions));
    }

    [Fact]
    public void QueueEpisodeOptionsKeepEveryVersion()
    {
        var web = Episode("web", 1, "web\\01.mkv");
        var bd = Episode("bd", 1, "bd\\01.mkv");
        var next = Episode("next", 2, "web\\02.mkv");
        var queue = new MpvPlaybackQueue([web, bd, next], web.ProgressKey);

        var options = PlaybackPagePresentation.BuildEpisodeOptions(queue, web);

        Assert.Equal(3, options.Count);
        Assert.Equal(ExpectedProgressKeys, options.Select(option => option.Episode.ProgressKey));
        Assert.Contains(options, option => option.IsCurrent && option.Episode.ProgressKey == web.ProgressKey);
    }

    [Theory]
    [InlineData(839, false, PlaybackLayoutMode.Compact, false)]
    [InlineData(840, false, PlaybackLayoutMode.Standard, false)]
    [InlineData(1119, false, PlaybackLayoutMode.Standard, false)]
    [InlineData(1120, false, PlaybackLayoutMode.Wide, true)]
    [InlineData(720, true, PlaybackLayoutMode.Fullscreen, false)]
    public void ResolveLayoutUsesStableDipBreakpoints(
        double width,
        bool fullscreen,
        PlaybackLayoutMode expectedMode,
        bool expectedInlineOptions)
    {
        var layout = PlaybackPagePresentation.ResolveLayout(width, fullscreen);

        Assert.Equal(expectedMode, layout.Mode);
        Assert.Equal(expectedInlineOptions, layout.ShowInlineOptions);
        Assert.InRange(layout.DrawerWidth, 280, fullscreen || width < 1120 ? 360 : 400);
    }

    [Fact]
    public void PlaybackEpisodeGroupsKeepVersionsNestedAndMarkCurrentVersion()
    {
        var web = Episode("web", 1, "web\01.mkv");
        var bd = Episode("bd", 1, "bd\01.mkv");
        var next = Episode("next", 2, "web\02.mkv");
        var queue = new MpvPlaybackQueue([web, bd, next], web.ProgressKey);

        var groups = PlaybackPagePresentation.BuildEpisodeGroups(queue, bd);

        Assert.Equal(2, groups.Count);
        Assert.Equal(2, groups[0].Versions.Count);
        Assert.True(groups[0].IsCurrent);
        Assert.True(groups[0].Versions.Single(version => version.Episode.ProgressKey == "bd").IsCurrent);
        Assert.False(groups[1].IsCurrent);
    }

    [Fact]
    public void SpeedOptionsAreStableAndChinese()
    {
        Assert.Equal([0.5f, 0.75f, 1f, 1.25f, 1.5f, 2f], PlaybackPagePresentation.SpeedOptions.Select(option => option.Value));
        Assert.Equal("1 倍", PlaybackPagePresentation.SpeedOptions[2].DisplayLabel);
    }

    [Fact]
    public void FullscreenChromeOnlyHidesForIdleUnblockedPlayback()
    {
        var idlePlaying = new PlaybackChromeContext(
            IsFullscreen: true,
            IsPlaying: true,
            IsPaused: false,
            HasError: false,
            IsDrawerOpen: false,
            IsOverflowOpen: false,
            IsSeeking: false,
            IsKeyboardFocusWithinChrome: false,
            IdleFor: TimeSpan.FromSeconds(2.5));

        Assert.False(PlaybackPagePresentation.ShouldShowChrome(idlePlaying));
        Assert.True(PlaybackPagePresentation.ShouldShowChrome(idlePlaying with { IsPaused = true }));
        Assert.True(PlaybackPagePresentation.ShouldShowChrome(idlePlaying with { IsDrawerOpen = true }));
        Assert.True(PlaybackPagePresentation.ShouldShowChrome(idlePlaying with { HasError = true }));
        Assert.True(PlaybackPagePresentation.ShouldShowChrome(idlePlaying with { IdleFor = TimeSpan.FromSeconds(2.49) }));
    }

    [Fact]
    public void StaleOptionResultCannotOverwriteNewerConfirmedSelection()
    {
        var initial = PlaybackOptionChangeState.Confirmed(1);
        var first = initial.Begin(2, 10);
        var second = first.Begin(3, 11).Confirm(11, 3);

        var afterStaleFailure = second.Reject(10);

        Assert.Equal(3, afterStaleFailure.ConfirmedValue);
        Assert.Equal(3, afterStaleFailure.DisplayValue);
        Assert.False(afterStaleFailure.IsPending);
    }

    [Fact]
    public void BuildViewStateKeepsRuntimeValuesOutOfTheWpfControlLayer()
    {
        var episode = Episode("current", 1);
        var queue = new MpvPlaybackQueue([episode], episode.ProgressKey);
        var info = new MpvPlaybackInfo(
            12_000,
            1_440_000,
            false,
            1.25,
            episode.FileName,
            episode.DisplayTitle,
            "matroska",
            "aac",
            new MpvVideoTrackInfo("hevc", "yuv420p10", "bt.2020", "pq", 1920, 1080),
            [new MpvAudioTrack(1, "jpn", "日语", "aac", false, true)],
            [],
            1,
            null,
            47.5,
            true);

        var state = PlaybackPagePresentation.BuildViewState(
            series: null,
            queue,
            episode,
            info,
            errorMessage: "切换失败",
            availableWidthDip: 1200,
            isFullscreen: false,
            isChromeVisible: true);

        Assert.Equal(12_000, state.Transport.PositionMs);
        Assert.Equal(47.5, state.Options.VolumePercent);
        Assert.True(state.Options.IsMuted);
        Assert.Equal(PlaybackLayoutMode.Wide, state.Layout.Mode);
        Assert.Equal("切换失败", state.Error!.Message);
        Assert.Contains("1920x1080", state.Header.MediaInfo, StringComparison.Ordinal);
    }

    private static LibraryEpisode Episode(string key, double number, string? path = null) => new(
        1,
        key,
        key,
        1,
        number,
        number,
        key,
        path ?? $"{key}.mkv",
        TimeSpan.FromMinutes(24),
        []);
}
