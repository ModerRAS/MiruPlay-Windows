using System.Windows.Input;

namespace MiruPlay.Windows.Services;

public enum PlaybackInputAction
{
    None,
    TogglePlayback,
    SeekRelative,
    ToggleFullscreen,
    ExitFullscreen,
    ToggleMute,
    VolumeUp,
    VolumeDown,
}

public readonly record struct PlaybackInputCommand(
    PlaybackInputAction Action,
    long DeltaMs = 0);

public enum PlaybackInputContext
{
    Page,
    VideoSurface,
    Button,
    TextEntry,
    SelectionControl,
    Slider,
}

public readonly record struct PlaybackInputDecision(
    PlaybackInputCommand Command,
    bool Handled);

public static class PlaybackInputMap
{
    public static PlaybackInputCommand Resolve(Key key, ModifierKeys modifiers) =>
        Resolve(key, modifiers, PlaybackInputContext.Page).Command;

    public static PlaybackInputDecision Resolve(
        Key key,
        ModifierKeys modifiers,
        PlaybackInputContext context,
        bool isSelectionPopupOpen = false)
    {
        if (key == Key.Escape && context == PlaybackInputContext.SelectionControl && isSelectionPopupOpen)
            return new(new(PlaybackInputAction.None), false);

        if (key == Key.F11)
            return new(new(PlaybackInputAction.ToggleFullscreen), true);
        if (key == Key.Escape)
            return new(new(PlaybackInputAction.ExitFullscreen), true);
        if (key == Key.MediaPlayPause)
            return new(new(PlaybackInputAction.TogglePlayback), true);

        if (context is PlaybackInputContext.Button or PlaybackInputContext.TextEntry or
            PlaybackInputContext.SelectionControl or PlaybackInputContext.Slider)
            return new(new(PlaybackInputAction.None), false);

        if (modifiers == ModifierKeys.Control && key is Key.Left or Key.Right)
        {
            return new(new(
                PlaybackInputAction.SeekRelative,
                key == Key.Left ? -30_000 : 30_000), true);
        }

        if (modifiers != ModifierKeys.None) return new(new(PlaybackInputAction.None), false);

        PlaybackInputCommand command = key switch
        {
            Key.Space => new(PlaybackInputAction.TogglePlayback),
            Key.Left => new(PlaybackInputAction.SeekRelative, -5_000),
            Key.Right => new(PlaybackInputAction.SeekRelative, 5_000),
            Key.F or Key.F11 => new(PlaybackInputAction.ToggleFullscreen),
            Key.Escape => new(PlaybackInputAction.ExitFullscreen),
            Key.M => new(PlaybackInputAction.ToggleMute),
            Key.Up => new(PlaybackInputAction.VolumeUp),
            Key.Down => new(PlaybackInputAction.VolumeDown),
            _ => new(PlaybackInputAction.None),
        };
        return new(command, command.Action != PlaybackInputAction.None);
    }
}
