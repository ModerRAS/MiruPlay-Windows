using System.Globalization;
using MiruPlay.Windows.Models;

namespace MiruPlay.Windows.Services;

public readonly record struct PlaybackQueueCapabilities(
    bool CanPlayPrevious,
    bool CanPlayNext);

public enum PlaybackLayoutMode
{
    Compact,
    Standard,
    Wide,
    Fullscreen,
}

public sealed record PlaybackLayoutProfile(
    PlaybackLayoutMode Mode,
    bool ShowInlineOptions,
    double DrawerWidth);

public sealed record PlaybackSpeedOption(float Value, string DisplayLabel);

public sealed record PlaybackEpisodeVersionViewState(
    LibraryEpisode Episode,
    bool IsCurrent,
    string AccessibleName);

public sealed record PlaybackEpisodeGroupViewState(
    int Season,
    double Number,
    string DisplayLabel,
    IReadOnlyList<PlaybackEpisodeVersionViewState> Versions,
    bool IsCurrent);

public sealed record PlaybackHeaderViewState(
    string Title,
    string MediaInfo,
    string FileName);

public sealed record PlaybackTransportViewState(
    long PositionMs,
    long DurationMs,
    bool IsPaused,
    bool IsPlaying,
    bool CanPlayPrevious,
    bool CanPlayNext);

public sealed record PlaybackOptionsViewState(
    IReadOnlyList<MpvAudioTrack> AudioTracks,
    int? SelectedAudioTrackId,
    IReadOnlyList<PlaybackSubtitleTrack> SubtitleTracks,
    int? SelectedSubtitleTrackId,
    IReadOnlyList<PlaybackSpeedOption> Speeds,
    double Speed,
    double VolumePercent,
    bool IsMuted);

public sealed record PlaybackErrorViewState(
    string Message,
    bool CanRetry = true);

public sealed record PlaybackPageViewState(
    LibrarySeries? Series,
    LibraryEpisode Episode,
    PlaybackHeaderViewState Header,
    PlaybackTransportViewState Transport,
    PlaybackOptionsViewState Options,
    IReadOnlyList<PlaybackEpisodeGroupViewState> EpisodeGroups,
    PlaybackLayoutProfile Layout,
    bool IsFullscreen,
    bool IsChromeVisible,
    PlaybackErrorViewState? Error);

public abstract record PlaybackPageAction;

public sealed record PlaybackBackAction : PlaybackPageAction;
public sealed record PlaybackStopAction : PlaybackPageAction;
public sealed record PlaybackToggleAction : PlaybackPageAction;
public sealed record PlaybackSeekAction(long ValueMs, bool IsAbsolute) : PlaybackPageAction;
public sealed record PlaybackPreviousAction : PlaybackPageAction;
public sealed record PlaybackNextAction : PlaybackPageAction;
public sealed record PlaybackSelectEpisodeAction(LibraryEpisode Episode) : PlaybackPageAction;
public sealed record PlaybackSelectAudioTrackAction(int TrackId, long RequestId) : PlaybackPageAction;
public sealed record PlaybackSelectSubtitleTrackAction(int? TrackId, long RequestId) : PlaybackPageAction;
public sealed record PlaybackSetSpeedAction(float Speed, long RequestId) : PlaybackPageAction;
public sealed record PlaybackSetVolumeAction(double VolumePercent, long RequestId) : PlaybackPageAction;
public sealed record PlaybackSetMutedAction(bool IsMuted, long RequestId) : PlaybackPageAction;
public sealed record PlaybackToggleFullscreenAction : PlaybackPageAction;
public sealed record PlaybackExitFullscreenAction : PlaybackPageAction;
public sealed record PlaybackRetryAction : PlaybackPageAction;

public sealed record PlaybackChromeContext(
    bool IsFullscreen,
    bool IsPlaying,
    bool IsPaused,
    bool HasError,
    bool IsDrawerOpen,
    bool IsOverflowOpen,
    bool IsSeeking,
    bool IsKeyboardFocusWithinChrome,
    TimeSpan IdleFor);

public sealed record PlaybackOptionChangeState<T>(
    T ConfirmedValue,
    T DisplayValue,
    long LatestRequestId,
    bool IsPending)
{
    public PlaybackOptionChangeState<T> Begin(T value, long requestId) =>
        requestId < LatestRequestId ? this : new(ConfirmedValue, value, requestId, true);

    public PlaybackOptionChangeState<T> Confirm(long requestId, T value) =>
        requestId < LatestRequestId ? this : new(value, value, requestId, false);

    public PlaybackOptionChangeState<T> Reject(long requestId) =>
        requestId == LatestRequestId ? new(ConfirmedValue, ConfirmedValue, requestId, false) : this;
}

public static class PlaybackOptionChangeState
{
    public static PlaybackOptionChangeState<T> Confirmed<T>(T value) => new(value, value, 0, false);
}

public sealed record PlaybackEpisodeOption(
    LibraryEpisode Episode,
    string DisplayLabel,
    string VersionLabel,
    double ProgressPercent,
    string ProgressText,
    bool IsCurrent,
    string AccessibleName);

public static class PlaybackPagePresentation
{
    public static IReadOnlyList<PlaybackSpeedOption> SpeedOptions { get; } =
    [
        new(0.5f, "0.5 倍"),
        new(0.75f, "0.75 倍"),
        new(1f, "1 倍"),
        new(1.25f, "1.25 倍"),
        new(1.5f, "1.5 倍"),
        new(2f, "2 倍"),
    ];

