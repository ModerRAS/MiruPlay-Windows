using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MiruPlay.Windows.Models;
using MiruPlay.Windows.Services;

namespace MiruPlay.Windows.Tv;

/// <summary>
/// Single "新番" destination with two modes (时间表 default / 季番):
/// - 时间表: Bangumi weekly calendar (last/this/next week, today highlighted).
/// - 季番: local library poster wall grouped by broadcast season then weekday.
/// Both modes share the top mode tabs; ←→ switches mode, ↓ enters content.
/// </summary>
public sealed class TvSeasonScreen : TvScreenBase, IDisposable
{
    private const int Columns = 6;
    private const double SeasonPosterWidth = 170;
    private const double SeasonPosterHeight = 254;
    private const double SeasonPlaceholderHeight = 330;
    private const double SchedulePosterWidth = 110;
    private const double SchedulePosterHeight = 160;
    private static readonly Dictionary<string, ImageSource?> PosterCache = new(StringComparer.OrdinalIgnoreCase);

    private enum NewSeasonMode { Schedule, Season }

    private readonly TvAppContext _context;
    private readonly TvShellWindow _shell;
    private readonly StackPanel _modeTabs = new() { Orientation = Orientation.Horizontal };
    private readonly List<Button> _modeButtons = [];
    private readonly StackPanel _weekTabs = new() { Orientation = Orientation.Horizontal };
    private readonly List<Button> _weekButtons = [];
    private readonly TextBlock _status = new();
    private Border _weekHost = null!;
    private readonly StackPanel _seasonGroups = new() { Margin = new Thickness(64, 0, 64, 40) };
    private readonly StackPanel _scheduleBody = new() { Margin = new Thickness(64, 8, 64, 40) };
    private readonly ScrollViewer _seasonScroll;
    private readonly ScrollViewer _scheduleScroll;
    private readonly List<FrameworkElement> _seasonCards = [];
    private readonly List<(TvPosterCard Card, string? Path)> _seasonPosterSources = [];
    private readonly List<TvZone> _scheduleRowZones = [];
    private readonly TvBangumiCalendarService _calendar;
    private readonly AirRadarScheduleService _airRadar;
    private IReadOnlyList<LibrarySeries>? _seasonBuiltFor;
    private int _seasonBuildVersion;
    private TvZone? _modeZone;
    private TvZone? _weekZone;
    private TvZone? _seasonZone;
    private TvBangumiCalendarData? _calendarData;
    private NewSeasonMode _mode = NewSeasonMode.Schedule;
    private int _weekOffset;
    private bool _calendarLoaded;
    private bool _calendarLoading;
    private string? _calendarError;
    private bool _calendarStale;
    private bool _airRadarLoading;
    private AirRadarScheduleData? _airRadarData;
    private string? _airRadarError;
    private bool _airRadarStale;
    private bool _scheduleLoadStarted;
    private string _scheduleUrlLoaded = string.Empty;
    private bool _disposed;

