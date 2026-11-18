using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MiruPlay.Windows.Models;
using MiruPlay.Windows.Services;

namespace MiruPlay.Windows.Tv;

/// <summary>
/// TV player page: libmpv keeps rendering into the embedded child HWND; all
/// chrome is drawn by the layered, non-activating <see cref="TvOsdWindow"/>
/// above it. This screen owns the playback session, queue, OSD state machine
/// and Back layering (drawer -> OSD -> exit).
/// </summary>
public sealed class TvPlayerScreen : TvScreenBase, IDisposable
{
    private static readonly float[] Speeds = [0.5f, 0.75f, 1f, 1.25f, 1.5f, 2f];

    private readonly TvAppContext _context;
    private readonly TvShellWindow _shell;
    private readonly Grid _root = new() { Background = Brushes.Black, Focusable = false };
    private readonly TvMpvHost _videoHost = new() { Focusable = false };
    private readonly TextBlock _loadingText;
    private readonly DispatcherTimer _tickTimer;
    private readonly DispatcherTimer _hideTimer;
    private readonly DispatcherTimer _statusTimer;
    private readonly DispatcherTimer _exitTimer;
    private TvOsdWindow? _osd;
    private IPlaybackSession? _session;
    private MpvPlaybackQueue? _queue;
    private LibrarySeries? _series;
    private LibraryEpisode? _episode;
    private TvOsdDrawer? _drawer;
    private DrawerSection _drawerSection;
    private int _drawerFocus;
    private readonly List<Action> _drawerActions = [];
    private bool _starting;
    private bool _disposed;
    private bool _osdVisible;
    private bool _osdFocusMode;
    private int _focusRow;
    private int _focusIndex;
    private string? _status;
    private string? _error;

    public TvPlayerScreen(TvAppContext context, TvShellWindow shell)
    {
        _context = context;
        _shell = shell;
        TvTheme.Apply(this);
        InputMethod.SetIsInputMethodEnabled(this, false);
        _root.Background = Brushes.Black;
        _videoHost.HorizontalAlignment = HorizontalAlignment.Stretch;
        _videoHost.VerticalAlignment = VerticalAlignment.Stretch;
        _root.Children.Add(_videoHost);
        _loadingText = new TextBlock
        {
            Text = AppStrings.Get("tv.player.loading"),
            FontSize = 26,
            Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _root.Children.Add(_loadingText);
        Content = _root;
        _videoHost.RawKeyDown += (_, e) => _shell.ProcessRawKeyDown(e.Key);
        _videoHost.RawKeyUp += (_, e) => _shell.ProcessRawKeyUp(e.Key);
        _tickTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(220) };
        _tickTimer.Tick += (_, _) => UpdateOsd();
        _hideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _hideTimer.Tick += (_, _) => HideOsdIfIdle();
        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        _statusTimer.Tick += (_, _) => { _statusTimer.Stop(); _status = null; UpdateOsd(); };
        _exitTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
        _exitTimer.Tick += (_, _) => { _exitTimer.Stop(); _shell.ExitPlaybackAsync(); };
    }

    public override TvScreenId Id => TvScreenId.Player;

    public override string HintText => string.Empty;

    public override bool FocusInitial() => true;

    public override string? OnDeactivating() => null;

    public IPlaybackSession? Session => _session;

    public async Task BeginAsync(LibrarySeries series, LibraryEpisode episode, bool resume)
    {
        _series = series;
        _episode = episode;
        _queue = new MpvPlaybackQueue(series.Episodes, episode.ProgressKey);
        await StartEpisodeAsync(episode, resume).ConfigureAwait(true);
    }

