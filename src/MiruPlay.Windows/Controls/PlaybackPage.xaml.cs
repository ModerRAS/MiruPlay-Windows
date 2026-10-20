using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using MiruPlay.Windows.Models;
using MiruPlay.Windows.Services;

namespace MiruPlay.Windows.Controls;

public sealed class PlaybackSeekEventArgs(long value, bool isAbsolute) : EventArgs
{
    public long Value { get; } = value;
    public bool IsAbsolute { get; } = isAbsolute;
}

public sealed class PlaybackTrackEventArgs(int? trackId) : EventArgs
{
    public int? TrackId { get; } = trackId;
}

public sealed class PlaybackEpisodeEventArgs(LibraryEpisode episode) : EventArgs
{
    public LibraryEpisode Episode { get; } = episode;
}

public sealed class PlaybackInputEventArgs(PlaybackInputCommand command) : EventArgs
{
    public PlaybackInputCommand Command { get; } = command;
}

public sealed class PlaybackPageActionEventArgs(PlaybackPageAction action) : EventArgs
{
    public PlaybackPageAction Action { get; } = action;
}

public partial class PlaybackPage : UserControl
{
    private static readonly PlaybackSubtitleOption NoSubtitles = new(null, AppStrings.Get("player.noSubs"));
    private readonly DispatcherTimer _chromeTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DispatcherTimer _osdTimer = new() { Interval = TimeSpan.FromSeconds(1.5) };
    private readonly Stopwatch _activity = Stopwatch.StartNew();
    private PlaybackPageViewState? _state;
    private LibrarySeries? _series;
    private LibraryEpisode? _episode;
    private MpvPlaybackQueue? _queue;
    private bool _isPresenting;
    private bool _drawerAllowed = true;
    private bool _chromeVisible = true;
    private long _requestId;

    public PlaybackPage()
    {
        InitializeComponent();
        _chromeTimer.Tick += ChromeTimer_Tick;
        _osdTimer.Tick += OsdTimer_Tick;
        MpvHost.PointerActivity += MpvHost_PointerActivity;
        MpvHost.VideoDoubleClicked += MpvHost_VideoDoubleClicked;
    }

    public MpvWindowHost VideoHost => MpvHost;

    public event EventHandler<PlaybackPageActionEventArgs>? ActionRequested;

    // Compatibility events remain until MainWindow moves to ActionRequested in the integration task.
    public event EventHandler? BackRequested;
    public event EventHandler? TogglePlaybackRequested;
    public event EventHandler? StopRequested;
    public event EventHandler<PlaybackSeekEventArgs>? SeekRequested;
    public event EventHandler? PreviousRequested;
    public event EventHandler? NextRequested;
    public event EventHandler<PlaybackEpisodeEventArgs>? EpisodeRequested;
    public event EventHandler<PlaybackTrackEventArgs>? AudioTrackRequested;
    public event EventHandler<PlaybackTrackEventArgs>? SubtitleTrackRequested;
    public event EventHandler? FullscreenRequested;
    public event EventHandler<PlaybackInputEventArgs>? InputActionRequested;
    public event EventHandler? RetryRequested;

