using System.Windows.Input;
using MiruPlay.Windows.Tv;

namespace MiruPlay.Windows.Tests;

public sealed class TvKeyMapTests
{
    [Theory]
    [InlineData(Key.Up, TvInputAction.Up)]
    [InlineData(Key.Down, TvInputAction.Down)]
    [InlineData(Key.Left, TvInputAction.Left)]
    [InlineData(Key.Right, TvInputAction.Right)]
    [InlineData(Key.Enter, TvInputAction.Select)]
    [InlineData(Key.Space, TvInputAction.Select)]
    [InlineData(Key.Escape, TvInputAction.Back)]
    [InlineData(Key.Back, TvInputAction.Back)]
    [InlineData(Key.Home, TvInputAction.Home)]
    [InlineData(Key.M, TvInputAction.Menu)]
    [InlineData(Key.MediaPlayPause, TvInputAction.PlayPause)]
    [InlineData(Key.MediaStop, TvInputAction.Stop)]
    [InlineData(Key.MediaPreviousTrack, TvInputAction.PrevEpisode)]
    [InlineData(Key.MediaNextTrack, TvInputAction.NextEpisode)]
    [InlineData(Key.VolumeUp, TvInputAction.VolumeUp)]
    [InlineData(Key.VolumeDown, TvInputAction.VolumeDown)]
    [InlineData(Key.VolumeMute, TvInputAction.Mute)]
    public void GeneralPresetMapsCommonKeyboardKeys(Key key, TvInputAction expected) =>
        Assert.Equal(expected, TvKeyMap.Resolve(TvRemotePreset.General, key, ModifierKeys.None));

    [Theory]
    [InlineData(Key.BrowserBack, TvInputAction.Back)]
    [InlineData(Key.BrowserHome, TvInputAction.Home)]
    [InlineData(Key.Enter, TvInputAction.Select)]
    [InlineData(Key.MediaPlayPause, TvInputAction.PlayPause)]
    [InlineData(Key.VolumeUp, TvInputAction.VolumeUp)]
    public void McePresetMapsRemoteControlKeys(Key key, TvInputAction expected) =>
        Assert.Equal(expected, TvKeyMap.Resolve(TvRemotePreset.MceRc6, key, ModifierKeys.None));

    [Theory]
    [InlineData(Key.Back, TvInputAction.Back)]
    [InlineData(Key.BrowserBack, TvInputAction.Back)]
    [InlineData(Key.BrowserHome, TvInputAction.Home)]
    [InlineData(Key.Enter, TvInputAction.Select)]
    [InlineData(Key.MediaPlayPause, TvInputAction.PlayPause)]
    public void FireTvPresetMapsBluetoothRemoteKeys(Key key, TvInputAction expected) =>
        Assert.Equal(expected, TvKeyMap.Resolve(TvRemotePreset.FireTv, key, ModifierKeys.None));

    [Fact]
    public void PresetFallsBackToTheUnionOfSupportedRemotes()
    {
        // BrowserHome is an MCE/universal key but not in the General table.
        Assert.Equal(TvInputAction.Home, TvKeyMap.Resolve(TvRemotePreset.General, Key.BrowserHome, ModifierKeys.None));
        Assert.Equal(TvInputAction.Back, TvKeyMap.Resolve(TvRemotePreset.General, Key.BrowserBack, ModifierKeys.None));
    }

    [Fact]
    public void ShiftArrowsMapToSeekActions()
    {
        Assert.Equal(TvInputAction.SeekBack, TvKeyMap.Resolve(TvRemotePreset.General, Key.Left, ModifierKeys.Shift));
        Assert.Equal(TvInputAction.SeekForward, TvKeyMap.Resolve(TvRemotePreset.General, Key.Right, ModifierKeys.Shift));
        Assert.Null(TvKeyMap.Resolve(TvRemotePreset.General, Key.Up, ModifierKeys.Shift));
    }

    [Fact]
    public void UnknownAndModifiedKeysAreNotMapped()
    {
        Assert.Null(TvKeyMap.Resolve(TvRemotePreset.General, Key.F24, ModifierKeys.None));
        Assert.Null(TvKeyMap.Resolve(TvRemotePreset.General, Key.Up, ModifierKeys.Control));
        Assert.Null(TvKeyMap.Resolve(TvRemotePreset.General, Key.Enter, ModifierKeys.Alt));
    }

    [Theory]
    [InlineData(null, "general")]
    [InlineData("", "general")]
    [InlineData("mce", "mce")]
    [InlineData("MCE", "mce")]
    [InlineData("firetv", "firetv")]
    [InlineData("custom", "general")]
    public void PresetIdsAreNormalized(string? value, string expected) =>
        Assert.Equal(expected, TvKeyMap.NormalizePresetId(value));

    [Fact]
    public void PresetRoundTripsThroughIds()
    {
        foreach (var preset in new[] { TvRemotePreset.General, TvRemotePreset.MceRc6, TvRemotePreset.FireTv })
            Assert.Equal(preset, TvKeyMap.ParsePreset(TvKeyMap.ToId(preset)));
    }

    [Theory]
    [InlineData(TvInputAction.Up, true)]
    [InlineData(TvInputAction.Right, true)]
    [InlineData(TvInputAction.SeekForward, true)]
    [InlineData(TvInputAction.VolumeDown, true)]
    [InlineData(TvInputAction.Select, false)]
    [InlineData(TvInputAction.Back, false)]
    [InlineData(TvInputAction.Home, false)]
    public void OnlyNavigationStyleActionsRepeat(TvInputAction action, bool expected) =>
        Assert.Equal(expected, TvKeyMap.IsRepeatable(action));
}
