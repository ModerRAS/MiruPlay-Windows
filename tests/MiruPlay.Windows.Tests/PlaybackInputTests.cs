using System.Windows.Input;
using MiruPlay.Windows.Services;

namespace MiruPlay.Windows.Tests;

public sealed class PlaybackInputTests
{
    [Fact]
    public void SpaceAndMediaPlayPauseTogglePlayback()
    {
        Assert.Equal(PlaybackInputAction.TogglePlayback, PlaybackInputMap.Resolve(Key.Space, ModifierKeys.None).Action);
        Assert.Equal(PlaybackInputAction.TogglePlayback, PlaybackInputMap.Resolve(Key.MediaPlayPause, ModifierKeys.None).Action);
    }

    [Fact]
    public void DirectionKeysSeekFiveSecondsAndCtrlDirectionKeysSeekThirtySeconds()
    {
        Assert.Equal(-5_000, PlaybackInputMap.Resolve(Key.Left, ModifierKeys.None).DeltaMs);
        Assert.Equal(5_000, PlaybackInputMap.Resolve(Key.Right, ModifierKeys.None).DeltaMs);
        Assert.Equal(-30_000, PlaybackInputMap.Resolve(Key.Left, ModifierKeys.Control).DeltaMs);
        Assert.Equal(30_000, PlaybackInputMap.Resolve(Key.Right, ModifierKeys.Control).DeltaMs);
    }

    [Fact]
    public void FullscreenMuteVolumeAndEscapeMapToDedicatedActions()
    {
        Assert.Equal(PlaybackInputAction.ToggleFullscreen, PlaybackInputMap.Resolve(Key.F11, ModifierKeys.None).Action);
        Assert.Equal(PlaybackInputAction.ToggleFullscreen, PlaybackInputMap.Resolve(Key.F, ModifierKeys.None).Action);
        Assert.Equal(PlaybackInputAction.ToggleMute, PlaybackInputMap.Resolve(Key.M, ModifierKeys.None).Action);
        Assert.Equal(PlaybackInputAction.VolumeUp, PlaybackInputMap.Resolve(Key.Up, ModifierKeys.None).Action);
        Assert.Equal(PlaybackInputAction.VolumeDown, PlaybackInputMap.Resolve(Key.Down, ModifierKeys.None).Action);
        Assert.Equal(PlaybackInputAction.ExitFullscreen, PlaybackInputMap.Resolve(Key.Escape, ModifierKeys.None).Action);
    }

    [Fact]
    public void UnmodifiedTextAndUnsupportedKeysAreIgnored()
    {
        Assert.Equal(PlaybackInputAction.None, PlaybackInputMap.Resolve(Key.A, ModifierKeys.None).Action);
        Assert.Equal(PlaybackInputAction.None, PlaybackInputMap.Resolve(Key.Space, ModifierKeys.Shift).Action);
    }

    [Theory]
    [InlineData(PlaybackInputContext.Button, Key.Space)]
    [InlineData(PlaybackInputContext.Button, Key.Enter)]
    [InlineData(PlaybackInputContext.TextEntry, Key.Space)]
    [InlineData(PlaybackInputContext.SelectionControl, Key.Left)]
    [InlineData(PlaybackInputContext.SelectionControl, Key.Space)]
    [InlineData(PlaybackInputContext.Slider, Key.Right)]
    public void FocusedControlsKeepTheirNativeKeys(PlaybackInputContext context, Key key)
    {
        var decision = PlaybackInputMap.Resolve(key, ModifierKeys.None, context);

        Assert.False(decision.Handled);
        Assert.Equal(PlaybackInputAction.None, decision.Command.Action);
    }

    [Theory]
    [InlineData(PlaybackInputContext.Button, Key.Escape, PlaybackInputAction.ExitFullscreen)]
    [InlineData(PlaybackInputContext.SelectionControl, Key.F11, PlaybackInputAction.ToggleFullscreen)]
    [InlineData(PlaybackInputContext.Slider, Key.Escape, PlaybackInputAction.ExitFullscreen)]
    [InlineData(PlaybackInputContext.TextEntry, Key.F11, PlaybackInputAction.ToggleFullscreen)]
    public void EscapeAndF11RemainGlobalAcrossFocusedControls(
        PlaybackInputContext context,
        Key key,
        PlaybackInputAction action)
    {
        var decision = PlaybackInputMap.Resolve(key, ModifierKeys.None, context);

        Assert.True(decision.Handled);
        Assert.Equal(action, decision.Command.Action);
    }

    [Fact]
    public void OpenSelectionPopupOwnsTheFirstEscape()
    {
        var decision = PlaybackInputMap.Resolve(
            Key.Escape,
            ModifierKeys.None,
            PlaybackInputContext.SelectionControl,
            isSelectionPopupOpen: true);

        Assert.False(decision.Handled);
        Assert.Equal(PlaybackInputAction.None, decision.Command.Action);
    }
}