    public void Present(PlaybackPageViewState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var keepChromeVisibility = _state?.IsFullscreen == state.IsFullscreen;
        _state = state;
        _series = state.Series;
        _episode = state.Episode;
        if (!keepChromeVisibility) _chromeVisible = state.IsChromeVisible;

        _isPresenting = true;
        try
        {
            TitleText.Text = state.Header.Title;
            MediaInfoText.Text = string.IsNullOrWhiteSpace(state.Header.MediaInfo) ? state.Header.FileName : state.Header.MediaInfo;
            Timeline.Maximum = Math.Max(1, state.Transport.DurationMs);
            Timeline.Value = Math.Clamp(state.Transport.PositionMs, 0, Timeline.Maximum);
            ElapsedText.Text = PlaybackPagePresentation.FormatTime(state.Transport.PositionMs);
            DurationText.Text = PlaybackPagePresentation.FormatTime(state.Transport.DurationMs);
            PlayPauseText.Text = state.Transport.IsPaused ? AppStrings.Get("wpf.play.play") : AppStrings.Get("wpf.play2.pause");
            PlayPauseIcon.Text = state.Transport.IsPaused ? "\uE768" : "\uE769";
            PreviousButton.IsEnabled = state.Transport.CanPlayPrevious;
            NextButton.IsEnabled = state.Transport.CanPlayNext;
            StatusText.Text = state.Error is not null
                ? AppStrings.Get("wpf.play2.playError")
                : state.Transport.IsPaused
                    ? AppStrings.Get("wpf.play2.paused")
                    : state.Transport.IsPlaying
                        ? AppStrings.Get("wpf.play.now")
                        : AppStrings.Get("wpf.play2.preparing");
            ErrorText.Text = state.Error?.Message ?? string.Empty;
            ErrorPanel.Visibility = state.Error is null ? Visibility.Collapsed : Visibility.Visible;

            SetAudioOptions(AudioTrackSelector, state.Options);
            SetAudioOptions(OverflowAudioTrackSelector, state.Options);
            SetSubtitleOptions(SubtitleTrackSelector, state.Options);
            SetSubtitleOptions(OverflowSubtitleTrackSelector, state.Options);
            SetSpeedOptions(SpeedSelector, state.Options);
            SetSpeedOptions(OverflowSpeedSelector, state.Options);
            VolumeSlider.Value = state.Options.VolumePercent;
            OverflowVolumeSlider.Value = state.Options.VolumePercent;
            VolumeText.Text = $"{state.Options.VolumePercent:0}%";
            var muteGlyph = state.Options.IsMuted || state.Options.VolumePercent <= 0 ? "\uE74F" : "\uE767";
            MuteIcon.Text = muteGlyph;
            OverflowMuteIcon.Text = muteGlyph;
            MuteButton.ToolTip = state.Options.IsMuted ? AppStrings.Get("player.unmute") : AppStrings.Get("player.mute");
            OverflowMuteButton.ToolTip = MuteButton.ToolTip;

            EpisodeList.ItemsSource = state.EpisodeGroups;
            QueueSummaryText.Text = AppStrings.Get("wpf.play2.queueSummary", state.EpisodeGroups.Count, state.Episode.DisplayNumber);
        }
        finally
        {
            _isPresenting = false;
        }

        ApplyLayout(state.Layout);
        ApplyFullscreen(state.IsFullscreen);
        UpdatePopupGeometry();
    }

    public void Prepare(LibrarySeries? series, MpvPlaybackQueue? queue, LibraryEpisode episode)
    {
        _series = series;
        _queue = queue;
        _episode = episode;
        Present(PlaybackPagePresentation.BuildViewState(
            series,
            queue,
            episode,
            info: null,
            errorMessage: null,
            AvailableWidth(),
            isFullscreen: false,
            isChromeVisible: true));
    }

    public void UpdateSession(IPlaybackSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _episode ??= session.Episode;
        Present(PlaybackPagePresentation.BuildViewState(
            _series,
            _queue,
            _episode,
            session.PlaybackInfo,
            session.LastError,
            AvailableWidth(),
            _state?.IsFullscreen == true,
            _chromeVisible));
        StatusText.ToolTip = FormatNativeDiagnostics(session.GetNativeDiagnostics());
    }

    public void ShowCommandError(string message)
    {
        if (_state is null) return;
        Present(_state with { Error = new(message), IsChromeVisible = true });
        RegisterActivity();
        ShowOsd(message);
    }

    public void SetFullscreen(bool fullscreen)
    {
        if (_state is null || _state.IsFullscreen == fullscreen) return;
        _chromeVisible = true;
        Present(_state with
        {
            IsFullscreen = fullscreen,
            IsChromeVisible = true,
            Layout = PlaybackPagePresentation.ResolveLayout(AvailableWidth(), fullscreen),
        });
    }

    public void SetEpisodeSidebarVisible(bool visible)
    {
        _drawerAllowed = visible;
        EpisodeDrawerButton.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (!visible) EpisodeDrawerPopup.IsOpen = false;
    }