    private async Task StartEpisodeAsync(LibraryEpisode episode, bool resume)
    {
        if (_disposed) return;
        _starting = true;
        _error = null;
        _episode = episode;
        _loadingText.Visibility = Visibility.Visible;
        _loadingText.Text = AppStrings.Get("tv.player.loading");
        UpdateOsd();
        await DisposeSessionAsync();
        try
        {
            UpdateLayout();
            for (var attempt = 0; attempt < 60 && _videoHost.NativeHandle == IntPtr.Zero; attempt++)
                await Task.Delay(50).ConfigureAwait(true);
            _videoHost.ValidateForMpv();
            var source = episode.SourceId > 0 ? _context.MediaSources.Get(episode.SourceId) : null;
            var proxy = source?.Type == "WEBDAV" ? _context.MediaSources.CreatePlaybackProxy(source.Id, EpisodeWithSidecars(episode)) : null;
            var resumePosition = resume && episode.IsInProgress ? episode.WatchedPositionMs : (long?)null;
            var launch = await MpvPlayerLauncher.PlayDetailedAsync(
                EpisodeWithSidecars(episode),
                _context.Settings,
                _context.ProgressStore,
                resumePosition,
                playbackProxy: proxy,
                windowHandle: _videoHost.NativeHandle,
                videoOptions: new MpvWindowsVideoOptions(),
                allowDirectHttp: source?.Type == "RSS",
                forceStartPosition: !resume).ConfigureAwait(true);
            if (_disposed)
            {
                if (launch.Session is not null) await launch.Session.DisposeAsync();
                return;
            }
            if (launch.Session is null)
            {
                ShowError(AppStrings.Get("tv.player.error.degraded", string.Join(" · ", launch.DegradedCapabilities)));
                return;
            }
            _session = launch.Session;
            _session.SubtitleTracksChanged += Session_Changed;
            _session.AudioTracksChanged += Session_Changed;
            _session.PlaybackInfoChanged += Session_Changed;
            _shell.FocusShellForPlayback();
            _starting = false;
            _loadingText.Visibility = Visibility.Collapsed;
            _osd = _shell.EnsureOsd();
            ShowOsd();
            _tickTimer.Start();
            _ = MonitorSessionAsync(_session);
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or NotSupportedException
            or System.ComponentModel.Win32Exception or UnauthorizedAccessException or ArgumentException)
        {
            ShowError(error.Message);
        }
    }

    private static LibraryEpisode EpisodeWithSidecars(LibraryEpisode episode)
    {
        if (episode.ExternalAudioPaths.Count > 0) return episode;
        var sidecars = ExternalAudioPathMatcher.FindLocalSiblings(episode.MediaPath);
        return sidecars.Count > 0 ? episode with { ExternalAudioPaths = sidecars } : episode;
    }

