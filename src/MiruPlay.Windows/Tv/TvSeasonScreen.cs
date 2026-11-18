using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using MiruPlay.Windows.Models;
using MiruPlay.Windows.Services;

namespace MiruPlay.Windows.Tv;

/// <summary>
/// New-season poster wall: groups by the Android-style broadcast season
/// (1/4/7/9 月新番), newest groups first, six posters per row, scrollable.
/// Focus and card behavior reuse the library wall's grid zone.
/// </summary>
public sealed class TvSeasonScreen : TvScreenBase
{
    private const int Columns = 6;
    private const double PosterWidth = 170;
    private const double PosterHeight = 254;
    private const double CardPlaceholderHeight = 330;

    private readonly TvAppContext _context;
    private readonly TvShellWindow _shell;
    private readonly StackPanel _groups = new() { Margin = new Thickness(64, 0, 64, 40) };
    private readonly TextBlock _summary = new();
    private readonly List<FrameworkElement> _cards = [];
    private readonly List<(TvPosterCard Card, string? Path)> _posterSources = [];
    private IReadOnlyList<LibrarySeries>? _builtFor;
    private int _buildVersion;
    private TvZone? _zone;

    public TvSeasonScreen(TvAppContext context, TvShellWindow shell)
    {
        _context = context;
        _shell = shell;
        TvTheme.Apply(this);
        Background = (Brush)FindResource("TvBackgroundBrush");

        var header = new StackPanel { Margin = new Thickness(64, 26, 64, 6) };
        header.Children.Add(new TextBlock
        {
            Text = AppStrings.Get("tv.season.title"),
            Style = TvTheme.Style(this, "TvPageTitleText"),
        });
        _summary.FontSize = 16;
        _summary.Foreground = (Brush)FindResource("TvTextMutedBrush");
        _summary.Margin = new Thickness(0, 6, 0, 0);
        header.Children.Add(_summary);

        var scroll = new ScrollViewer
        {
            Style = TvTheme.Style(this, "TvVerticalScrollViewerStyle"),
            Content = _groups,
        };

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.Children.Add(header);
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);
        Content = root;
        Build();
    }

    public override TvScreenId Id => TvScreenId.Season;

    public override string HintText => AppStrings.Get("tv.hint.season");

    public override void OnActivated(string? focusKey)
    {
        if (!ReferenceEquals(_builtFor, _context.AllSeries)) Build();
        base.OnActivated(focusKey);
    }

    public override bool FocusInitial()
    {
        if (_zone is { Items.Count: > 0 })
        {
            FocusZone(_zone, 0);
            return true;
        }
        return false;
    }

    private void Build()
    {
        if (_zone is not null) UnregisterZone(_zone);
        _groups.Children.Clear();
        _cards.Clear();
        _posterSources.Clear();
        _builtFor = _context.AllSeries;
        _buildVersion++;

        var groups = TvReleaseSeason.Group(_context.AllSeries);
        _summary.Text = AppStrings.Get("tv.season.summary", groups.Sum(group => group.Count));
        var groupIndex = 0;
        foreach (var group in groups)
        {
            _groups.Children.Add(new TextBlock
            {
                Text = group.Title,
                Style = TvTheme.Style(this, "TvRowTitleText"),
                Margin = new Thickness(0, groupIndex == 0 ? 10 : 32, 0, 8),
            });
            var weekIndex = 0;
            foreach (var week in group.Weeks)
            {
                _groups.Children.Add(new TextBlock
                {
                    Text = week.Title,
                    FontSize = 18,
                    Foreground = (Brush)FindResource(week.Weekday == TvAirWeekday.Unknown ? "TvTextMutedBrush" : "TvTextSecondaryBrush"),
                    Margin = new Thickness(0, weekIndex == 0 ? 6 : 16, 0, 8),
                });
                var grid = new UniformGrid { Columns = Columns };
                foreach (var item in week.Series)
                {
                    var card = CreateCard(item);
                    grid.Children.Add(card);
                    _cards.Add(card);
                }
                var remainder = week.Series.Count % Columns;
                if (remainder != 0)
                {
                    for (var i = remainder; i < Columns; i++)
                    {
                        grid.Children.Add(new Border
                        {
                            Width = PosterWidth,
                            Height = CardPlaceholderHeight,
                            Visibility = Visibility.Hidden,
                        });
                    }
                }
                _groups.Children.Add(grid);
                weekIndex++;
            }
            groupIndex++;
        }

        if (_cards.Count == 0)
        {
            _groups.Children.Add(new TextBlock
            {
                Text = AppStrings.Get("tv.season.empty"),
                FontSize = 20,
                Foreground = (Brush)FindResource("TvTextMutedBrush"),
                Margin = new Thickness(0, 24, 0, 0),
            });
        }

        _zone = new TvZone("season", TvZoneKind.Grid, _cards, Columns);
        RegisterZone(_zone);
        StartPosterLoading();
    }

    /// <summary>Decode posters off the UI thread so the wall is interactive immediately (S: artwork can be slow).</summary>
    private void StartPosterLoading()
    {
        var version = _buildVersion;
        var sources = _posterSources.ToList();
        _ = Task.Run(() =>
        {
            foreach (var (card, path) in sources)
            {
                if (version != _buildVersion) return;
                if (string.IsNullOrWhiteSpace(path)) continue;
                var image = TvImages.Load(path);
                if (image is null) continue;
                Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                {
                    if (version == _buildVersion) card.PosterSource = image;
                }));
            }
        });
    }

    private TvPosterCard CreateCard(LibrarySeries series)
    {
        var (percent, label, completed) = TvAppContext.SeriesProgress(series);
        var card = new TvPosterCard
        {
            Style = TvTheme.Style(this, "TvPosterCardStyle"),
            TitleText = series.Title,
            SubtitleText = string.Empty,
            PosterWidth = PosterWidth,
            PosterHeight = PosterHeight,
            ShowProgress = percent > 0,
            ProgressPercent = percent,
            BadgeText = completed ? "✓" : percent > 0 ? label : string.Empty,
            Margin = new Thickness(0, 0, 18, 18),
        };
        card.Click += (_, _) => _shell.ShowDetail(series);
        _posterSources.Add((card, series.PosterPath));
        return card;
    }
}