    public void Clear()
    {
        EpisodeDrawerPopup.IsOpen = false;
        OptionsOverflowPopup.IsOpen = false;
        FullscreenChromePopup.IsOpen = false;
        PlaybackOsdPopup.IsOpen = false;
        RestoreChromeToWindow();
        _state = null;
        _series = null;
        _episode = null;
        _queue = null;
        EpisodeList.ItemsSource = null;
        AudioTrackSelector.ItemsSource = null;
        SubtitleTrackSelector.ItemsSource = null;
        Timeline.Value = 0;
        Timeline.Maximum = 1;
        ErrorPanel.Visibility = Visibility.Collapsed;
        StatusText.Text = AppStrings.Get("wpf.play.ready");
        StatusText.ToolTip = null;
    }

    public void ClearSession() => Clear();

    private static string? FormatNativeDiagnostics(MpvNativeDiagnostics? diagnostics)
    {
        if (diagnostics is null) return null;
        var properties = string.Join(" · ", diagnostics.Properties.Select(item => $"{item.Name}={item.Value}"));
        var summary = string.IsNullOrWhiteSpace(properties) ? AppStrings.Get("diag.noProps") : properties;
        var logs = diagnostics.RecentLogs;
        return logs.Count == 0
            ? AppStrings.Get("diag.summary", summary, 0, "")
            : AppStrings.Get("diag.summary", summary, logs.Count, " · " + logs[^1].Text);
    }

    public bool FocusInitial() => PlayPauseButton.Focus();

    private static void SetAudioOptions(ComboBox selector, PlaybackOptionsViewState options)
    {
        selector.ItemsSource = options.AudioTracks;
        selector.SelectedValue = options.SelectedAudioTrackId;
        selector.IsEnabled = options.AudioTracks.Count > 0;
        AutomationProperties.SetName(selector, options.AudioTracks.Count > 0 ? AppStrings.Get("player.audio") : AppStrings.Get("wpf.play2.noAudioTrack"));
    }

    private static void SetSubtitleOptions(ComboBox selector, PlaybackOptionsViewState options)
    {
        var items = new[] { NoSubtitles }
            .Concat(options.SubtitleTracks.Select(track => new PlaybackSubtitleOption(track.Id, track.DisplayLabel)))
            .ToArray();
        selector.ItemsSource = items;
        selector.SelectedValue = options.SelectedSubtitleTrackId;
        selector.IsEnabled = options.SubtitleTracks.Count > 0;
        AutomationProperties.SetName(selector, options.SubtitleTracks.Count > 0 ? AppStrings.Get("player.subtitle") : AppStrings.Get("wpf.play2.noSubTrack"));
    }

    private static void SetSpeedOptions(ComboBox selector, PlaybackOptionsViewState options)
    {
        selector.ItemsSource = options.Speeds;
        selector.SelectedItem = options.Speeds.OrderBy(item => Math.Abs(item.Value - options.Speed)).FirstOrDefault();
    }

    private void ApplyLayout(PlaybackLayoutProfile layout)
    {
        InlineOptionsPanel.Visibility = layout.ShowInlineOptions ? Visibility.Visible : Visibility.Collapsed;
        OptionsOverflowButton.Visibility = layout.ShowInlineOptions ? Visibility.Collapsed : Visibility.Visible;
        EpisodeDrawer.Width = layout.DrawerWidth;
        TransportPanel.Margin = layout.Mode == PlaybackLayoutMode.Compact ? new Thickness(0) : new Thickness(0);
    }

    private void ApplyFullscreen(bool fullscreen)
    {
        if (fullscreen)
        {
            if (ReferenceEquals(WindowChromeHost.Child, ChromePanel))
            {
                WindowChromeHost.Child = null;
                FullscreenChromePopup.Child = ChromePanel;
            }
            WindowHeader.Visibility = Visibility.Collapsed;
            WindowChromeHost.Visibility = Visibility.Collapsed;
            Grid.SetRow(VideoBorder, 0);
            Grid.SetRowSpan(VideoBorder, 3);
            FullscreenChromePopup.IsOpen = _chromeVisible;
        }
        else
        {
            RestoreChromeToWindow();
            WindowHeader.Visibility = Visibility.Visible;
            WindowChromeHost.Visibility = Visibility.Visible;
            Grid.SetRow(VideoBorder, 1);
            Grid.SetRowSpan(VideoBorder, 1);
        }
    }

