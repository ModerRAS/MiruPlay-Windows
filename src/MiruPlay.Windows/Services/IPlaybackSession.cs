namespace MiruPlay.Windows.Services;

public interface IPlaybackSession : IAsyncDisposable
{
    Task Completion { get; }
    Models.LibraryEpisode Episode { get; }
    bool WasCompleted { get; }
    long PositionMs { get; }
    long DurationMs { get; }
    bool IsPaused { get; }
    string? LastError { get; }
    IReadOnlyList<PlaybackSubtitleTrack> SubtitleTracks { get; }
    int? SelectedSubtitleTrackId { get; }
    IReadOnlyList<MpvAudioTrack> AudioTracks { get; }
    int? SelectedAudioTrackId { get; }
    double Speed { get; }
    MpvPlaybackInfo PlaybackInfo { get; }
    AudioDspFilterGraph? AppliedAudioDsp { get; }
    bool IsActive { get; }
    bool IsPlaying { get; }
    event EventHandler? SubtitleTracksChanged;
    event EventHandler? AudioTracksChanged;
    event EventHandler? PlaybackInfoChanged;

    Task SetSubtitleTrackAsync(int? trackId);
    Task SetAudioTrackAsync(int? trackId);
    Task SeekAsync(long positionMs);
    Task SeekRelativeAsync(long deltaMs);
    Task SetSpeedAsync(float speed);
    Task TogglePauseAsync();
    Task ApplyAudioDspAsync(AudioDspFilterGraph graph);
    Task<string?> GetAudioFilterGraphAsync();
    Task<MpvPlaybackInfo> GetPlaybackInfoAsync();
    Task ExecuteCommandAsync(PlaybackControlCommand request);
    Task ExecuteCommandAsync(MpvPlaybackCommand request);
}