    public TvSeasonScreen(TvAppContext context, TvShellWindow shell)
    {
        _context = context;
        _shell = shell;
        TvTheme.Apply(this);
        Background = (Brush)FindResource("TvBackgroundBrush");

        var config = new CloudDriveAutomationStore().Load();
        _calendar = new TvBangumiCalendarService(
            handler: OutboundHttpProxy.CreateHandler(config.RssProxyEnabled, config.RssProxyHost, config.RssProxyPort));
        _airRadar = new AirRadarScheduleService(
            () => _context.Settings.ScheduleDataSourceUrl,
            handler: OutboundHttpProxy.CreateHandler(config.RssProxyEnabled, config.RssProxyHost, config.RssProxyPort));

        var header = new StackPanel { Margin = new Thickness(64, 26, 64, 0) };
        header.Children.Add(new TextBlock
        {
            Text = AppStrings.Get("tv.season.title"),
            Style = TvTheme.Style(this, "TvPageTitleText"),
        });
        for (var index = 0; index < 2; index++)
        {
            var mode = index == 0 ? NewSeasonMode.Schedule : NewSeasonMode.Season;
            var button = new Button
            {
                Content = AppStrings.Get(mode == NewSeasonMode.Schedule ? "tv.season.tab.schedule" : "tv.season.tab.season"),
                Style = TvTheme.Style(this, "TvChipButton"),
                FocusVisualStyle = null,
                Margin = new Thickness(0, 0, 14, 0),
            };
            button.Click += (_, _) => SwitchMode(mode);
            _modeButtons.Add(button);
            _modeTabs.Children.Add(button);
        }
        header.Children.Add(new Border { Margin = new Thickness(0, 10, 0, 0), Child = _modeTabs });
        for (var index = 0; index < 3; index++)
        {
            var offset = index - 1;
            var button = new Button
            {
                Content = AppStrings.Get(offset switch
                {
                    -1 => "tv.schedule.lastWeek",
                    0 => "tv.schedule.thisWeek",
                    _ => "tv.schedule.nextWeek",
                }),
                Style = TvTheme.Style(this, "TvChipButton"),
                FocusVisualStyle = null,
                Margin = new Thickness(0, 0, 14, 0),
            };
            button.Click += (_, _) => ChangeWeek(offset);
            _weekButtons.Add(button);
            _weekTabs.Children.Add(button);
        }
        _weekHost = new Border { Margin = new Thickness(0, 8, 0, 0), Child = _weekTabs };
        header.Children.Add(_weekHost);
        _status.FontSize = 16;
        _status.Foreground = (Brush)FindResource("TvTextMutedBrush");
        _status.Margin = new Thickness(0, 10, 0, 4);
        _status.TextWrapping = TextWrapping.Wrap;
        header.Children.Add(_status);

        _seasonScroll = new ScrollViewer
        {
            Style = TvTheme.Style(this, "TvVerticalScrollViewerStyle"),
            Content = _seasonGroups,
            Visibility = Visibility.Collapsed,
        };
        _scheduleScroll = new ScrollViewer
        {
            Style = TvTheme.Style(this, "TvVerticalScrollViewerStyle"),
            Content = _scheduleBody,
            Visibility = Visibility.Visible,
        };
        var body = new Grid();
        body.Children.Add(_seasonScroll);
        body.Children.Add(_scheduleScroll);
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.Children.Add(header);
        Grid.SetRow(body, 1);
        root.Children.Add(body);
        Content = root;

        _modeZone = new TvZone("newseason-tabs", TvZoneKind.Horizontal, _modeButtons.Cast<FrameworkElement>().ToList());
        _weekZone = new TvZone("newseason-weeks", TvZoneKind.Horizontal, _weekButtons.Cast<FrameworkElement>().ToList());
        RegisterZones(_modeZone, _weekZone);
        UpdateModeUi();
    }

    public override TvScreenId Id => TvScreenId.Season;

    public override string HintText => AppStrings.Get("tv.hint.season");

    public override bool FocusInitial()
    {
        if (_modeZone is { Items.Count: > 0 })
        {
            FocusZone(_modeZone, (int)_mode);
            return true;
        }
        return false;
    }

    public override void OnActivated(string? focusKey)
    {
        if (focusKey?.StartsWith("season-grid", StringComparison.Ordinal) == true && _mode != NewSeasonMode.Season)
        {
            _mode = NewSeasonMode.Season;
            UpdateModeUi();
        }
        else if (focusKey?.StartsWith("schedule-day", StringComparison.Ordinal) == true && _mode != NewSeasonMode.Schedule)
        {
            _mode = NewSeasonMode.Schedule;
            UpdateModeUi();
        }
        if (_mode == NewSeasonMode.Season) EnsureSeasonBuilt();
        base.OnActivated(focusKey);
        if (_mode == NewSeasonMode.Schedule)
        {
            var url = AppSettingsStore.NormalizeScheduleDataSourceUrl(_context.Settings.ScheduleDataSourceUrl);
            if (!_scheduleLoadStarted || !string.Equals(url, _scheduleUrlLoaded, StringComparison.Ordinal))
            {
                _scheduleLoadStarted = true;
                _scheduleUrlLoaded = url;
                _ = LoadScheduleAsync();
            }
        }
    }

