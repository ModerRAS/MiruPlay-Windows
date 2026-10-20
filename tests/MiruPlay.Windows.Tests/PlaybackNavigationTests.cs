using MiruPlay.Windows.Services;

namespace MiruPlay.Windows.Tests;

public sealed class PlaybackNavigationTests
{
    [Fact]
    public void PlaybackBackReturnsToDetail()
    {
        var state = new PlaybackNavigationState(PlaybackPageMode.Playback, false);

        Assert.Equal(PlaybackPageMode.Detail, state.OnPlaybackBack());
    }

    [Fact]
    public void EscapeExitsFullscreenBeforeLeavingPlayback()
    {
        var state = new PlaybackNavigationState(PlaybackPageMode.Playback, true);

        Assert.Equal(PlaybackPageMode.Playback, state.OnEscape());
        Assert.False(state.IsFullscreen);
    }

    [Fact]
    public void DirectNavigationTracksAllMutuallyExclusiveApplicationPages()
    {
        var state = new PlaybackNavigationState(PlaybackPageMode.Library, false);

        state.SetMode(PlaybackPageMode.Settings);

        Assert.Equal(PlaybackPageMode.Settings, state.Mode);
    }
}
