using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MiruPlay.Windows.Models;
using MiruPlay.Windows.Services;

namespace MiruPlay.Windows.Tv;

/// <summary>
/// Animeko-style Bangumi schedule: last / this / next week tabs with seven
/// weekday sections (Monday..Sunday). Each card opens the matching local series
/// (Bangumi id) or shows a toast when the title is not in the library.
/// </summary>
public sealed class TvScheduleScreen : TvScreenBase, IDisposable
{
    private static readonly Dictionary<string, ImageSource?> ImageCache = new(StringComparer.OrdinalIgnoreCase);

    private readonly TvAppContext _context;
    private readonly TvShellWindow _shell;
    private readonly TvBangumiCalendarService _service;
    private readonly StackPanel _tabs = new() { Orientation = Orientation.Horizontal };
    private readonly List<Button> _tabButtons = [];
    private readonly TextBlock _status = new();
    private readonly StackPanel _body = new() { Margin = new Thickness(64, 8, 64, 40) };
    private readonly List<TvZone> _rowZones = [];
    private TvZone? _tabZone;
    private TvBangumiCalendarData? _data;
    private int _weekOffset;
    private bool _loaded;
    private bool _loading;
    private string? _lastError;
    private bool _stale;
    private bool _disposed;

    public TvScheduleScreen(TvAppContext context, TvShellWindow shell)
    {
        _context = context;
        _shell = shell;
        TvTheme.Apply(this);
        Background = (Brush)FindResource("TvBackgroundBrush");

        var config = new CloudDriveAutomationStore().Load();
        _service = new TvBangumiCalendarService(
            handler: OutboundHttpProxy.CreateHandler(config.RssProxyEnabled, config.RssProxyHost, config.RssProxyPort));

        var header = new StackPanel { Margin = new Thickness(64, 26, 64, 0) };
        header.Children.Add(new TextBlock
        {
            Text = AppStrings.Get("tv.schedule.title"),
            Style = TvTheme.Style(this, "TvPageTitleText"),
        });
        var tabHost = new Border { Margin = new Thickness(0, 12, 0, 0), Child = _tabs };
        header.Children.Add(tabHost);
        for (var index = 0; index < 3; index++)
        {
            var offset = index - 1;
            var button = new Button
            {
                Content = AppStrings.Get(offset switch { -1 => "tv.schedule.lastWeek", 0 => "tv.schedule.thisWeek", _ => "tv.schedule.nextWeek" }),
                Style = TvTheme.Style(this, "TvChipButton"),
                FocusVisualStyle = null,
                Margin = new Thickness(0, 0, 14, 0),
            };
            button.Click += (_, _) => ChangeWeek(offset);
            _tabButtons.Add(button);
            _tabs.Children.Add(button);
        }
        _status.FontSize = 16;
        _status.Foreground = (Brush)FindResource("TvTextMutedBrush");
        _status.Margin = new Thickness(0, 10, 0, 0);
        _status.TextWrapping = TextWrapping.Wrap;
        header.Children.Add(_status);

        var scroll = new ScrollViewer
        {
            Style = TvTheme.Style(this, "TvVerticalScrollViewerStyle"),
            Content = _body,
        };
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.Children.Add(header);
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);
        Content = root;