    public override bool HandleAction(TvInputAction action)
    {
        if (action == TvInputAction.Select && _mode == NewSeasonMode.Schedule && (_calendarError is not null || _airRadarError is not null))
        {
            _ = LoadScheduleAsync(forceRefresh: true);
            return true;
        }
        if (ActiveZone == _modeZone)
        {
            if (action == TvInputAction.Left && _mode != NewSeasonMode.Schedule)
            {
                SwitchMode(NewSeasonMode.Schedule);
                return true;
            }
            if (action == TvInputAction.Right && _mode != NewSeasonMode.Season)
            {
                SwitchMode(NewSeasonMode.Season);
                return true;
            }
            if (action == TvInputAction.Down)
            {
                if (_mode == NewSeasonMode.Schedule && _airRadarData is null) FocusZone(_weekZone!, _weekOffset + 1);
                else FocusContent();
                return true;
            }
        }
        if (ActiveZone == _weekZone)
        {
            if (action == TvInputAction.Left) { ChangeWeek(Math.Max(-1, _weekOffset - 1)); return true; }
            if (action == TvInputAction.Right) { ChangeWeek(Math.Min(1, _weekOffset + 1)); return true; }
            if (action == TvInputAction.Up) { FocusZone(_modeZone!, (int)NewSeasonMode.Schedule); return true; }
            if (action == TvInputAction.Down) { FocusRow(0, 0); return true; }
        }
        if (_mode == NewSeasonMode.Schedule)
        {
            var rowIndex = _scheduleRowZones.IndexOf(ActiveZone!);
            if (rowIndex >= 0 && action is TvInputAction.Up or TvInputAction.Down)
            {
                if (action == TvInputAction.Up && rowIndex == 0)
                {
                    if (_airRadarData is not null) FocusZone(_modeZone!, (int)NewSeasonMode.Schedule);
                    else FocusZone(_weekZone!, _weekOffset + 1);
                    return true;
                }
                var target = Math.Clamp(rowIndex + (action == TvInputAction.Down ? 1 : -1), 0, Math.Max(0, _scheduleRowZones.Count - 1));
                FocusRow(target, Math.Min(ActiveIndex, _scheduleRowZones[target].Items.Count - 1));
                return true;
            }
        }
        else if (ActiveZone == _seasonZone && action == TvInputAction.Up && ActiveIndex < Columns)
        {
            FocusZone(_modeZone!, (int)NewSeasonMode.Season);
            return true;
        }
        return base.HandleAction(action);
    }

    private void SwitchMode(NewSeasonMode mode)
    {
        _mode = mode;
        UpdateModeUi();
        if (mode == NewSeasonMode.Season) EnsureSeasonBuilt();
        if (mode == NewSeasonMode.Schedule && (!_scheduleLoadStarted || !string.Equals(
                AppSettingsStore.NormalizeScheduleDataSourceUrl(_context.Settings.ScheduleDataSourceUrl),
                _scheduleUrlLoaded, StringComparison.Ordinal)))
        {
            _scheduleLoadStarted = true;
            _scheduleUrlLoaded = AppSettingsStore.NormalizeScheduleDataSourceUrl(_context.Settings.ScheduleDataSourceUrl);
            _ = LoadScheduleAsync();
        }
        FocusZone(_modeZone!, (int)mode);
    }

    private void UpdateModeUi()
    {
        for (var index = 0; index < _modeButtons.Count; index++)
            _modeButtons[index].Tag = index == (int)_mode ? "active" : null;
        _weekHost.Visibility = _mode == NewSeasonMode.Schedule && _airRadarData is null ? Visibility.Visible : Visibility.Collapsed;
        _seasonScroll.Visibility = _mode == NewSeasonMode.Season ? Visibility.Visible : Visibility.Collapsed;
        _scheduleScroll.Visibility = _mode == NewSeasonMode.Schedule ? Visibility.Visible : Visibility.Collapsed;
        UpdateWeekSelection();
        UpdateStatusText();
    }

