using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using MiruPlay.Windows.Models;
using MiruPlay.Windows.Services;

namespace MiruPlay.Windows.Tv;

/// <summary>
/// Search page: on-screen keyboard, recent searches and result row. Text can
/// also be pushed from the phone /remote page (see <see cref="SetRemoteText"/>).
/// </summary>
public sealed class TvSearchScreen : TvScreenBase
{
    private static readonly string[] KeyboardRows = ["1234567890", "qwertyuiop", "asdfghjkl", "zxcvbnm"];
    private static readonly List<string> RecentSearches = [];

    private readonly TvAppContext _context;
    private readonly TvShellWindow _shell;
    private readonly TextBlock _queryText;
    private readonly StackPanel _recentPanel = new() { Orientation = Orientation.Horizontal };
    private readonly StackPanel _actionPanel = new() { Orientation = Orientation.Horizontal };
    private readonly UniformGrid _keys = new() { Columns = 10, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly StackPanel _resultsPanel = new() { Orientation = Orientation.Horizontal };
    private readonly ScrollViewer _resultsScroller;
    private readonly TextBlock _emptyText;
    private readonly StackPanel _keyboardHost = new();
    private readonly Border _recentHost;
    private TvZone? _recentZone;
    private TvZone? _keyZone;
    private TvZone? _actionZone;
    private TvZone? _resultZone;
    private string _query = string.Empty;
    private List<LibrarySeries> _results = [];

    public TvSearchScreen(TvAppContext context, TvShellWindow shell)
    {
        _context = context;
        _shell = shell;
        TvTheme.Apply(this);
        Background = (Brush)FindResource("TvBackgroundBrush");

        var root = new Grid { Margin = new Thickness(64, 30, 64, 20) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var title = new TextBlock { Text = AppStrings.Get("tv.search.title"), FontSize = 34, FontWeight = FontWeights.SemiBold };
        root.Children.Add(title);

        var box = new Border
        {
            Background = (Brush)FindResource("TvSurfaceBrush"),
            BorderBrush = (Brush)FindResource("TvBorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(22, 14, 22, 14),
            Margin = new Thickness(0, 14, 0, 14),
            Child = _queryText = new TextBlock { FontSize = 24, Text = string.Empty },
        };
        Grid.SetRow(box, 1);
        root.Children.Add(box);

        var recentHost = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        recentHost.Children.Add(new TextBlock
        {
            Text = AppStrings.Get("tv.search.recent"),
            FontSize = 17,
            Foreground = (Brush)FindResource("TvTextMutedBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 16, 0),
        });
        recentHost.Children.Add(_recentPanel);
        _recentHost = new Border { Child = recentHost, Margin = new Thickness(0, 0, 0, 8) };
        Grid.SetRow(_recentHost, 2);
        root.Children.Add(_recentHost);

        var keyboardRows = new StackPanel { Margin = new Thickness(0, 2, 0, 10) };
        foreach (var row in KeyboardRows)
        {
            var rowPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
            foreach (var character in row)
            {
                var key = character.ToString();
                var button = CreateKeyButton(key);
                button.Click += (_, _) => Append(key);
                rowPanel.Children.Add(button);
            }
            keyboardRows.Children.Add(rowPanel);
        }
        var actionRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        var space = CreateWideKey(AppStrings.Get("tv.search.space"), "space");
        space.Click += (_, _) => Append(" ");
        var backspace = CreateWideKey("⌫", "backspace");
        backspace.Click += (_, _) => Backspace();
        var clear = CreateWideKey(AppStrings.Get("tv.search.clear"), "clear");
        clear.Click += (_, _) => { _query = string.Empty; ApplyFilter(); };
        actionRow.Children.Add(space);
        actionRow.Children.Add(backspace);
        actionRow.Children.Add(clear);
        keyboardRows.Children.Add(actionRow);
        _keyboardHost.Children.Add(keyboardRows);
        Grid.SetRow(_keyboardHost, 3);
        root.Children.Add(_keyboardHost);

        var resultsHost = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        resultsHost.Children.Add(new TextBlock
        {
            Text = AppStrings.Get("tv.search.results"),
            FontSize = 24,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 10),
        });
        _resultsScroller = new ScrollViewer
        {
            Style = TvTheme.Style(this, "TvScrollViewerStyle"),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        _resultsScroller.Content = _resultsPanel;
        resultsHost.Children.Add(_resultsScroller);
        _emptyText = new TextBlock
        {
            Text = AppStrings.Get("tv.search.empty"),
            FontSize = 18,
            Foreground = (Brush)FindResource("TvTextMutedBrush"),
            Margin = new Thickness(0, 8, 0, 0),
        };
        resultsHost.Children.Add(_emptyText);
        Grid.SetRow(resultsHost, 4);
        root.Children.Add(resultsHost);

        Content = root;
        BuildZones();
        RebuildRecent();
        ApplyFilter();
    }

    public override TvScreenId Id => TvScreenId.Search;

    public override string HintText => AppStrings.Get("tv.hint.search");

    public override bool FocusInitial()
    {
        if (_keyZone is { Items.Count: > 0 })
        {
            FocusZone(_keyZone, 0);
            return true;
        }
        return false;
    }

    public override void OnActivated(string? focusKey)
    {
        base.OnActivated(focusKey);
        RebuildRecent();
        ApplyFilter();
    }

    /// <summary>Phone /remote text input lands here while TV search is foreground.</summary>
    public void SetRemoteText(string text)
    {
        _query = text ?? string.Empty;
        ApplyFilter();
    }

    public string Query => _query;

    private Button CreateKeyButton(string text) => new()
    {
        Content = text,
        Style = TvTheme.Style(this, "TvKeyButton"),
        FocusVisualStyle = null,
        Margin = new Thickness(0, 0, 6, 0),
    };

    private Button CreateWideKey(string text, string id) => new()
    {
        Content = text,
        Tag = id,
        Style = TvTheme.Style(this, "TvChipButton"),
        FocusVisualStyle = null,
        Margin = new Thickness(0, 0, 10, 0),
        Padding = new Thickness(24, 0, 24, 0),
    };

    private void BuildZones()
    {
        var recentItems = _recentPanel.Children.Cast<FrameworkElement>().ToList();
        _recentZone = new TvZone("recent", TvZoneKind.Horizontal, recentItems);
        var keyItems = _keyboardHost.Children
            .OfType<StackPanel>()
            .SelectMany(panel => panel.Children.OfType<FrameworkElement>())
            .ToList();
        _keyZone = new TvZone("keys", TvZoneKind.Grid, keyItems, 10, onEdge: (index, direction) => HandleKeyEdge(index, direction));
        var actionItems = _actionPanel.Children.Cast<FrameworkElement>().ToList();
        _actionZone = new TvZone("actions", TvZoneKind.Horizontal, actionItems);
        _resultZone = new TvZone("results", TvZoneKind.Horizontal, _resultsPanel.Children.Cast<FrameworkElement>().ToList(),
            onFocusChanged: BringResultIntoView);
        RegisterZones(_recentZone, _keyZone, _actionZone, _resultZone);
    }

    private bool HandleKeyEdge(int index, TvInputAction direction)
    {
        if (direction == TvInputAction.Down && index >= _keyZone!.Items.Count - KeyboardRows[^1].Length)
        {
            FocusZone(_actionZone!, 0);
            return true;
        }
        return false;
    }

    private void Append(string text)
    {
        _query += text;
        ApplyFilter();
    }

    /// <summary>Hardware keyboard / phone remote characters arrive here (on-screen keyboard still primary).</summary>
    public void AppendCharacter(char character)
    {
        if (char.IsControl(character)) return;
        Append(character.ToString());
    }

    public void RemoveLastCharacter() => Backspace();

    private void Backspace()
    {
        if (_query.Length > 0) _query = _query[..^1];
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        _queryText.Text = _query.Length == 0 ? AppStrings.Get("tv.search.placeholder") : _query + "▏";
        _results = _context.FilterLibrary(_query).Take(20).ToList();
        _resultsPanel.Children.Clear();
        foreach (var series in _results)
        {
            var (percent, label, completed) = TvAppContext.SeriesProgress(series);
            var card = new TvPosterCard
            {
                Style = TvTheme.Style(this, "TvPosterCardStyle"),
                PosterSource = TvImages.Load(series.PosterPath),
                TitleText = series.Title,
                SubtitleText = TvPresentation.MetadataLine(series),
                PosterWidth = 160,
                PosterHeight = 240,
                ShowProgress = percent > 0,
                ProgressPercent = percent,
                BadgeText = completed ? "✓" : percent > 0 ? label : string.Empty,
                Margin = new Thickness(0, 0, 18, 0),
            };
            card.Click += (_, _) => OpenResult(series);
            _resultsPanel.Children.Add(card);
        }
        _emptyText.Visibility = _results.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_resultZone is not null) UnregisterZone(_resultZone);
        var items = _resultsPanel.Children.Cast<FrameworkElement>().ToList();
        _resultZone = new TvZone("results", TvZoneKind.Horizontal, items, onFocusChanged: BringResultIntoView);
        RegisterZone(_resultZone);
        _resultsScroller.Visibility = _results.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OpenResult(LibrarySeries series)
    {
        var text = _query.Trim();
        if (text.Length > 0)
        {
            RecentSearches.RemoveAll(item => item.Equals(text, StringComparison.OrdinalIgnoreCase));
            RecentSearches.Insert(0, text);
            if (RecentSearches.Count > 8) RecentSearches.RemoveAt(RecentSearches.Count - 1);
        }
        _shell.ShowDetail(series);
    }

    private void RebuildRecent()
    {
        _recentPanel.Children.Clear();
        foreach (var term in RecentSearches.Take(8))
        {
            var chip = new Button
            {
                Content = term,
                Style = TvTheme.Style(this, "TvChipButton"),
                FocusVisualStyle = null,
                Margin = new Thickness(0, 0, 10, 0),
            };
            chip.Click += (_, _) => { _query = term; ApplyFilter(); };
            _recentPanel.Children.Add(chip);
        }
        _recentHost.Visibility = RecentSearches.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_recentZone is not null) UnregisterZone(_recentZone);
        var items = _recentPanel.Children.Cast<FrameworkElement>().ToList();
        _recentZone = new TvZone("recent", TvZoneKind.Horizontal, items);
        RegisterZone(_recentZone);
    }

    private void BringResultIntoView(int index)
    {
        if (index < 0 || index >= _resultsPanel.Children.Count) return;
        var card = (FrameworkElement)_resultsPanel.Children[index];
        card.BringIntoView();
        if (!_resultsScroller.IsAncestorOf(card)) return;
        try
        {
            var transform = card.TransformToAncestor(_resultsScroller);
            var bounds = transform.TransformBounds(new Rect(0, 0, card.ActualWidth, card.ActualHeight));
            if (bounds.Right > _resultsScroller.ViewportWidth)
                _resultsScroller.ScrollToHorizontalOffset(_resultsScroller.HorizontalOffset + bounds.Right - _resultsScroller.ViewportWidth + 30);
            else if (bounds.Left < 0)
                _resultsScroller.ScrollToHorizontalOffset(_resultsScroller.HorizontalOffset + bounds.Left - 30);
        }
        catch (InvalidOperationException)
        {
            // Visual tree is rebuilding; BringIntoView above is the safe fallback.
        }
    }

    protected override bool OnMoveOutOfZone(TvInputAction direction)
    {
        if (direction == TvInputAction.Down && ActiveZone == _keyZone)
        {
            FocusZone(_actionZone!, 0);
            return true;
        }
        if (direction == TvInputAction.Down && ActiveZone == _actionZone)
        {
            if (_resultZone is { Items.Count: > 0 }) FocusZone(_resultZone, 0);
            return true;
        }
        if (direction == TvInputAction.Up && ActiveZone == _actionZone)
        {
            FocusZone(_keyZone!, Math.Max(0, _keyZone!.Items.Count - 10));
            return true;
        }
        if (direction == TvInputAction.Up && ActiveZone == _resultZone)
        {
            FocusZone(_actionZone!, 0);
            return true;
        }
        if (direction == TvInputAction.Up && ActiveZone == _keyZone)
        {
            if (_recentZone is { Items.Count: > 0 }) FocusZone(_recentZone, 0);
            return true;
        }
        if (direction == TvInputAction.Down && ActiveZone == _recentZone)
        {
            FocusZone(_keyZone!, 0);
            return true;
        }
        return false;
    }
}