    public static string FormatTime(long milliseconds) => TimeSpan
        .FromMilliseconds(Math.Max(0, milliseconds))
        .ToString(
            milliseconds >= 3_600_000 ? @"h\:mm\:ss" : @"m\:ss",
            CultureInfo.InvariantCulture);

    public static PlaybackQueueCapabilities GetCapabilities(MpvPlaybackQueue? queue) => new(
        queue?.CanPlayPrevious == true,
        queue?.CanPlayNext == true);

    public static bool HasTrackOptions<T>(IReadOnlyCollection<T> tracks) => tracks.Count > 0;

    public static PlaybackPageViewState BuildViewState(
        LibrarySeries? series,
        MpvPlaybackQueue? queue,
        LibraryEpisode episode,
        MpvPlaybackInfo? info,
        string? errorMessage,
        double availableWidthDip,
        bool isFullscreen,
        bool isChromeVisible)
    {
        ArgumentNullException.ThrowIfNull(episode);
        var video = info?.Video;
        var mediaInfo = string.Join("  ·  ", new[]
        {
            video?.Codec,
            video?.Width is int width && video.Height is int height ? $"{width}x{height}" : null,
            video?.IsHdr == true ? "HDR" : null,
            info?.ContainerFormat,
            info?.AudioCodec,
        }.Where(value => !string.IsNullOrWhiteSpace(value)));
        var capabilities = GetCapabilities(queue);
        var durationMs = info?.DurationMs ?? Convert.ToInt64(episode.Duration.TotalMilliseconds, CultureInfo.InvariantCulture);

        return new(
            series,
            episode,
            new($"{episode.DisplayNumber}  {episode.DisplayTitle}", mediaInfo, episode.FileName),
            new(
                info?.PositionMs ?? 0,
                Math.Max(0, durationMs),
                info?.IsPaused ?? false,
                info is not null && info.IsPaused == false && string.IsNullOrWhiteSpace(errorMessage),
                capabilities.CanPlayPrevious,
                capabilities.CanPlayNext),
            new(
                info?.AudioTracks ?? [],
                info?.SelectedAudioTrackId,
                info?.SubtitleTracks ?? [],
                info?.SelectedSubtitleTrackId,
                SpeedOptions,
                info?.Speed ?? 1,
                info?.VolumePercent ?? 100,
                info?.IsMuted ?? false),
            BuildEpisodeGroups(queue, episode),
            ResolveLayout(availableWidthDip, isFullscreen),
            isFullscreen,
            isChromeVisible,
            string.IsNullOrWhiteSpace(errorMessage) ? null : new(errorMessage));
    }

    public static PlaybackLayoutProfile ResolveLayout(double availableWidthDip, bool fullscreen)
    {
        var width = double.IsFinite(availableWidthDip) ? Math.Max(0, availableWidthDip) : 0;
        if (fullscreen) return new(PlaybackLayoutMode.Fullscreen, false, Math.Clamp(width - 24, 280, 360));
        var mode = width < 840
            ? PlaybackLayoutMode.Compact
            : width < 1120
                ? PlaybackLayoutMode.Standard
                : PlaybackLayoutMode.Wide;
        return new(mode, mode == PlaybackLayoutMode.Wide, Math.Clamp(width - 24, 280, mode == PlaybackLayoutMode.Wide ? 400 : 360));
    }

    public static bool ShouldShowChrome(PlaybackChromeContext context) =>
        !context.IsFullscreen ||
        !context.IsPlaying ||
        context.IsPaused ||
        context.HasError ||
        context.IsDrawerOpen ||
        context.IsOverflowOpen ||
        context.IsSeeking ||
        context.IsKeyboardFocusWithinChrome ||
        context.IdleFor < TimeSpan.FromSeconds(2.5);

    public static IReadOnlyList<PlaybackEpisodeGroupViewState> BuildEpisodeGroups(
        MpvPlaybackQueue? queue,
        LibraryEpisode current)
    {
        ArgumentNullException.ThrowIfNull(current);
        return queue?.Entries.Select(entry => new PlaybackEpisodeGroupViewState(
            entry.Season,
            entry.Number,
            entry.Versions[0].DisplayNumber,
            entry.Versions.Select(version => new PlaybackEpisodeVersionViewState(
                version,
                version.ProgressKey == current.ProgressKey,
                $"{version.DisplayNumber} {version.DisplayTitle} {version.VersionLabel}"))
                .ToList(),
            entry.Versions.Any(version => version.ProgressKey == current.ProgressKey)))
            .ToList() ?? [];
    }

    public static IReadOnlyList<PlaybackEpisodeOption> BuildEpisodeOptions(
        MpvPlaybackQueue? queue,
        LibraryEpisode current)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (queue is null) return [];

        return queue.Entries
            .SelectMany(entry => entry.Versions)
            .Select(episode => new PlaybackEpisodeOption(
                episode,
                episode.DisplayNumber,
                episode.VersionLabel,
                episode.ProgressPercent,
                episode.ProgressText,
                episode.ProgressKey == current.ProgressKey,
                $"{episode.PlayActionLabel} {episode.DisplayNumber} {episode.VersionLabel}"))
            .ToList();
    }
}