    private void UpdateStatusText()
    {
        if (_mode == NewSeasonMode.Season)
        {
            var count = _seasonCards.Count;
            _status.Text = count > 0 ? AppStrings.Get("tv.season.summary", count) : AppStrings.Get("tv.season.empty");
        }
        else if (_airRadarData is not null)
        {
            _status.Text = _airRadarStale
                ? AppStrings.Get("tv.schedule.airradarStale")
                : string.IsNullOrWhiteSpace(_airRadarData.Season)
                    ? string.Empty
                    : AppStrings.Get("tv.schedule.airradarSeason", _airRadarData.Season);
        }
        else if (_airRadarError is not null)
        {
            _status.Text = AppStrings.Get("tv.schedule.airradarFallback");
        }
        else if (_calendarError is not null)
        {
            _status.Text = AppStrings.Get("tv.schedule.error", _calendarError);
        }
        else if (_calendarStale)
        {
            _status.Text = AppStrings.Get("tv.schedule.stale");
        }
        else if (_calendarLoading)
        {
            _status.Text = AppStrings.Get("tv.schedule.loading");
        }
        else if (!_calendarLoaded)
        {
            _status.Text = AppStrings.Get("tv.schedule.loading");
        }
        else
        {
            _status.Text = string.Empty;
        }
        _status.Visibility = string.IsNullOrEmpty(_status.Text) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void UpdateWeekSelection()
    {
        for (var index = 0; index < _weekButtons.Count; index++)
            _weekButtons[index].Tag = index - 1 == _weekOffset ? "active" : null;
    }

    private void FocusContent()
    {
        if (_mode == NewSeasonMode.Schedule)
        {
            if (_scheduleRowZones.Count > 0) FocusRow(0, 0);
        }
        else if (_seasonZone is { Items.Count: > 0 })
        {
            FocusZone(_seasonZone, 0);
        }
    }

    private void FocusRow(int row, int column)
    {
        if (row < 0 || row >= _scheduleRowZones.Count || _scheduleRowZones[row].Items.Count == 0) return;
        FocusZone(_scheduleRowZones[row], Math.Clamp(column, 0, _scheduleRowZones[row].Items.Count - 1));
    }

    private async Task LoadScheduleAsync(bool forceRefresh = false)
    {
        var url = AppSettingsStore.NormalizeScheduleDataSourceUrl(_context.Settings.ScheduleDataSourceUrl);
        if (url.Length == 0)
        {
            await LoadCalendarAsync(forceRefresh);
            return;
        }
        if (_airRadarLoading || _calendarLoading) return;
        _airRadarLoading = true;
        _airRadarError = null;
        UpdateStatusText();
        try
        {
            var result = await _airRadar.LoadAsync(forceRefresh);
            if (_disposed) return;
            if (result.Data is null)
            {
                _airRadarData = null;
                _airRadarError = result.Error ?? "unknown";
                _shell.ShowToast(AppStrings.Get("tv.schedule.airradarFallback"));
                await LoadCalendarAsync(forceRefresh);
                return;
            }
            _airRadarData = result.Data;
            _airRadarStale = result.Stale;
            _calendarData = null;
            UpdateWeekVisibility();
            BuildSchedule();
            UpdateStatusText();
        }
        finally
        {
            _airRadarLoading = false;
            UpdateStatusText();
        }
    }

    private void UpdateWeekVisibility() =>
        _weekHost.Visibility = _mode == NewSeasonMode.Schedule && _airRadarData is null
            ? Visibility.Visible : Visibility.Collapsed;

    private async Task LoadCalendarAsync(bool forceRefresh = false)
    {
        if (_calendarLoading) return;
        _calendarLoading = true;
        _calendarError = null;
        UpdateStatusText();
        try
        {
            var result = await _calendar.LoadAsync(forceRefresh);
            if (_disposed) return;
            if (result.Data is null)
            {
                _calendarError = result.Error ?? "unknown";
                UpdateStatusText();
                return;
            }
            _calendarData = result.Data;
            _calendarStale = result.Stale;
            _calendarLoaded = true;
            UpdateStatusText();
            BuildSchedule();
        }
        finally
        {
            _calendarLoading = false;
            UpdateStatusText();
        }
    }

    private void BuildSchedule()
    {
        foreach (var zone in _scheduleRowZones) UnregisterZone(zone);
        _scheduleRowZones.Clear();
        _scheduleBody.Children.Clear();
        if (_airRadarData is not null)
        {
            BuildAirRadarSchedule();
            return;
        }
        if (_calendarData is null) return;

        var today = DateOnly.FromDateTime(DateTime.Now);
        var sections = TvBangumiCalendarService.BuildWeek(_calendarData, today, _weekOffset);
        if (sections.All(section => section.Entries.Count == 0))
        {
            _scheduleBody.Children.Add(new TextBlock
            {
                Text = AppStrings.Get("tv.schedule.emptyWeek"),
                FontSize = 20,
                Foreground = (Brush)FindResource("TvTextMutedBrush"),
                Margin = new Thickness(0, 20, 0, 0),
            });
            return;
        }

        foreach (var section in sections)
        {
            var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, section.IsToday ? 18 : 22, 0, 8) };
            header.Children.Add(new TextBlock
            {
                Text = TvAirWeekdayLabels.Title(section.Weekday),
                FontSize = 24,
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)FindResource(section.IsToday ? "TvAccentBrush" : "TvTextBrush"),
            });
            header.Children.Add(new TextBlock
            {
                Text = "  " + string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{section.Date.Month}/{section.Date.Day}"),
                FontSize = 18,
                VerticalAlignment = VerticalAlignment.Bottom,
                Foreground = (Brush)FindResource("TvTextMutedBrush"),
                Margin = new Thickness(0, 0, 0, 2),
            });
            if (section.IsToday)
            {
                header.Children.Add(new TextBlock
                {
                    Text = "  " + AppStrings.Get("tv.schedule.today"),
                    FontSize = 16,
                    VerticalAlignment = VerticalAlignment.Bottom,
                    Foreground = (Brush)FindResource("TvAccentBrush"),
                    Margin = new Thickness(0, 0, 0, 3),
                });
            }
            _scheduleBody.Children.Add(header);

            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 6, 30, 6) };
            if (section.Entries.Count == 0)
            {
                row.Children.Add(new TextBlock
                {
                    Text = AppStrings.Get("tv.schedule.emptyDay"),
                    FontSize = 16,
                    Foreground = (Brush)FindResource("TvTextMutedBrush"),
                    Margin = new Thickness(4, 8, 0, 16),
                });
            }
            var cards = new List<FrameworkElement>();
            foreach (var entry in section.Entries)
            {
                var card = CreateScheduleCard(entry);
                row.Children.Add(card);
                cards.Add(card);
            }
            var scroller = new ScrollViewer
            {
                Style = TvTheme.Style(this, "TvScrollViewerStyle"),
                Content = row,
                Margin = new Thickness(0, 0, 0, 0),
            };
            _scheduleBody.Children.Add(scroller);
            var zone = new TvZone($"schedule-day-{(int)section.Weekday}", TvZoneKind.Horizontal, cards);
            RegisterZone(zone);
            _scheduleRowZones.Add(zone);
        }
    }

    private void BuildAirRadarSchedule()
    {
        if (_airRadarData is null) return;
        var days = AirRadarScheduleService.BuildWeek(_airRadarData);
        var hasEntries = days.Any(day => day.Entries.Count > 0);
        if (!hasEntries)
        {
            _scheduleBody.Children.Add(new TextBlock
            {
                Text = AppStrings.Get("tv.schedule.emptyWeek"),
                FontSize = 20,
                Foreground = (Brush)FindResource("TvTextMutedBrush"),
                Margin = new Thickness(0, 20, 0, 0),
            });
            return;
        }
        foreach (var day in days)
        {
            var header = new TextBlock
            {
                Text = TvAirWeekdayLabels.Title(ToTvWeekday(day.Weekday)),
                FontSize = 24,
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)FindResource("TvTextBrush"),
                Margin = new Thickness(0, 22, 0, 8),
            };
            _scheduleBody.Children.Add(header);
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 6, 30, 6) };
            var cards = new List<FrameworkElement>();
            if (day.Entries.Count == 0)
            {
                row.Children.Add(new TextBlock
                {
                    Text = AppStrings.Get("tv.schedule.emptyDay"),
                    FontSize = 16,
                    Foreground = (Brush)FindResource("TvTextMutedBrush"),
                    Margin = new Thickness(4, 8, 0, 16),
                });
            }
            foreach (var entry in day.Entries)
            {
                var card = CreateAirRadarCard(entry, day.Weekday);
                row.Children.Add(card);
                cards.Add(card);
            }
            var scroller = new ScrollViewer
            {
                Style = TvTheme.Style(this, "TvScrollViewerStyle"),
                Content = row,
                Margin = new Thickness(0, 0, 0, 0),
            };
            _scheduleBody.Children.Add(scroller);
            var zone = new TvZone($"schedule-day-{(int)ToTvWeekday(day.Weekday)}", TvZoneKind.Horizontal, cards);
            RegisterZone(zone);
            _scheduleRowZones.Add(zone);
        }
    }

    private TvPosterCard CreateAirRadarCard(AirRadarLocalEntry entry, DayOfWeek weekday)
    {
        var dayLabel = TvAirWeekdayLabels.Title(ToTvWeekday(weekday));
        var timeLabel = string.IsNullOrEmpty(entry.TimeText)
            ? dayLabel + "  " + AppStrings.Get("tv.schedule.timeUnknown")
            : $"{dayLabel} {entry.TimeText}";
        var local = AirRadarScheduleService.MatchLibrary(_context.AllSeries, entry.Item);
        var card = new TvPosterCard
        {
            Style = TvTheme.Style(this, "TvPosterCardStyle"),
            TitleText = entry.Item.DisplayName(),
            SubtitleText = timeLabel,
            PosterWidth = SchedulePosterWidth,
            PosterHeight = SchedulePosterHeight,
            TitleFontSize = 15,
            BadgeText = local is null ? AppStrings.Get("tv.schedule.notInLibraryShort") : string.Empty,
            Margin = new Thickness(0, 0, 14, 14),
        };
        card.Click += (_, _) =>
        {
            if (local is not null) _shell.ShowDetail(local);
            else _shell.ShowToast(AppStrings.Get("tv.schedule.notInLibrary", entry.Item.DisplayName()));
        };
        _ = LoadAirRadarPosterAsync(card, entry.Item.Image);
        return card;
    }

    private async Task LoadAirRadarPosterAsync(TvPosterCard card, string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        try
        {
            if (!PosterCache.TryGetValue(url, out var image))
            {
                var bytes = await _airRadar.GetImageAsync(url);
                if (bytes is null) return;
                using var stream = new MemoryStream(bytes);
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.DecodePixelWidth = 220;
                bitmap.StreamSource = stream;
                bitmap.EndInit();
                bitmap.Freeze();
                image = bitmap;
                PosterCache[url] = image;
            }
            if (!_disposed && image is not null) card.PosterSource = image;
        }
        catch (Exception error) when (error is IOException or NotSupportedException or ArgumentException or InvalidOperationException)
        {
            // Missing poster must not hide the schedule entry.
        }
    }

    private static TvAirWeekday ToTvWeekday(DayOfWeek weekday) => weekday == DayOfWeek.Sunday
        ? TvAirWeekday.Sunday
        : (TvAirWeekday)(int)weekday;

    private void ChangeWeek(int offset)
    {
        _weekOffset = Math.Clamp(offset, -1, 1);
        UpdateWeekSelection();
        BuildSchedule();
        FocusZone(_weekZone!, _weekOffset + 1);
    }

    private TvPosterCard CreateScheduleCard(TvScheduleEntry entry)
    {
        var card = new TvPosterCard
        {
            Style = TvTheme.Style(this, "TvPosterCardStyle"),
            TitleText = entry.Item.DisplayName,
            SubtitleText = entry.IsToday ? entry.DateText + " · " + AppStrings.Get("tv.schedule.today") : entry.DateText,
            PosterWidth = SchedulePosterWidth,
            PosterHeight = SchedulePosterHeight,
            TitleFontSize = 15,
            Margin = new Thickness(0, 0, 14, 14),
        };
        card.Click += (_, _) =>
        {
            var local = TvBangumiCalendarService.MatchLibrary(_context.AllSeries, entry.Item);
            if (local is not null) _shell.ShowDetail(local);
            else _shell.ShowToast(AppStrings.Get("tv.schedule.notInLibrary", entry.Item.DisplayName));
        };
        _ = LoadPosterAsync(card, entry.Item.ImageUrl);
        return card;
    }

    private void EnsureSeasonBuilt()
    {
        if (ReferenceEquals(_seasonBuiltFor, _context.AllSeries) && _seasonCards.Count > 0) return;
        BuildSeason();
    }

    private void BuildSeason()
    {
        if (_seasonZone is not null) UnregisterZone(_seasonZone);
        _seasonGroups.Children.Clear();
        _seasonCards.Clear();
        _seasonPosterSources.Clear();
        _seasonBuiltFor = _context.AllSeries;
        _seasonBuildVersion++;

        var groups = TvReleaseSeason.Group(_context.AllSeries);
        var groupIndex = 0;
        foreach (var group in groups)
        {
            _seasonGroups.Children.Add(new TextBlock
            {
                Text = group.Title,
                Style = TvTheme.Style(this, "TvRowTitleText"),
                Margin = new Thickness(0, groupIndex == 0 ? 10 : 32, 0, 8),
            });
            var weekIndex = 0;
            foreach (var week in group.Weeks)
            {
                _seasonGroups.Children.Add(new TextBlock
                {
                    Text = week.Title,
                    FontSize = 18,
                    Foreground = (Brush)FindResource(week.Weekday == TvAirWeekday.Unknown ? "TvTextMutedBrush" : "TvTextSecondaryBrush"),
                    Margin = new Thickness(0, weekIndex == 0 ? 6 : 16, 0, 8),
                });
                var grid = new UniformGrid { Columns = Columns };
                foreach (var item in week.Series)
                {
                    var card = CreateSeasonCard(item);
                    grid.Children.Add(card);
                    _seasonCards.Add(card);
                }
                var remainder = week.Series.Count % Columns;
                if (remainder != 0)
                {
                    for (var i = remainder; i < Columns; i++)
                    {
                        grid.Children.Add(new Border
                        {
                            Width = SeasonPosterWidth,
                            Height = SeasonPlaceholderHeight,
                            Visibility = Visibility.Hidden,
                        });
                    }
                }
                _seasonGroups.Children.Add(grid);
                weekIndex++;
            }
            groupIndex++;
        }

        if (_seasonCards.Count == 0)
        {
            _seasonGroups.Children.Add(new TextBlock
            {
                Text = AppStrings.Get("tv.season.empty"),
                FontSize = 20,
                Foreground = (Brush)FindResource("TvTextMutedBrush"),
                Margin = new Thickness(0, 24, 0, 0),
            });
        }

        _seasonZone = new TvZone("season-grid", TvZoneKind.Grid, _seasonCards, Columns);
        RegisterZone(_seasonZone);
        StartSeasonPosterLoading();
        if (_mode == NewSeasonMode.Season) UpdateStatusText();
    }

    private TvPosterCard CreateSeasonCard(LibrarySeries series)
    {
        var (percent, label, completed) = TvAppContext.SeriesProgress(series);
        var card = new TvPosterCard
        {
            Style = TvTheme.Style(this, "TvPosterCardStyle"),
            TitleText = series.Title,
            SubtitleText = string.Empty,
            PosterWidth = SeasonPosterWidth,
            PosterHeight = SeasonPosterHeight,
            ShowProgress = percent > 0,
            ProgressPercent = percent,
            BadgeText = completed ? "✓" : percent > 0 ? label : string.Empty,
            Margin = new Thickness(0, 0, 18, 18),
        };
        card.Click += (_, _) => _shell.ShowDetail(series);
        _seasonPosterSources.Add((card, series.PosterPath));
        return card;
    }

    private void StartSeasonPosterLoading()
    {
        var version = _seasonBuildVersion;
        var sources = _seasonPosterSources.ToList();
        _ = Task.Run(() =>
        {
            foreach (var (card, path) in sources)
            {
                if (version != _seasonBuildVersion) return;
                if (string.IsNullOrWhiteSpace(path)) continue;
                var image = TvImages.Load(path);
                if (image is null) continue;
                Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                {
                    if (version == _seasonBuildVersion) card.PosterSource = image;
                }));
            }
        });
    }

    private async Task LoadPosterAsync(TvPosterCard card, string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        try
        {
            if (!PosterCache.TryGetValue(url, out var image))
            {
                var bytes = await _calendar.GetImageAsync(url);
                if (bytes is null) return;
                using var stream = new MemoryStream(bytes);
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.DecodePixelWidth = 220;
                bitmap.StreamSource = stream;
                bitmap.EndInit();
                bitmap.Freeze();
                image = bitmap;
                PosterCache[url] = image;
            }
            if (!_disposed && image is not null) card.PosterSource = image;
        }
        catch (Exception error) when (error is IOException or NotSupportedException or ArgumentException or InvalidOperationException)
        {
            // Missing poster must not hide the schedule entry.
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _airRadar.Dispose();
        _calendar.Dispose();
    }
}