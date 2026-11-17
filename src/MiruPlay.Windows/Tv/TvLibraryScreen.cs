using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using MiruPlay.Windows.Models;
using MiruPlay.Windows.Services;

namespace MiruPlay.Windows.Tv;

/// <summary>Plex-style poster wall: 6 columns x 3 rows per page, page flipping at row edges.</summary>
public sealed class TvLibraryScreen : TvScreenBase
{
    private const int Columns = 6;
    private const int Rows = 3;
    private const int PageSize = Columns * Rows;

    private readonly TvAppContext _context;
    private readonly TvShellWindow _shell;
    private readonly UniformGrid _grid = new() { Rows = Rows, Columns = Columns, VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel _filterBar = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _pageText = new();
    private readonly TextBlock _summaryText = new();
    private readonly Button _allChip;
    private readonly Button _genreChip;
    private readonly Button _sortChip;
    private readonly Button _searchChip;
    private readonly List<string> _genres = [];
    private List<LibrarySeries> _items = [];
    private int _page;
    private string? _genre;
    private bool _sortByDate;
    private TvZone? _filterZone;
    private TvZone? _gridZone;

    public TvLibraryScreen(TvAppContext context, TvShellWindow shell)
    {
        _context = context;
        _shell = shell;
        TvTheme.Apply(this);
        Background = (Brush)FindResource("TvBackgroundBrush");

        _allChip = CreateChip("tv.library.filter.all");
        _allChip.Click += (_, _) => { _genre = null; _page = 0; Refresh(rebuildGenres: true); FocusZone(_gridZone!, 0); };
        _genreChip = CreateChip("tv.library.filter.genre");
        _genreChip.Click += (_, _) => CycleGenre();
        _sortChip = CreateChip("tv.library.filter.sort");
        _sortChip.Click += (_, _) => { _sortByDate = !_sortByDate; _page = 0; Refresh(rebuildGenres: false); FocusZone(_gridZone!, 0); };
        _searchChip = CreateChip("tv.library.filter.search");
        _searchChip.Click += (_, _) => _shell.ShowSearch();
        foreach (var chip in new[] { _allChip, _genreChip, _sortChip, _searchChip })
        {
            chip.Margin = new Thickness(0, 0, 14, 0);
            _filterBar.Children.Add(chip);
        }

        var header = new Grid { Margin = new Thickness(64, 26, 64, 0) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(_filterBar);
        _summaryText.FontSize = 16;
        _summaryText.Foreground = (Brush)FindResource("TvTextMutedBrush");
        _summaryText.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(_summaryText, 1);
        header.Children.Add(_summaryText);

        var footer = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(64, 0, 64, 18) };
        _pageText.FontSize = 17;
        _pageText.Foreground = (Brush)FindResource("TvTextMutedBrush");
        footer.Children.Add(_pageText);

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(header);
        var gridHost = new Border { Margin = new Thickness(64, 18, 64, 6) };
        gridHost.Child = _grid;
        Grid.SetRow(gridHost, 1);
        root.Children.Add(gridHost);
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);
        Content = root;

        _filterZone = new TvZone("filter", TvZoneKind.Horizontal, new FrameworkElement[] { _allChip, _genreChip, _sortChip, _searchChip });
        RegisterZone(_filterZone);
        Refresh(rebuildGenres: true);
    }

    public override TvScreenId Id => TvScreenId.Library;

    public override string HintText => AppStrings.Get("tv.hint.library");

    public void NavigateWithGenre(string? genre)
    {
        _genre = genre;
        _page = 0;
        Refresh(rebuildGenres: true);
        if (_gridZone is { Items.Count: > 0 }) FocusZone(_gridZone, 0);
    }

    private Button CreateChip(string locKey) => new()
    {
        Content = AppStrings.Get(locKey),
        Style = TvTheme.Style(this, "TvChipButton"),
        FocusVisualStyle = null,
    };

    private void CycleGenre()
    {
        if (_genres.Count == 0) return;
        var index = _genre is null ? 0 : _genres.IndexOf(_genre) + 1;
        _genre = index >= _genres.Count ? null : _genres[index];
        _page = 0;
        Refresh(rebuildGenres: false);
        if (_gridZone is { Items.Count: > 0 }) FocusZone(_gridZone, 0);
    }

