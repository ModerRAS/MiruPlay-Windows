namespace MiruPlay.Windows.Services;

public enum PlaybackPageMode
{
    Library,
    Settings,
    Detail,
    Playback,
}

public sealed class PlaybackNavigationState
{
    public PlaybackNavigationState(PlaybackPageMode mode, bool isFullscreen)
    {
        Mode = mode;
        IsFullscreen = isFullscreen;
    }

    public PlaybackPageMode Mode { get; private set; }
    public bool IsFullscreen { get; private set; }

    public PlaybackPageMode OnPlaybackBack()
    {
        Mode = Mode == PlaybackPageMode.Playback ? PlaybackPageMode.Detail : Mode;
        return Mode;
    }

    public PlaybackPageMode OnEscape()
    {
        if (IsFullscreen)
        {
            IsFullscreen = false;
            return Mode;
        }

        return OnPlaybackBack();
    }

    public void SetMode(PlaybackPageMode mode) => Mode = mode;

    public void SetFullscreen(bool enabled) => IsFullscreen = enabled;
}
