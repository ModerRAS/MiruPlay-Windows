namespace MiruPlay.Windows.Tv;

public sealed record TvOsdDrawerItem(string Label, string? Value, bool IsHeader = false, bool IsFocusable = false, bool IsSelected = false);

public sealed record TvOsdDrawer(string Title, IReadOnlyList<TvOsdDrawerItem> Items, int FocusIndex);

/// <summary>Immutable snapshot rendered by the layered OSD window.</summary>
public sealed record TvOsdState
{
    public bool Visible { get; init; }
    public string Title { get; init; } = string.Empty;
    public string Subtitle { get; init; } = string.Empty;
    public string MetaLine { get; init; } = string.Empty;
    public long PositionMs { get; init; }
    public long DurationMs { get; init; }
    public bool IsPaused { get; init; }
    public double Speed { get; init; } = 1;
    public string SubtitleLabel { get; init; } = string.Empty;
    public string AudioLabel { get; init; } = string.Empty;
    public string SpeedLabel { get; init; } = string.Empty;
    public bool FocusVisible { get; init; }
    public int FocusRow { get; init; }
    public int FocusIndex { get; init; }
    public TvOsdDrawer? Drawer { get; init; }
    public string? Status { get; init; }
    public string? Error { get; init; }
    public string? NextEpisode { get; init; }
}