    private void Refresh(bool rebuildGenres)
    {
        _items = _context.FilterLibrary(null, _genre).ToList();
        if (_sortByDate)
        {
            _items = _items.OrderByDescending(series => series.AirDate ?? string.Empty, StringComparer.Ordinal)
                .ThenBy(series => series.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
        }
        if (rebuildGenres || _genres.Count == 0)
        {
            _genres.Clear();
            _genres.AddRange(_context.AllSeries
                .SelectMany(series => series.Genres)
                .Where(genre => !string.IsNullOrWhiteSpace(genre))
                .Distinct(StringComparer.CurrentCultureIgnoreCase)
                .OrderBy(genre => genre, StringComparer.CurrentCultureIgnoreCase));
        }
        _allChip.Tag = _genre is null ? "active" : null;
        _genreChip.Tag = _genre is not null ? "active" : null;
        _genreChip.Content = AppStrings.Get("tv.library.filter.genreValue", _genre ?? AppStrings.Get("tv.library.filter.allGenres"));
        _sortChip.Content = AppStrings.Get(_sortByDate ? "tv.library.sort.date" : "tv.library.sort.title");
        _summaryText.Text = AppStrings.Get("tv.library.summary", _items.Count);
        BuildPage();
    }

    private void BuildPage()
    {
        var pageCount = Math.Max(1, (_items.Count + PageSize - 1) / PageSize);
        _page = Math.Clamp(_page, 0, pageCount - 1);
        _pageText.Text = AppStrings.Get("tv.library.page", _page + 1, pageCount);
        _grid.Children.Clear();
        var pageItems = _items.Skip(_page * PageSize).Take(PageSize).ToList();
        var cards = new List<FrameworkElement>();
        foreach (var series in pageItems)
        {
            var card = CreateCard(series);
            _grid.Children.Add(card);
            cards.Add(card);
        }
        if (_gridZone is not null) UnregisterZone(_gridZone);
        _gridZone = new TvZone(
            "grid",
            TvZoneKind.Grid,
            cards,
            Columns,
            onEdge: (index, direction) => HandleGridEdge(index, direction, pageCount));
        RegisterZone(_gridZone);
        if (ActiveZone is not null && ActiveZone != _filterZone && cards.Count > 0)
        {
            var current = Math.Clamp(ActiveIndex, 0, cards.Count - 1);
            FocusZone(_gridZone, current);
        }
    }

    private TvPosterCard CreateCard(LibrarySeries series)
    {
        var (percent, label, completed) = TvAppContext.SeriesProgress(series);
        var card = new TvPosterCard
        {
            Style = TvTheme.Style(this, "TvPosterCardStyle"),
            PosterSource = TvImages.Load(series.PosterPath),
            TitleText = series.Title,
            SubtitleText = series.Genres.Count > 0 ? series.Genres[0] : string.Empty,
            PosterWidth = 176,
            PosterHeight = 264,
            ShowProgress = percent > 0,
            ProgressPercent = percent,
            BadgeText = completed ? "✓" : label,
            Margin = new Thickness(10, 8, 10, 8),
        };
        card.Click += (_, _) => _shell.ShowDetail(series);
        return card;
    }

    private bool HandleGridEdge(int index, TvInputAction direction, int pageCount)
    {
        if (direction == TvInputAction.Left && index == 0 && _page > 0)
        {
            _page--;
            BuildPage();
            if (_gridZone is { Items.Count: > 0 }) FocusZone(_gridZone, _gridZone.Items.Count - 1);
            return true;
        }
        if (direction == TvInputAction.Right && index == _gridZone!.Items.Count - 1 && _page < pageCount - 1)
        {
            _page++;
            BuildPage();
            if (_gridZone is { Items.Count: > 0 }) FocusZone(_gridZone, 0);
            return true;
        }
        return false;
    }

    protected override bool OnMoveOutOfZone(TvInputAction direction)
    {
        if (direction == TvInputAction.Up && ActiveZone == _gridZone)
        {
            FocusZone(_filterZone!, 0);
            return true;
        }
        if (direction == TvInputAction.Down && ActiveZone == _filterZone)
        {
            if (_gridZone is { Items.Count: > 0 }) FocusZone(_gridZone, 0);
            return true;
        }
        return false;
    }

    public override bool FocusInitial()
    {
        if (_gridZone is { Items.Count: > 0 })
        {
            FocusZone(_gridZone, 0);
            return true;
        }
        FocusZone(_filterZone!, 0);
        return true;
    }
}