    private void RestoreChromeToWindow()
    {
        FullscreenChromePopup.IsOpen = false;
        if (ReferenceEquals(FullscreenChromePopup.Child, ChromePanel))
        {
            FullscreenChromePopup.Child = null;
            WindowChromeHost.Child = ChromePanel;
        }
    }

    private void UpdatePopupGeometry()
    {
        if (_state is null) return;
        EpisodeDrawer.Height = Math.Max(1, ActualHeight);
        EpisodeDrawerPopup.HorizontalOffset = Math.Max(0, ActualWidth - EpisodeDrawer.Width);
        EpisodeDrawerPopup.VerticalOffset = 0;
        ChromePanel.Width = Math.Max(1, ActualWidth);
        _ = Dispatcher.BeginInvoke(() =>
        {
            FullscreenChromePopup.HorizontalOffset = 0;
            FullscreenChromePopup.VerticalOffset = Math.Max(0, ActualHeight - ChromePanel.ActualHeight);
        }, DispatcherPriority.Loaded);
    }

    private double AvailableWidth() => ActualWidth > 0 ? ActualWidth : 1200;

    private void RaiseAction(PlaybackPageAction action) => ActionRequested?.Invoke(this, new(action));

    private long NextRequestId() => Interlocked.Increment(ref _requestId);

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        RaiseAction(new PlaybackBackAction());
        BackRequested?.Invoke(this, EventArgs.Empty);
    }

    private void PlayPause_Click(object sender, RoutedEventArgs e)
    {
        RaiseAction(new PlaybackToggleAction());
        TogglePlaybackRequested?.Invoke(this, EventArgs.Empty);
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        RaiseAction(new PlaybackStopAction());
        StopRequested?.Invoke(this, EventArgs.Empty);
    }

    private void Previous_Click(object sender, RoutedEventArgs e)
    {
        RaiseAction(new PlaybackPreviousAction());
        PreviousRequested?.Invoke(this, EventArgs.Empty);
    }

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        RaiseAction(new PlaybackNextAction());
        NextRequested?.Invoke(this, EventArgs.Empty);
    }

    private void BackSeek_Click(object sender, RoutedEventArgs e) => RaiseSeek(-10_000, false);

    private void ForwardSeek_Click(object sender, RoutedEventArgs e) => RaiseSeek(30_000, false);

    private void RaiseSeek(long value, bool absolute)
    {
        RaiseAction(new PlaybackSeekAction(value, absolute));
        SeekRequested?.Invoke(this, new(value, absolute));
    }

    private void Fullscreen_Click(object sender, RoutedEventArgs e)
    {
        RaiseAction(new PlaybackToggleFullscreenAction());
        FullscreenRequested?.Invoke(this, EventArgs.Empty);
    }

    private void Retry_Click(object sender, RoutedEventArgs e)
    {
        RaiseAction(new PlaybackRetryAction());
        RetryRequested?.Invoke(this, EventArgs.Empty);
    }

    private void Episode_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: LibraryEpisode episode }) return;
        EpisodeDrawerPopup.IsOpen = false;
        RaiseAction(new PlaybackSelectEpisodeAction(episode));
        EpisodeRequested?.Invoke(this, new(episode));
    }

    private void AudioTrack_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isPresenting || sender is not ComboBox { SelectedItem: MpvAudioTrack track } selector) return;
        selector.IsEnabled = false;
        var requestId = NextRequestId();
        RaiseAction(new PlaybackSelectAudioTrackAction(track.Id, requestId));
        AudioTrackRequested?.Invoke(this, new(track.Id));
        ShowOsd(AppStrings.Get("wpf.play2.audioTrack", track.DisplayLabel));
    }

    private void SubtitleTrack_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isPresenting || sender is not ComboBox { SelectedItem: PlaybackSubtitleOption option } selector) return;
        selector.IsEnabled = false;
        var requestId = NextRequestId();
        RaiseAction(new PlaybackSelectSubtitleTrackAction(option.TrackId, requestId));
        SubtitleTrackRequested?.Invoke(this, new(option.TrackId));
        ShowOsd(AppStrings.Get("wpf.play2.subTrack", option.DisplayLabel));
    }

    private void Speed_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isPresenting || sender is not ComboBox { SelectedItem: PlaybackSpeedOption option } selector) return;
        selector.IsEnabled = false;
        RaiseAction(new PlaybackSetSpeedAction(option.Value, NextRequestId()));
        ShowOsd(AppStrings.Get("wpf.play2.speedRate", option.DisplayLabel));
    }

    private void Mute_Click(object sender, RoutedEventArgs e)
    {
        if (_state is null) return;
        var muted = !_state.Options.IsMuted;
        RaiseAction(new PlaybackSetMutedAction(muted, NextRequestId()));
        ShowOsd(muted ? AppStrings.Get("wpf.play2.muted") : AppStrings.Get("wpf.play2.volPercent", _state.Options.VolumePercent.ToString("0", CultureInfo.CurrentCulture)));
    }

    private void Volume_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => RaiseVolume(sender);

    private void Volume_PreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Left or Key.Right or Key.Home or Key.End or Key.PageUp or Key.PageDown) RaiseVolume(sender);
    }

    private void RaiseVolume(object sender)
    {
        if (_isPresenting || sender is not Slider slider) return;
        RaiseAction(new PlaybackSetVolumeAction(slider.Value, NextRequestId()));
        ShowOsd(AppStrings.Get("wpf.play2.volPercent", slider.Value.ToString("0", CultureInfo.CurrentCulture)));
    }

    private void Timeline_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isPresenting) RaiseSeek((long)Timeline.Value, true);
    }

    private void Timeline_PreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Left or Key.Right or Key.Home or Key.End)) return;
        RaiseSeek((long)Timeline.Value, true);
        e.Handled = true;
    }

    private void EpisodeDrawer_Click(object sender, RoutedEventArgs e)
    {
        if (!_drawerAllowed) return;
        OptionsOverflowPopup.IsOpen = false;
        EpisodeDrawerPopup.IsOpen = !EpisodeDrawerPopup.IsOpen;
        RegisterActivity();
    }

    private void EpisodeDrawerClose_Click(object sender, RoutedEventArgs e) => EpisodeDrawerPopup.IsOpen = false;

    private void OptionsOverflow_Click(object sender, RoutedEventArgs e)
    {
        EpisodeDrawerPopup.IsOpen = false;
        OptionsOverflowPopup.IsOpen = !OptionsOverflowPopup.IsOpen;
        RegisterActivity();
    }

    private void PlaybackRoot_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        RegisterActivity();
        var focused = Keyboard.FocusedElement;
        var context = focused switch
        {
            TextBoxBase or PasswordBox => PlaybackInputContext.TextEntry,
            ComboBox => PlaybackInputContext.SelectionControl,
            Slider => PlaybackInputContext.Slider,
            ButtonBase => PlaybackInputContext.Button,
            _ => PlaybackInputContext.Page,
        };
        var selectionOpen = focused is ComboBox { IsDropDownOpen: true };
        var decision = PlaybackInputMap.Resolve(e.Key, Keyboard.Modifiers, context, selectionOpen);
        if (!decision.Handled) return;

        InputActionRequested?.Invoke(this, new(decision.Command));
        switch (decision.Command.Action)
        {
            case PlaybackInputAction.TogglePlayback:
                RaiseAction(new PlaybackToggleAction());
                break;
            case PlaybackInputAction.SeekRelative:
                RaiseAction(new PlaybackSeekAction(decision.Command.DeltaMs, false));
                break;
            case PlaybackInputAction.ToggleFullscreen:
                RaiseAction(new PlaybackToggleFullscreenAction());
                break;
            case PlaybackInputAction.ExitFullscreen:
                RaiseAction(new PlaybackExitFullscreenAction());
                break;
            case PlaybackInputAction.ToggleMute when _state is not null:
                RaiseAction(new PlaybackSetMutedAction(!_state.Options.IsMuted, NextRequestId()));
                break;
            case PlaybackInputAction.VolumeUp when _state is not null:
                RaiseAction(new PlaybackSetVolumeAction(Math.Min(100, _state.Options.VolumePercent + 5), NextRequestId()));
                break;
            case PlaybackInputAction.VolumeDown when _state is not null:
                RaiseAction(new PlaybackSetVolumeAction(Math.Max(0, _state.Options.VolumePercent - 5), NextRequestId()));
                break;
        }
        e.Handled = true;
    }

    private void PlaybackRoot_PreviewMouseMove(object sender, MouseEventArgs e) => RegisterActivity();

    private void MpvHost_PointerActivity(object? sender, EventArgs e) => Dispatcher.InvokeAsync(RegisterActivity);

    private void MpvHost_VideoDoubleClicked(object? sender, EventArgs e) => Dispatcher.InvokeAsync(() =>
    {
        RegisterActivity();
        RaiseAction(new PlaybackToggleFullscreenAction());
        FullscreenRequested?.Invoke(this, EventArgs.Empty);
    });

    private void RegisterActivity()
    {
        _activity.Restart();
        if (_state?.IsFullscreen == true && !_chromeVisible)
        {
            _chromeVisible = true;
            FullscreenChromePopup.IsOpen = true;
            UpdatePopupGeometry();
        }
    }

    private void ChromeTimer_Tick(object? sender, EventArgs e)
    {
        if (_state?.IsFullscreen != true) return;
        var show = PlaybackPagePresentation.ShouldShowChrome(new(
            IsFullscreen: true,
            IsPlaying: _state.Transport.IsPlaying,
            IsPaused: _state.Transport.IsPaused,
            HasError: _state.Error is not null,
            IsDrawerOpen: EpisodeDrawerPopup.IsOpen,
            IsOverflowOpen: OptionsOverflowPopup.IsOpen,
            IsSeeking: Timeline.IsMouseCaptureWithin,
            IsKeyboardFocusWithinChrome: ChromePanel.IsKeyboardFocusWithin,
            IdleFor: _activity.Elapsed));
        if (show == _chromeVisible) return;
        _chromeVisible = show;
        FullscreenChromePopup.IsOpen = show;
        if (show) UpdatePopupGeometry();
    }

    private void ShowOsd(string text)
    {
        PlaybackOsdText.Text = text;
        PlaybackOsdPopup.IsOpen = true;
        _osdTimer.Stop();
        _osdTimer.Start();
    }

    private void OsdTimer_Tick(object? sender, EventArgs e)
    {
        _osdTimer.Stop();
        PlaybackOsdPopup.IsOpen = false;
    }

    private void PlaybackRoot_Loaded(object sender, RoutedEventArgs e)
    {
        _chromeTimer.Start();
        UpdatePopupGeometry();
    }

    private void PlaybackRoot_Unloaded(object sender, RoutedEventArgs e)
    {
        _chromeTimer.Stop();
        _osdTimer.Stop();
        EpisodeDrawerPopup.IsOpen = false;
        OptionsOverflowPopup.IsOpen = false;
        FullscreenChromePopup.IsOpen = false;
        PlaybackOsdPopup.IsOpen = false;
    }

    private void PlaybackRoot_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_state is not null)
        {
            _state = _state with { Layout = PlaybackPagePresentation.ResolveLayout(e.NewSize.Width, _state.IsFullscreen) };
            ApplyLayout(_state.Layout);
        }
        UpdatePopupGeometry();
    }

    private void EpisodeDrawerPopup_Closed(object? sender, EventArgs e) => RegisterActivity();

    private void OptionsOverflowPopup_Closed(object? sender, EventArgs e) => RegisterActivity();

    private sealed record PlaybackSubtitleOption(int? TrackId, string DisplayLabel);
}
