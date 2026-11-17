using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using MiruPlay.Windows.Models;
using MiruPlay.Windows.Services;

namespace MiruPlay.Windows.Tv;

public sealed class TvShellOptions
{
    public bool DemoMode { get; init; }
    public bool LoadLibrary { get; init; } = true;
    public TvAppContext? Context { get; init; }
    public Action? ExitRequested { get; init; }
}

/// <summary>
/// Full-screen, remote-first shell. Owns the left navigation rail, the screen
/// stack, key routing and the layered OSD window used by the player.
/// </summary>
public sealed partial class TvShellWindow : Window, IDisposable
{
    private const double CollapsedNavWidth = 72;
    private const double ExpandedNavWidth = 200;

    private readonly TvShellOptions _options;
    private readonly TvAppContext _context;
    private readonly TvNavigator _navigator = new();
    private readonly TvInputService _input;
    private readonly Dictionary<TvScreenId, TvScreenBase> _persistentScreens = [];
    private readonly List<NavItem> _navItems = [];
    private readonly DispatcherTimer _toastTimer;
    private readonly DispatcherTimer _clockTimer;
    private readonly DispatcherTimer _exitOverlayTimer;
    private TvOsdWindow? _osd;
    private TvScreenBase? _currentScreen;
    private TvDetailScreen? _detailScreen;
    private TvPlayerScreen? _playerScreen;
    private int _navIndex;
    private bool _navFocused;
    private bool _libraryLoaded;
    private bool _loadFailed;
    private bool _disposed;
    private bool _closingForExit;
    private bool _exitOverlayVisible;
    private Action? _exitRequested;
    private int _exitOverlayTicks;

    public TvShellWindow() : this(new TvShellOptions()) { }