        _tabZone = new TvZone("schedule-tabs", TvZoneKind.Horizontal, _tabButtons.Cast<FrameworkElement>().ToList());
        RegisterZone(_tabZone);
        UpdateTabSelection();
        _status.Text = AppStrings.Get("tv.schedule.loading");
    }

    public override TvScreenId Id => TvScreenId.Schedule;

    public override string HintText => AppStrings.Get("tv.hint.schedule");

    public override bool FocusInitial()
    {
        if (_tabZone is { Items.Count: > 0 })
        {
            FocusZone(_tabZone, _weekOffset + 1);
            return true;
        }
        return false;
    }

    public override void OnActivated(string? focusKey)
    {
        base.OnActivated(focusKey);
        if (!_loaded && !_loading) _ = LoadAsync();
    }

    public override bool HandleAction(TvInputAction action)
    {
        if (action == TvInputAction.Select && _lastError is not null)
        {
            _ = LoadAsync(forceRefresh: true);
            return true;
        }
        if (ActiveZone == _tabZone)
        {
            if (action == TvInputAction.Left) { ChangeWeek(Math.Max(-1, _weekOffset - 1)); return true; }
            if (action == TvInputAction.Right) { ChangeWeek(Math.Min(1, _weekOffset + 1)); return true; }
            if (action == TvInputAction.Down) { FocusRow(0, 0); return true; }
        }
        var rowIndex = _rowZones.IndexOf(ActiveZone!);
        if (rowIndex >= 0 && action is TvInputAction.Up or TvInputAction.Down)
        {
            if (action == TvInputAction.Up && rowIndex == 0)
            {
                FocusZone(_tabZone!, _weekOffset + 1);
                return true;
            }
            var target = Math.Clamp(rowIndex + (action == TvInputAction.Down ? 1 : -1), 0, Math.Max(0, _rowZones.Count - 1));
            FocusRow(target, Math.Min(ActiveIndex, _rowZones[target].Items.Count - 1));
            return true;
        }
        return base.HandleAction(action);
    }

    private void ChangeWeek(int offset)
    {
        _weekOffset = Math.Clamp(offset, -1, 1);
        UpdateTabSelection();
        RebuildBody();
        FocusZone(_tabZone!, _weekOffset + 1);
    }

    private void UpdateTabSelection()
    {
        for (var index = 0; index < _tabButtons.Count; index++)
            _tabButtons[index].Tag = index - 1 == _weekOffset ? "active" : null;
    }

    private void FocusRow(int row, int column)
    {
        if (row < 0 || row >= _rowZones.Count || _rowZones[row].Items.Count == 0) return;
        FocusZone(_rowZones[row], Math.Clamp(column, 0, _rowZones[row].Items.Count - 1));
    }

    private async Task LoadAsync(bool forceRefresh = false)
    {
        if (_loading) return;
        _loading = true;
        _lastError = null;
        _status.Text = AppStrings.Get("tv.schedule.loading");
        try
        {
            var result = await _service.LoadAsync(forceRefresh);
            if (_disposed) return;
            if (result.Data is null)
            {
                _lastError = result.Error ?? "unknown";
                _status.Text = AppStrings.Get("tv.schedule.error", _lastError);
                return;
            }
            _data = result.Data;
            _stale = result.Stale;
            _loaded = true;
            _status.Text = _stale
                ? AppStrings.Get("tv.schedule.stale")
                : string.Empty;
            SetStatusVisibility();
            RebuildBody();
        }
        finally
        {
            _loading = false;
        }
    }

    private void SetStatusVisibility() => _status.Visibility = string.IsNullOrEmpty(_status.Text) ? Visibility.Collapsed : Visibility.Visible;

    private void RebuildBody()
    {
        foreach (var zone in _rowZones) UnregisterZone(zone);
        _rowZones.Clear();
        _body.Children.Clear();
        if (_data is null) return;

        var today = DateOnly.FromDateTime(DateTime.Now);
        var sections = TvBangumiCalendarService.BuildWeek(_data, today, _weekOffset);
        if (sections.All(section => section.Entries.Count == 0))
        {
            _body.Children.Add(new TextBlock
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
            var header = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, section.IsToday ? 18 : 22, 0, 8),
            };
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
            _body.Children.Add(header);

            var row = new StackPanel { Orientation = Orientation.Horizontal };
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
                var card = CreateCard(entry);
                row.Children.Add(card);
                cards.Add(card);
            }
            var scroller = new ScrollViewer
            {
                Style = TvTheme.Style(this, "TvScrollViewerStyle"),
                Content = row,
                Margin = new Thickness(0, 0, 0, 6),
            };
            _body.Children.Add(scroller);
            var zone = new TvZone($"schedule-day-{(int)section.Weekday}", TvZoneKind.Horizontal, cards);
            RegisterZone(zone);
            _rowZones.Add(zone);
        }
    }

    private TvPosterCard CreateCard(TvScheduleEntry entry)
    {
        var card = new TvPosterCard
        {
            Style = TvTheme.Style(this, "TvPosterCardStyle"),
            TitleText = entry.Item.DisplayName,
            SubtitleText = entry.IsToday ? entry.DateText + " · " + AppStrings.Get("tv.schedule.today") : entry.DateText,
            PosterWidth = 110,
            PosterHeight = 160,
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

    private async Task LoadPosterAsync(TvPosterCard card, string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        try
        {
            if (!ImageCache.TryGetValue(url, out var image))
            {
                var bytes = await _service.GetImageAsync(url);
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
                ImageCache[url] = image;
            }
            if (!_disposed && image is not null) card.PosterSource = image;
        }
        catch (Exception error) when (error is IOException or NotSupportedException or ArgumentException or InvalidOperationException)
        {
            // A missing poster should not hide the schedule entry.
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _service.Dispose();
    }
}