    private void Session_Changed(object? sender, EventArgs e)
    {
        if (_disposed) return;
        _ = Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(UpdateOsd));
    }

    private async Task MonitorSessionAsync(IPlaybackSession session)
    {
        await session.Completion.ConfigureAwait(true);
        if (_disposed || !ReferenceEquals(_session, session)) return;
        var completed = session.WasCompleted;
        var lastError = session.LastError;
        _tickTimer.Stop();
        if (!string.IsNullOrWhiteSpace(lastError))
        {
            ShowError(lastError);
            return;
        }
        if (completed && _queue is not null && _context.Settings.PlaybackEndAction == "play_next_episode")
        {
            if (_queue.Next() is { } next)
            {
                _status = AppStrings.Get("tv.player.nextNow");
                UpdateOsd();
                await StartEpisodeAsync(next, resume: false).ConfigureAwait(true);
                return;
            }
        }
        _status = AppStrings.Get(completed ? "tv.player.finished" : "tv.player.stopped");
        UpdateOsd();
        _exitTimer.Start();
    }

    public override bool HandleAction(TvInputAction action)
    {
        if (_disposed) return true;
        if (_error is not null)
        {
            if (action == TvInputAction.Back) { _shell.ExitPlaybackAsync(); return true; }
            if (action == TvInputAction.Select) { _ = RetryAsync(); return true; }
            return action != TvInputAction.Home;
        }
        if (_starting)
        {
            if (action == TvInputAction.Back) { _shell.ExitPlaybackAsync(); return true; }
            return action != TvInputAction.Home;
        }

        switch (action)
        {
            case TvInputAction.Back:
                if (_drawer is not null) { CloseDrawer(); return true; }
                if (_osdVisible) { HideOsd(); return true; }
                _shell.ExitPlaybackAsync();
                return true;
            case TvInputAction.Home:
                return false;
            case TvInputAction.Menu:
                OpenDrawer(DrawerSection.Subtitles);
                return true;
            case TvInputAction.Subtitles:
                OpenDrawer(DrawerSection.Subtitles);
                return true;
            case TvInputAction.AudioTrack:
                OpenDrawer(DrawerSection.Audio);
                return true;
            case TvInputAction.Info:
                OpenDrawer(DrawerSection.Info);
                return true;
            case TvInputAction.Up:
            case TvInputAction.Down:
            case TvInputAction.Left:
            case TvInputAction.Right:
                if (_drawer is not null) { MoveDrawer(action); return true; }
                if (!_osdVisible) { ShowOsd(enterFocus: true); return true; }
                if (!_osdFocusMode) { _osdFocusMode = true; _focusRow = 0; _focusIndex = 0; UpdateOsd(); RestartHideTimer(); return true; }
                MoveFocus(action);
                UpdateOsd();
                RestartHideTimer();
                return true;
            case TvInputAction.Select:
                if (_drawer is not null) { ActivateDrawer(); return true; }
                if (_osdFocusMode) { ActivateFocus(); return true; }
                _ = TogglePauseAsync();
                return true;
            case TvInputAction.PlayPause:
                _ = TogglePauseAsync();
                return true;
            case TvInputAction.SeekBack:
                _ = SeekAsync(-10_000);
                return true;
            case TvInputAction.SeekForward:
                _ = SeekAsync(30_000);
                return true;
            case TvInputAction.PrevEpisode:
                _ = PlayRelativeAsync(-1);
                return true;
            case TvInputAction.NextEpisode:
                _ = PlayRelativeAsync(1);
                return true;
            case TvInputAction.SpeedDown:
                _ = StepSpeedAsync(-1);
                return true;
            case TvInputAction.SpeedUp:
                _ = StepSpeedAsync(1);
                return true;
            case TvInputAction.VolumeUp:
                _ = StepVolumeAsync(5);
                return true;
            case TvInputAction.VolumeDown:
                _ = StepVolumeAsync(-5);
                return true;
            case TvInputAction.Mute:
                _ = ToggleMuteAsync();
                return true;
            case TvInputAction.Stop:
                _shell.ExitPlaybackAsync();
                return true;
            default:
                return false;
        }
    }

    public override bool HandleBack() => false;

    public async Task DisposeScreenAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _tickTimer.Stop();
        _hideTimer.Stop();
        _statusTimer.Stop();
        _exitTimer.Stop();
        _osd?.SetVisible(false);
        _osd?.SetError(null);
        _shell.HideOsd();
        _osd = null;
        await DisposeSessionAsync();
        Content = null;
        _videoHost.Dispose();
    }

    /// <summary>Synchronous disposal hook for the CA1001 pattern; the async screen teardown does the work.</summary>
    public void Dispose()
    {
        _ = DisposeScreenAsync();
        GC.SuppressFinalize(this);
    }

    private async Task DisposeSessionAsync()
    {
        var session = _session;
        _session = null;
        if (session is null) return;
        session.SubtitleTracksChanged -= Session_Changed;
        session.AudioTracksChanged -= Session_Changed;
        session.PlaybackInfoChanged -= Session_Changed;
        try
        {
            await session.DisposeAsync();
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or ObjectDisposedException)
        {
            // A stopped libmpv session may already be gone; progress was saved by the session itself.
        }
    }

    private async Task TogglePauseAsync()
    {
        if (_session is null) return;
        try
        {
            await _session.TogglePauseAsync();
            _status = AppStrings.Get(_session.IsPaused ? "tv.player.paused" : "tv.player.playing");
            ShowStatus();
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or TimeoutException)
        {
            ShowToastError(error.Message);
        }
    }

    private async Task SeekAsync(long deltaMs)
    {
        if (_session is null) return;
        try
        {
            await _session.SeekRelativeAsync(deltaMs);
            _status = AppStrings.Get(deltaMs < 0 ? "tv.player.seekBackDone" : "tv.player.seekForwardDone",
                Math.Abs(deltaMs) / 1000);
            ShowStatus();
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or TimeoutException)
        {
            ShowToastError(error.Message);
        }
    }

    private async Task PlayRelativeAsync(int direction)
    {
        if (_queue is null) return;
        var episode = direction < 0 ? _queue.Previous() : _queue.Next();
        if (episode is null)
        {
            _status = AppStrings.Get("tv.player.noAdjacent");
            ShowStatus();
            return;
        }
        _status = null;
        await StartEpisodeAsync(episode, resume: false).ConfigureAwait(true);
    }

    private async Task StepSpeedAsync(int direction)
    {
        if (_session is null) return;
        var current = _session.Speed;
        var index = Array.FindIndex(Speeds, speed => Math.Abs(speed - current) < 0.01f);
        if (index < 0) index = 2;
        index = Math.Clamp(index + direction, 0, Speeds.Length - 1);
        try
        {
            await _session.SetSpeedAsync(Speeds[index]);
            _status = AppStrings.Get("tv.player.speedSet", TvPresentation.FormatSpeed(Speeds[index]));
            ShowStatus();
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or TimeoutException)
        {
            ShowToastError(error.Message);
        }
    }

    private async Task StepVolumeAsync(double delta)
    {
        if (_session is null) return;
        var value = Math.Clamp(_session.VolumePercent + delta, 0, 100);
        try
        {
            await _session.SetVolumeAsync(value);
            _status = AppStrings.Get("tv.player.volume", (int)value);
            ShowStatus();
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or TimeoutException)
        {
            ShowToastError(error.Message);
        }
    }

    private async Task ToggleMuteAsync()
    {
        if (_session is null) return;
        try
        {
            await _session.SetMutedAsync(!_session.IsMuted);
            _status = AppStrings.Get(_session.IsMuted ? "tv.player.muted" : "tv.player.unmuted");
            ShowStatus();
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or TimeoutException)
        {
            ShowToastError(error.Message);
        }
    }

    private void ShowStatus()
    {
        _osdVisible = true;
        _statusTimer.Stop();
        _statusTimer.Start();
        UpdateOsd();
        _osd?.SetVisible(true);
        RestartHideTimer();
    }

    private void ShowToastError(string message)
    {
        _status = message;
        ShowStatus();
    }

    private void ShowOsd(bool enterFocus = false)
    {
        if (_disposed) return;
        _osdVisible = true;
        if (enterFocus)
        {
            _osdFocusMode = true;
            _focusRow = 0;
            _focusIndex = 0;
        }
        _osd?.SetVisible(true);
        UpdateOsd();
        RestartHideTimer();
    }

    private void HideOsd()
    {
        _osdVisible = false;
        _osdFocusMode = false;
        _drawer = null;
        _osd?.SetVisible(false);
        _hideTimer.Stop();
    }

    private void RestartHideTimer()
    {
        _hideTimer.Stop();
        if (!_osdVisible) return;
        if (_osdFocusMode || _drawer is not null || _session?.IsPaused == true || _error is not null) return;
        _hideTimer.Start();
    }

    private void HideOsdIfIdle()
    {
        _hideTimer.Stop();
        if (!_osdVisible) return;
        if (_osdFocusMode || _drawer is not null || _session?.IsPaused == true) return;
        HideOsd();
    }

    private void MoveFocus(TvInputAction action)
    {
        switch (action)
        {
            case TvInputAction.Up when _focusRow == 1:
                _focusRow = 0;
                _focusIndex = Math.Min(_focusIndex, 4);
                break;
            case TvInputAction.Down when _focusRow == 0:
                _focusRow = 1;
                _focusIndex = Math.Min(_focusIndex, 3);
                break;
            case TvInputAction.Left:
                _focusIndex = Math.Max(0, _focusIndex - 1);
                break;
            case TvInputAction.Right:
                _focusIndex = Math.Min(_focusRow == 0 ? 4 : 3, _focusIndex + 1);
                break;
        }
    }

    private void ActivateFocus()
    {
        if (_focusRow == 0)
        {
            switch (_focusIndex)
            {
                case 0: _ = PlayRelativeAsync(-1); break;
                case 1: _ = SeekAsync(-10_000); break;
                case 2: _ = TogglePauseAsync(); break;
                case 3: _ = SeekAsync(30_000); break;
                case 4: _ = PlayRelativeAsync(1); break;
            }
            return;
        }
        switch (_focusIndex)
        {
            case 0: OpenDrawer(DrawerSection.Subtitles); break;
            case 1: OpenDrawer(DrawerSection.Audio); break;
            case 2: OpenDrawer(DrawerSection.Speed); break;
            case 3: OpenDrawer(DrawerSection.Info); break;
        }
    }

    private void OpenDrawer(DrawerSection section)
    {
        _drawerSection = section;
        _drawerFocus = 0;
        _osdFocusMode = false;
        _osdVisible = true;
        _osd?.SetVisible(true);
        RebuildDrawer();
        UpdateOsd();
        _hideTimer.Stop();
    }

    private void CloseDrawer()
    {
        _drawer = null;
        UpdateOsd();
        RestartHideTimer();
    }

    private void MoveDrawer(TvInputAction action)
    {
        var focusable = _drawerActions.Count;
        if (focusable == 0) return;
        if (action == TvInputAction.Up) _drawerFocus = Math.Max(0, _drawerFocus - 1);
        else if (action == TvInputAction.Down) _drawerFocus = Math.Min(focusable - 1, _drawerFocus + 1);
        else return;
        RebuildDrawer();
        UpdateOsd();
    }

    private void ActivateDrawer()
    {
        if (_drawerFocus < 0 || _drawerFocus >= _drawerActions.Count) return;
        _drawerActions[_drawerFocus]();
    }

    private void RebuildDrawer()
    {
        _drawerActions.Clear();
        var items = new List<TvOsdDrawerItem>();
        var titleKey = _drawerSection switch
        {
            DrawerSection.Subtitles => "tv.player.drawer.subtitles",
            DrawerSection.Audio => "tv.player.drawer.audio",
            DrawerSection.Speed => "tv.player.drawer.speed",
            _ => "tv.player.drawer.info",
        };
        var title = AppStrings.Get(titleKey);
        var session = _session;
        switch (_drawerSection)
        {
            case DrawerSection.Subtitles:
                {
                    var selected = session?.SelectedSubtitleTrackId;
                    items.Add(new TvOsdDrawerItem(AppStrings.Get("tv.player.subtitleOff"), null, IsFocusable: true, IsSelected: selected is null));
                    _drawerActions.Add(() => _ = SetSubtitleAsync(null));
                    if (session is not null)
                    {
                        foreach (var track in session.SubtitleTracks)
                        {
                            var trackId = track.Id;
                            items.Add(new TvOsdDrawerItem(FriendlyTrackLabel(track.DisplayLabel, track.Language),
                                track.IsExternal ? AppStrings.Get("tv.player.external") : track.Codec,
                                IsFocusable: true, IsSelected: selected == trackId));
                            _drawerActions.Add(() => _ = SetSubtitleAsync(trackId));
                        }
                    }
                    break;
                }
            case DrawerSection.Audio:
                {
                    var selected = session?.SelectedAudioTrackId;
                    if (session is not null)
                    {
                        foreach (var track in session.AudioTracks)
                        {
                            var trackId = track.Id;
                            items.Add(new TvOsdDrawerItem(FriendlyTrackLabel(track.DisplayLabel, track.Language), track.Codec,
                                IsFocusable: true, IsSelected: selected == trackId));
                            _drawerActions.Add(() => _ = SetAudioAsync(trackId));
                        }
                    }
                    break;
                }
            case DrawerSection.Speed:
                foreach (var speed in Speeds)
                {
                    var value = speed;
                    items.Add(new TvOsdDrawerItem(TvPresentation.FormatSpeed(value), null,
                        IsFocusable: true, IsSelected: session is not null && Math.Abs(session.Speed - value) < 0.01f));
                    _drawerActions.Add(() => _ = SetSpeedAsync(value));
                }
                break;
            case DrawerSection.Info:
                if (session is not null)
                {
                    var info = session.PlaybackInfo;
                    items.Add(new TvOsdDrawerItem(AppStrings.Get("tv.player.info.file"), _episode?.FileName ?? string.Empty));
                    items.Add(new TvOsdDrawerItem(AppStrings.Get("tv.player.info.video"),
                        string.Join(" · ", new[] { info.Video?.Codec, info.Video?.Width is int w && info.Video?.Height is int h ? $"{w}x{h}" : null, info.Video?.IsHdr == true ? "HDR" : null }
                            .Where(value => !string.IsNullOrWhiteSpace(value)))));
                    items.Add(new TvOsdDrawerItem(AppStrings.Get("tv.player.info.audio"), info.AudioCodec ?? string.Empty));
                    items.Add(new TvOsdDrawerItem(AppStrings.Get("tv.player.info.container"), info.ContainerFormat ?? string.Empty));
                    items.Add(new TvOsdDrawerItem(AppStrings.Get("tv.player.info.speed"), TvPresentation.FormatSpeed(session.Speed)));
                    items.Add(new TvOsdDrawerItem(AppStrings.Get("tv.player.info.volume"), $"{(int)session.VolumePercent}%"));
                    items.Add(new TvOsdDrawerItem(AppStrings.Get("tv.player.info.status"),
                        AppStrings.Get(session.IsPaused ? "tv.player.paused" : "tv.player.playing")));
                }
                break;
        }
        if (_drawerFocus >= _drawerActions.Count) _drawerFocus = Math.Max(0, _drawerActions.Count - 1);
        _drawer = new TvOsdDrawer(title, items, _drawerFocus);
    }

    private async Task SetSubtitleAsync(int? trackId)
    {
        if (_session is null) return;
        try
        {
            await _session.SetSubtitleTrackAsync(trackId);
            _status = AppStrings.Get("tv.player.subtitleSet", trackId is null ? AppStrings.Get("tv.player.subtitleOff") : SubtitleLabel());
            RebuildDrawer();
            ShowStatus();
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or TimeoutException)
        {
            ShowToastError(error.Message);
        }
    }

    private async Task SetAudioAsync(int trackId)
    {
        if (_session is null) return;
        try
        {
            await _session.SetAudioTrackAsync(trackId);
            _status = AppStrings.Get("tv.player.audioSet", AudioLabel());
            RebuildDrawer();
            ShowStatus();
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or TimeoutException)
        {
            ShowToastError(error.Message);
        }
    }

    private async Task SetSpeedAsync(float speed)
    {
        if (_session is null) return;
        try
        {
            await _session.SetSpeedAsync(speed);
            _status = AppStrings.Get("tv.player.speedSet", TvPresentation.FormatSpeed(speed));
            RebuildDrawer();
            ShowStatus();
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or TimeoutException)
        {
            ShowToastError(error.Message);
        }
    }

    private async Task RetryAsync()
    {
        if (_episode is null) return;
        _error = null;
        _osd?.SetError(null);
        await StartEpisodeAsync(_episode, resume: false).ConfigureAwait(true);
    }

    private void ShowError(string message)
    {
        _error = message;
        _starting = false;
        _loadingText.Visibility = Visibility.Collapsed;
        _osd = _shell.EnsureOsd();
        _osd.SetVisible(true);
        _osd.SetError(message);
        _osdVisible = true;
        _hideTimer.Stop();
        UpdateOsd();
    }

    private void UpdateOsd()
    {
        if (_disposed || _osd is null) return;
        _osd.Present(BuildState());
    }

    private TvOsdState BuildState()
    {
        var session = _session;
        var info = session?.PlaybackInfo;
        var meta = string.Join("  ·  ", new[]
        {
            _context.MediaSources.Get(_episode?.SourceId ?? 0)?.TypeLabel,
            info?.Video?.Codec,
            info?.Video?.Width is int width && info.Video?.Height is int height ? $"{width}x{height}" : null,
            info?.Video?.IsHdr == true ? "HDR" : null,
        }.Where(value => !string.IsNullOrWhiteSpace(value)));
        return new TvOsdState
        {
            Visible = _osdVisible && _error is null,
            Title = _series?.Title ?? string.Empty,
            Subtitle = _episode is null ? string.Empty : $"{_episode.DisplayNumber}  ·  {_episode.DisplayTitle}",
            MetaLine = meta,
            PositionMs = session?.PositionMs ?? 0,
            DurationMs = session?.DurationMs ?? 0,
            IsPaused = session?.IsPaused ?? false,
            Speed = session?.Speed ?? 1,
            SubtitleLabel = SubtitleLabel(),
            AudioLabel = AudioLabel(),
            SpeedLabel = TvPresentation.FormatSpeed(session?.Speed ?? 1),
            FocusVisible = _osdFocusMode && _drawer is null,
            FocusRow = _focusRow,
            FocusIndex = _focusIndex,
            Drawer = _drawer,
            Status = _status,
        };
    }

    private string SubtitleLabel()
    {
        var session = _session;
        if (session?.SelectedSubtitleTrackId is not int id) return AppStrings.Get("tv.player.subtitleOff");
        var track = session.SubtitleTracks.FirstOrDefault(item => item.Id == id);
        return track is null ? AppStrings.Get("tv.player.subtitleOn") : FriendlyTrackLabel(track.DisplayLabel, track.Language);
    }

    internal static string FriendlyTrackLabel(string label, string? language)
    {
        var mapped = language?.Trim().ToLowerInvariant() switch
        {
            "zh-hans" or "zh-cn" or "chs" or "zh" => AppStrings.Get("tv.settings.subtitle.zhHans"),
            "zh-hant" or "zh-tw" or "cht" => AppStrings.Get("tv.settings.subtitle.zhHant"),
            "en" or "eng" => AppStrings.Get("tv.settings.subtitle.en"),
            "ja" or "jpn" => AppStrings.Get("tv.settings.subtitle.ja"),
            _ => null,
        };
        if (!string.IsNullOrWhiteSpace(label) && label is not "und" &&
            !label.Equals(language, StringComparison.OrdinalIgnoreCase)) return label;
        return mapped ?? (string.IsNullOrWhiteSpace(label) || label == "und" ? AppStrings.Get("tv.player.audioDefault") : label);
    }

    private string AudioLabel()
    {
        var session = _session;
        if (session?.SelectedAudioTrackId is not int id) return AppStrings.Get("tv.player.audioDefault");
        var track = session.AudioTracks.FirstOrDefault(item => item.Id == id);
        return track is null ? AppStrings.Get("tv.player.audioDefault") : FriendlyTrackLabel(track.DisplayLabel, track.Language);
    }

    private enum DrawerSection
    {
        Subtitles,
        Audio,
        Speed,
        Info,
    }
}