    public TvShellWindow(TvShellOptions options)
    {
        _options = options;
        _exitRequested = options.ExitRequested;
        _context = options.Context ?? new TvAppContext();
        InitializeComponent();
        TvTheme.Apply(this);
        _input = new TvInputService(
            TvKeyMap.ParsePreset(_context.Settings.TvRemotePreset),
            _context.Settings.TvExitLongPressMs);
        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.6) };
        _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); Toast.Visibility = Visibility.Collapsed; };
        _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _clockTimer.Tick += (_, _) => UpdateClock();
        _exitOverlayTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(40) };
        _exitOverlayTimer.Tick += (_, _) => UpdateExitOverlay();
        BuildNavigation();
        _input.ActionInvoked += Input_ActionInvoked;
        _input.BackHoldStarted += (_, _) => ShowExitHoldOverlay();
        _input.BackHoldProgressChanged += (_, args) => UpdateExitHoldProgress(args.Progress);
        _input.BackHoldCompleted += (_, _) =>
        {
            HideExitHoldOverlay();
            ExitTvMode();
        };
        PreviewKeyDown += Shell_PreviewKeyDown;
        Loaded += Shell_Loaded;
        Closing += Shell_Closing;
        Activated += (_, _) => RestoreFocus();
        SizeChanged += (_, _) => UpdateOsdGeometry();
    }

    internal TvNavigator Navigator => _navigator;

    internal TvScreenBase? CurrentScreen => _currentScreen;

    internal TvAppContext Context => _context;

    internal TvInputService Input => _input;

    internal TvOsdWindow? Osd => _osd;

    /// <summary>Tangent for automated tests and the demo key overlay.</summary>
    internal void SimulateAction(TvInputAction action) => DispatchAction(action);

    private void Shell_Loaded(object sender, RoutedEventArgs e)
    {
        if (_libraryLoaded) return;
        FitToPrimaryScreen();
        Mouse.OverrideCursor = Cursors.None;
        ClockText.Visibility = _context.Settings.TvClockVisible ? Visibility.Visible : Visibility.Collapsed;
        UpdateClock();
        _clockTimer.Start();
        if (_options.DemoMode) DemoBadge.Visibility = Visibility.Visible;
        FocusRoot();
        _ = LoadLibraryAsync();
    }

    private async Task LoadLibraryAsync()
    {
        _libraryLoaded = false;
        _loadFailed = false;
        ShowLoading();
        try
        {
            if (_options.LoadLibrary) await _context.LoadAsync();
            _libraryLoaded = true;
            HideLoading();
            if (_navigator.Current == TvScreenId.Home) ShowCurrentScreen(activate: true);
            else NavigateTo(TvScreenId.Home);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or Microsoft.Data.Sqlite.SqliteException
            or NotSupportedException or ArgumentException or UnauthorizedAccessException)
        {
            _loadFailed = true;
            ShowLoadError(error);
        }
    }

    private void FitToPrimaryScreen()
    {
        WindowState = WindowState.Normal;
        Left = 0;
        Top = 0;
        Width = SystemParameters.PrimaryScreenWidth;
        Height = SystemParameters.PrimaryScreenHeight;
        Topmost = true;
    }

    private void BuildNavigation()
    {
        AddNavItem(TvScreenId.Home, "\uE80F", "tv.nav.home");
        AddNavItem(TvScreenId.Library, "\uE8F1", "tv.nav.library");
        AddNavItem(TvScreenId.Search, "\uE721", "tv.nav.search");
        AddNavItem(TvScreenId.Settings, "\uE713", "tv.nav.settings");
        AddNavItem(null, "\uE7E8", "tv.nav.exit");
    }

    private void AddNavItem(TvScreenId? screen, string glyph, string locKey)
    {
        var label = new TextBlock
        {
            Text = AppStrings.Get(locKey),
            FontSize = 19,
            Margin = new Thickness(18, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
        };
        var icon = new TextBlock
        {
            Text = glyph,
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 21,
            Foreground = (Brush)FindResource("TvTextBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            Width = 26,
            TextAlignment = TextAlignment.Center,
        };
        var content = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        content.Children.Add(icon);
        content.Children.Add(label);
        var button = new Button
        {
            Style = TvTheme.Style(this, "TvNavButton"),
            Content = content,
            FocusVisualStyle = null,
        };
        System.Windows.Automation.AutomationProperties.SetName(button, AppStrings.Get(locKey));
        button.Click += (_, _) => ActivateNavItem(screen);
        button.GotKeyboardFocus += (_, _) =>
        {
            _navFocused = true;
            _navIndex = _navItems.IndexOf(_navItems.First(item => ReferenceEquals(item.Button, button)));
            ExpandNavigation(true);
            _input.CancelBackHold();
            UpdateHint();
        };
        NavItemsPanel.Children.Add(button);
        _navItems.Add(new NavItem(screen, button, label, icon));
    }

    private sealed record NavItem(TvScreenId? Screen, Button Button, TextBlock Label, TextBlock Icon);

    private void ActivateNavItem(TvScreenId? screen)
    {
        if (screen is null)
        {
            ExitTvMode();
            return;
        }
        _navFocused = false;
        NavigateTo(screen.Value);
    }

    private async void NavigateTo(TvScreenId screen)
    {
        if (screen == TvScreenId.Player) return;
        if (_playerScreen is not null) await StopPlaybackInternalAsync();
        _navigator.ResetTo(screen);
        ShowCurrentScreen(activate: true, focusContent: true);
    }

    internal void NavigateToLibrary(string? genre = null)
    {
        NavigateTo(TvScreenId.Library);
        if (_persistentScreens.TryGetValue(TvScreenId.Library, out var screen) && screen is TvLibraryScreen library)
            library.NavigateWithGenre(genre);
    }

    internal void OnTvSettingsChanged()
    {
        ClockText.Visibility = _context.Settings.TvClockVisible ? Visibility.Visible : Visibility.Collapsed;
        UpdateClock();
    }

    internal void ProcessRawKeyDown(Key key)
    {
        if (_input.HandleKeyDown(key, ModifierKeys.None)) { }
    }

    internal void ProcessRawKeyUp(Key key) => _input.HandleKeyUp(key);

    internal void ApplyRemoteSearchText(string text)
    {
        if (_currentScreen is TvSearchScreen search) search.SetRemoteText(text);
    }

    internal bool IsPlayerActive => _playerScreen is not null;

    internal string CurrentScreenName => _navigator.Current.ToString();

    private void ShowCurrentScreen(bool activate, bool focusContent = false)
    {
        var id = _navigator.Current;
        if (id is TvScreenId.Detail or TvScreenId.Player)
        {
            if (id == TvScreenId.Detail && _detailScreen is not null) SetCurrentScreen(_detailScreen, activate, focusContent);
            else if (id == TvScreenId.Player && _playerScreen is not null) SetCurrentScreen(_playerScreen, activate, focusContent);
            return;
        }
        if (!_persistentScreens.TryGetValue(id, out var screen))
        {
            screen = CreateScreen(id);
            _persistentScreens[id] = screen;
        }
        SetCurrentScreen(screen, activate, focusContent);
    }

    private TvScreenBase CreateScreen(TvScreenId id) => id switch
    {
        TvScreenId.Home => new TvHomeScreen(_context, this),
        TvScreenId.Library => new TvLibraryScreen(_context, this),
        TvScreenId.Search => new TvSearchScreen(_context, this),
        TvScreenId.Settings => new TvSettingsScreen(_context, this),
        _ => throw new InvalidOperationException($"Screen {id} is not persistent."),
    };

    private void SetCurrentScreen(TvScreenBase screen, bool activate, bool focusContent)
    {
        if (!ReferenceEquals(_currentScreen, screen) && _currentScreen is not null)
            _navigator.RememberFocus(_currentScreen.Id, _currentScreen.OnDeactivating());
        _currentScreen = screen;
        ScreenHost.Content = screen;
        _navFocused = false;
        ExpandNavigation(false);
        UpdateNavSelection();
        var immersive = screen.Id == TvScreenId.Player;
        NavRail.Visibility = immersive ? Visibility.Collapsed : Visibility.Visible;
        HintBar.Visibility = immersive ? Visibility.Collapsed : Visibility.Visible;
        if (activate)
        {
            var memory = _navigator.RecallFocus(screen.Id);
            screen.OnActivated(memory);
            if (focusContent) screen.FocusFromNav();
        }
        UpdateHint();
    }

    private void UpdateNavSelection()
    {
        foreach (var item in _navItems)
        {
            var selected = item.Screen is not null && item.Screen == _navigator.Current;
            item.Button.Tag = selected ? "selected" : null;
            item.Icon.Foreground = selected
                ? (Brush)FindResource("TvAccentBrush")
                : (Brush)FindResource("TvTextBrush");
        }
    }

    private void ExpandNavigation(bool expanded)
    {
        if (_currentScreen?.Id == TvScreenId.Player) return;
        var target = expanded ? ExpandedNavWidth : CollapsedNavWidth;
        foreach (var item in _navItems) item.Label.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        var animation = new DoubleAnimation(target, TimeSpan.FromMilliseconds(140))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        NavRail.BeginAnimation(WidthProperty, animation);
    }

    private void Shell_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_exitOverlayVisible) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (_input.HandleKeyDown(key, Keyboard.Modifiers)) e.Handled = true;
    }

    private void Input_ActionInvoked(object? sender, TvInputActionEventArgs e)
    {
        if (_options.DemoMode) ShowDemoBadge(e.Action);
        DispatchAction(e.Action);
    }

    internal void DispatchAction(TvInputAction action)
    {
        if (_disposed) return;
        if (_exitOverlayVisible) return;
        if (_loadFailed)
        {
            if (action == TvInputAction.Select) _ = LoadLibraryAsync();
            else if (action == TvInputAction.Back) ExitTvMode();
            return;
        }
        if (!_libraryLoaded) return;

        if (_navFocused)
        {
            HandleNavAction(action);
            return;
        }

        if (_currentScreen?.HandleAction(action) == true)
        {
            UpdateHint();
            return;
        }

        switch (action)
        {
            case TvInputAction.Back:
                HandleBack();
                break;
            case TvInputAction.Home:
                NavigateTo(TvScreenId.Home);
                break;
            case TvInputAction.Menu when _currentScreen?.HandleMenu() == true:
                break;
            case TvInputAction.Left:
            case TvInputAction.Up:
                FocusNav();
                break;
            case TvInputAction.PlayPause when _currentScreen?.Id == TvScreenId.Player:
                break;
            default:
                break;
        }
        UpdateHint();
    }

    private void HandleNavAction(TvInputAction action)
    {
        switch (action)
        {
            case TvInputAction.Up:
                _navIndex = (_navIndex - 1 + _navItems.Count) % _navItems.Count;
                _navItems[_navIndex].Button.Focus();
                break;
            case TvInputAction.Down:
                _navIndex = (_navIndex + 1) % _navItems.Count;
                _navItems[_navIndex].Button.Focus();
                break;
            case TvInputAction.Select:
                ActivateNavItem(_navItems[_navIndex].Screen);
                break;
            case TvInputAction.Right:
                _navFocused = false;
                ExpandNavigation(false);
                _currentScreen?.FocusFromNav();
                break;
            case TvInputAction.Back:
                _navFocused = false;
                ExpandNavigation(false);
                _currentScreen?.FocusFromNav();
                break;
            case TvInputAction.Home:
                _navFocused = false;
                NavigateTo(TvScreenId.Home);
                break;
        }
    }

    private void FocusNav()
    {
        if (_navItems.Count == 0) return;
        _navFocused = true;
        var selected = _navItems.FindIndex(item => item.Screen == _navigator.Current);
        _navIndex = selected >= 0 ? selected : 0;
        ExpandNavigation(true);
        _navItems[_navIndex].Button.Focus();
        UpdateHint();
    }

    private void FocusRoot()
    {
        RootGrid.Focus();
        FocusManager.SetFocusedElement(this, RootGrid);
    }

    private void RestoreFocus()
    {
        if (_disposed) return;
        if (_navFocused && _navIndex >= 0 && _navIndex < _navItems.Count)
        {
            _navItems[_navIndex].Button.Focus();
            return;
        }
        if (_currentScreen?.ActiveZone is null) _currentScreen?.FocusInitial();
    }

    private void HandleBack()
    {
        if (_currentScreen is { } screen && screen.HandleBack()) return;
        if (_navigator.CanGoBack)
        {
            var leaving = _currentScreen;
            _navigator.Pop();
            if (leaving is TvDetailScreen detail)
            {
                _detailScreen = null;
                detail.DisposeScreen();
            }
            ShowCurrentScreen(activate: true, focusContent: true);
            return;
        }
        if (_currentScreen?.Id == TvScreenId.Home) ShowToast(AppStrings.Get("tv.toast.atHome"));
        else NavigateTo(TvScreenId.Home);
    }

    internal async void ShowDetail(LibrarySeries series)
    {
        if (_playerScreen is not null) await StopPlaybackInternalAsync();
        if (_navigator.Current == TvScreenId.Detail)
        {
            _detailScreen?.DisposeScreen();
            _navigator.Pop();
        }
        _detailScreen = new TvDetailScreen(_context, this, series);
        _navigator.Push(TvScreenId.Detail);
        ShowCurrentScreen(activate: true, focusContent: true);
    }

    internal async void StartPlayback(LibrarySeries series, LibraryEpisode episode, bool resume)
    {
        if (_playerScreen is not null) await StopPlaybackInternalAsync();
        _navigator.Push(TvScreenId.Player);
        _playerScreen = new TvPlayerScreen(_context, this);
        ShowCurrentScreen(activate: true, focusContent: false);
        await _playerScreen.BeginAsync(series, episode, resume);
    }

    /// <summary>Back/Stop from the player: dispose the session and return to the previous screen.</summary>
    internal async void ExitPlaybackAsync()
    {
        if (_playerScreen is null) return;
        await StopPlaybackInternalAsync();
        ShowCurrentScreen(activate: true, focusContent: true);
        if (_currentScreen is TvDetailScreen detail) detail.RefreshFromContext();
    }

    private async Task StopPlaybackInternalAsync()
    {
        var player = _playerScreen;
        _playerScreen = null;
        if (player is not null) await player.DisposeScreenAsync();
        if (_navigator.Current == TvScreenId.Player) _navigator.Pop();
        _context.RefreshProgress();
    }

    internal void ShowSearch() => NavigateTo(TvScreenId.Search);

    internal void ShowSettings() => NavigateTo(TvScreenId.Settings);

    internal void UpdateHint() =>
        HintTextBlock.Text = _navFocused ? AppStrings.Get("tv.hint.nav") : _currentScreen?.HintText ?? string.Empty;

    internal void ShowToast(string text)
    {
        ToastText.Text = text;
        Toast.Visibility = Visibility.Visible;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    internal void ExitTvMode()
    {
        if (_closingForExit || _disposed) return;
        _closingForExit = true;
        _exitRequested?.Invoke();
        Close();
    }

    internal void SetExitRequested(Action exitRequested) => _exitRequested = exitRequested;

    private void Shell_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_closingForExit && _exitRequested is not null)
        {
            e.Cancel = true;
            ExitTvMode();
            return;
        }
        Dispose();
    }

    /// <summary>Releases timers/input/OSD/screens; safe to call more than once.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _clockTimer.Stop();
        _toastTimer.Stop();
        _exitOverlayTimer.Stop();
        _input.Dispose();
        Mouse.OverrideCursor = null;
        _osd?.Close();
        _osd = null;
        if (_playerScreen is { } player)
        {
            _playerScreen = null;
            _ = player.DisposeScreenAsync();
        }
        _detailScreen?.DisposeScreen();
        foreach (var screen in _persistentScreens.Values)
            if (screen is IDisposable disposable) disposable.Dispose();
        _ = _context.DisposeAsync().AsTask();
        GC.SuppressFinalize(this);
    }

    private void ShowLoading()
    {
        LoadingPanel.Children.Clear();
        var title = new TextBlock
        {
            Text = AppStrings.Get("tv.loading"),
            FontSize = 30,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        var detail = new TextBlock
        {
            Text = AppStrings.Get("tv.loading.detail"),
            FontSize = 18,
            Margin = new Thickness(0, 12, 0, 0),
            Foreground = (Brush)FindResource("TvTextMutedBrush"),
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        LoadingPanel.Children.Add(title);
        LoadingPanel.Children.Add(detail);
        LoadingOverlay.Visibility = Visibility.Visible;
    }

    private void HideLoading() => LoadingOverlay.Visibility = Visibility.Collapsed;

    private void ShowLoadError(Exception error)
    {
        LoadingPanel.Children.Clear();
        var title = new TextBlock
        {
            Text = AppStrings.Get("tv.error.library"),
            FontSize = 30,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        var detail = new TextBlock
        {
            Text = error.Message,
            FontSize = 18,
            MaxWidth = 900,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 14, 0, 24),
            Foreground = (Brush)FindResource("TvTextMutedBrush"),
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        var retry = new Button
        {
            Content = AppStrings.Get("tv.error.retry"),
            Style = TvTheme.Style(this, "TvPrimaryButton"),
            HorizontalAlignment = HorizontalAlignment.Center,
            FocusVisualStyle = null,
        };
        retry.Click += (_, _) => _ = LoadLibraryAsync();
        var hint = new TextBlock
        {
            Text = AppStrings.Get("tv.error.hint"),
            FontSize = 16,
            Margin = new Thickness(0, 18, 0, 0),
            Foreground = (Brush)FindResource("TvTextMutedBrush"),
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        LoadingPanel.Children.Add(title);
        LoadingPanel.Children.Add(detail);
        LoadingPanel.Children.Add(retry);
        LoadingPanel.Children.Add(hint);
        LoadingOverlay.Visibility = Visibility.Visible;
        retry.Focus();
    }

    private void ShowExitHoldOverlay()
    {
        _exitOverlayVisible = true;
        ExitHoldOverlay.Visibility = Visibility.Visible;
        ExitHoldTitle.Text = AppStrings.Get("tv.exit.hold");
        ExitHoldHint.Text = AppStrings.Get("tv.exit.hint");
        ExitHoldProgressBar.Value = 0;
        _exitOverlayTicks = 0;
        _exitOverlayTimer.Start();
    }

    private void UpdateExitHoldProgress(double progress)
    {
        ExitHoldProgressBar.Value = Math.Clamp(progress, 0, 1) * 100;
    }

    private void UpdateExitOverlay()
    {
        ExitHoldProgressBar.Value = _input.BackHoldProgress * 100;
        if (_exitOverlayVisible && !_input.IsBackHeld)
        {
            HideExitHoldOverlay();
            return;
        }
        _exitOverlayTicks++;
    }

    private void HideExitHoldOverlay()
    {
        _exitOverlayVisible = false;
        _exitOverlayTimer.Stop();
        ExitHoldOverlay.Visibility = Visibility.Collapsed;
    }

    private void ShowDemoBadge(TvInputAction action)
    {
        if (!_options.DemoMode) return;
        DemoBadgeText.Text = TvActionLabel(action);
        DemoBadge.Visibility = Visibility.Visible;
    }

    internal static string TvActionLabel(TvInputAction action) => action switch
    {
        TvInputAction.Up => "▲ " + AppStrings.Get("tv.key.up"),
        TvInputAction.Down => "▼ " + AppStrings.Get("tv.key.down"),
        TvInputAction.Left => "◀ " + AppStrings.Get("tv.key.left"),
        TvInputAction.Right => "▶ " + AppStrings.Get("tv.key.right"),
        TvInputAction.Select => "OK " + AppStrings.Get("tv.key.select"),
        TvInputAction.Back => "↩ " + AppStrings.Get("tv.key.back"),
        TvInputAction.Home => "⌂ " + AppStrings.Get("tv.key.home"),
        TvInputAction.Menu => "☰ " + AppStrings.Get("tv.key.menu"),
        TvInputAction.PlayPause => "⏯ " + AppStrings.Get("tv.key.playPause"),
        TvInputAction.SeekBack => "⏪ " + AppStrings.Get("tv.key.seekBack"),
        TvInputAction.SeekForward => "⏩ " + AppStrings.Get("tv.key.seekForward"),
        TvInputAction.PrevEpisode => "⏮ " + AppStrings.Get("tv.key.prev"),
        TvInputAction.NextEpisode => "⏭ " + AppStrings.Get("tv.key.next"),
        _ => action.ToString(),
    };

    private void UpdateClock()
    {
        ClockText.Text = DateTime.Now.ToString("HH:mm", System.Globalization.CultureInfo.CurrentCulture);
        ClockText.Visibility = _context.Settings.TvClockVisible ? Visibility.Visible : Visibility.Collapsed;
    }

    internal void UpdateOsdGeometry()
    {
        if (_osd is null) return;
        _osd.SyncGeometry(this);
    }

    internal TvOsdWindow EnsureOsd()
    {
        _osd ??= new TvOsdWindow();
        if (!_osd.IsVisible)
        {
            _osd.Show();
            _osd.SyncGeometry(this);
        }
        else
        {
            _osd.SyncGeometry(this);
        }
        return _osd;
    }

    internal void HideOsd()
    {
        _osd?.Hide();
    }
}
