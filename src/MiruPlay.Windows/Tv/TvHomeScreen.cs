using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MiruPlay.Windows.Models;
using MiruPlay.Windows.Services;

namespace MiruPlay.Windows.Tv;

/// <summary>Plex-style home: continue-watching hero plus hub rows.</summary>
public sealed class TvHomeScreen : TvScreenBase
{
    private readonly TvAppContext _context;
    private readonly TvShellWindow _shell;
    private readonly ScrollViewer _scroller;
    private readonly StackPanel _content = new();
    private readonly List<TvZone> _rowZones = [];
    private readonly List<(string Title, ScrollViewer Scroller, StackPanel Panel, FrameworkElement FirstCard)> _rowInfo = [];
    private TvZone? _heroZone;
    private TvContinueItem? _heroContinue;
    private LibrarySeries? _heroSeries;
    private bool _built;

    public TvHomeScreen(TvAppContext context, TvShellWindow shell)
    {
        _context = context;
        _shell = shell;
        TvTheme.Apply(this);
        Background = (Brush)FindResource("TvBackgroundBrush");
        _scroller = new ScrollViewer
        {
            Style = TvTheme.Style(this, "TvVerticalScrollViewerStyle"),
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        _content.Margin = new Thickness(0, 0, 0, 56);
        _scroller.Content = _content;
        Content = _scroller;
        _context.LibraryChanged += Context_LibraryChanged;
        Build();
    }

    public override TvScreenId Id => TvScreenId.Home;

    public override string HintText => AppStrings.Get("tv.hint.home");

    public override bool FocusInitial()
    {
        if (_heroZone is { Items.Count: > 0 } hero)
        {
            FocusZone(hero, 0);
            return true;
        }
        return base.FocusInitial();
    }

    public void Rebuild()
    {
        var memory = OnDeactivating();
        _content.Children.Clear();
        _rowZones.Clear();
        _heroZone = null;
        ClearZones();
        Build();
        if (memory is not null) RestoreFocus(memory);
        else FocusInitial();
    }

    private void Context_LibraryChanged(object? sender, EventArgs e) => Rebuild();

    private void Build()
    {
        var home = _context.Home;
        _heroContinue = home.ContinueWatching.Count > 0 ? home.ContinueWatching[0] : null;
        _heroSeries = _heroContinue?.Series
            ?? (home.RecentlyAdded.Count > 0 ? home.RecentlyAdded[0] : null)
            ?? (home.AllSeries.Count > 0 ? home.AllSeries[0] : null);
        BuildHero();
        BuildContinueRow(home);
        BuildRecentlyAddedRow(home);
        BuildGenreRow(home);
        BuildAllRow(home);
        _built = true;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(DumpLayout));
    }

    private void DumpLayout()
    {
        try
        {
            var lines = new List<string>
            {
                $"SELF {ActualWidth}x{ActualHeight}",
                $"SCROLLER extent={_scroller.ExtentHeight:0} viewport={_scroller.ViewportHeight:0} offset={_scroller.VerticalOffset:0} scrollable={_scroller.ScrollableHeight:0}",
                $"FOCUS zone={ActiveZone?.Id} index={ActiveIndex}",
            };
            foreach (var (title, scroller, panel, firstCard) in _rowInfo)
                lines.Add($"ROW {title} scroller={scroller.ActualHeight:0} viewport={scroller.ViewportHeight:0} extent={scroller.ExtentHeight:0} panel={panel.ActualHeight:0} card={firstCard.ActualHeight:0}");
            File.AppendAllText(Path.Combine(Path.GetTempPath(), "miruplay-tv-layout.log"), string.Join("\n", lines) + "\n---\n");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Layout diagnostics only.
        }
    }

    private void BuildHero()
    {
        var hero = new Grid { Height = 420, Margin = new Thickness(0, 0, 0, 22) };
        if (_heroSeries is { } series)
        {
            var image = new Image
            {
                Source = TvImages.Load(series.PosterPath),
                Stretch = Stretch.UniformToFill,
                Opacity = 0.55,
            };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            hero.Children.Add(image);
        }
        var gradient = new Border
        {
            Background = new LinearGradientBrush(
                new GradientStopCollection
                {
                    new(Color.FromArgb(0xEE, 0x0B, 0x0B, 0x0D), 0),
                    new(Color.FromArgb(0x8C, 0x0B, 0x0B, 0x0D), 0.45),
                    new(Color.FromArgb(0x00, 0x0B, 0x0B, 0x0D), 1),
                },
                new Point(0, 0.5),
                new Point(1, 0.5)),
        };
        hero.Children.Add(gradient);
        var bottomFade = new Border
        {
            VerticalAlignment = VerticalAlignment.Bottom,
            Height = 160,
            Background = new LinearGradientBrush(
                new GradientStopCollection
                {
                    new(Color.FromArgb(0x00, 0x0B, 0x0B, 0x0D), 0),
                    new(Color.FromArgb(0xFF, 0x0B, 0x0B, 0x0D), 1),
                },
                new Point(0, 0),
                new Point(0, 1)),
        };
        hero.Children.Add(bottomFade);

        var info = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Bottom,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(68, 0, 68, 42),
            MaxWidth = 1050,
        };
        info.Children.Add(new TextBlock
        {
            Text = AppStrings.Get(_heroContinue is null ? "tv.home.hero.featured" : "tv.home.hero.continue"),
            FontSize = 17,
            Foreground = (Brush)FindResource("TvAccentBrush"),
            FontWeight = FontWeights.SemiBold,
        });
        info.Children.Add(new TextBlock
        {
            Text = _heroSeries?.Title ?? AppStrings.Get("tv.home.empty"),
            FontSize = 46,
            FontWeight = FontWeights.Bold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 6, 0, 0),
        });
        var meta = _heroContinue is not null
            ? $"{_heroContinue.EpisodeLabel}  ·  {_heroContinue.RemainingText}"
            : _heroSeries is null ? string.Empty : TvPresentation.MetadataLine(_heroSeries);
        info.Children.Add(new TextBlock
        {
            Text = meta,
            FontSize = 19,
            Margin = new Thickness(0, 8, 0, 0),
            Foreground = (Brush)FindResource("TvTextSecondaryBrush"),
        });
        if (_heroContinue is { ProgressPercent: > 0 } continueItem)
        {
            var progress = new ProgressBar
            {
                Style = TvTheme.Style(this, "TvProgressBar"),
                Height = 6,
                Width = 430,
                HorizontalAlignment = HorizontalAlignment.Left,
                Maximum = 100,
                Value = continueItem.ProgressPercent,
                Margin = new Thickness(0, 18, 0, 0),
            };
            info.Children.Add(progress);
        }

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 22, 0, 0) };
        var play = new Button
        {
            Content = AppStrings.Get(_heroContinue is null ? "tv.home.hero.play" : "tv.home.hero.resume"),
            Style = TvTheme.Style(this, "TvPrimaryButton"),
            FocusVisualStyle = null,
            Margin = new Thickness(0, 0, 16, 0),
        };
        play.Click += (_, _) => PlayHero(resume: true);
        var detail = new Button
        {
            Content = AppStrings.Get("tv.home.hero.detail"),
            Style = TvTheme.Style(this, "TvSecondaryButton"),
            FocusVisualStyle = null,
        };
        detail.Click += (_, _) => { if (_heroSeries is not null) _shell.ShowDetail(_heroSeries); };
        buttons.Children.Add(play);
        buttons.Children.Add(detail);
        info.Children.Add(buttons);
        hero.Children.Add(info);
        _content.Children.Add(hero);

        _heroZone = new TvZone("hero", TvZoneKind.Horizontal, [play, detail]);
        RegisterZones(_heroZone);
    }

    private void PlayHero(bool resume)
    {
        if (_heroSeries is null) return;
        var episode = _heroContinue?.Episode ?? TvAppContext.ResumeEpisode(_heroSeries);
        if (episode is null)
        {
            _shell.ShowToast(AppStrings.Get("tv.toast.noEpisodes"));
            return;
        }
        _shell.StartPlayback(_heroSeries, episode, resume: resume && _heroContinue is not null);
    }

    private void BuildContinueRow(TvHomeData home)
    {
        var cards = new List<FrameworkElement>();
        foreach (var item in home.ContinueWatching.Take(12))
        {
            var card = new TvPosterCard
            {
                Style = TvTheme.Style(this, "TvPosterCardStyle"),
                PosterSource = TvImages.Load(item.Series.PosterPath),
                TitleText = item.Series.Title,
                SubtitleText = item.EpisodeLabel,
                PosterWidth = 330,
                PosterHeight = 186,
                ShowProgress = true,
                ProgressPercent = item.ProgressPercent,
            };
            card.Click += (_, _) => _shell.StartPlayback(item.Series, item.Episode, resume: true);
            cards.Add(card);
        }
        AddRow("tv.home.row.continue", cards, home.ContinueWatching.Count > 0);
    }

    private void BuildRecentlyAddedRow(TvHomeData home)
    {
        var cards = home.RecentlyAdded.Take(20).Select(series =>
        {
            var card = CreatePosterCard(series, 180, 270);
            card.Click += (_, _) => _shell.ShowDetail(series);
            return (FrameworkElement)card;
        }).ToList();
        AddRow("tv.home.row.recent", cards, cards.Count > 0);
    }

    private void BuildGenreRow(TvHomeData home)
    {
        var cards = home.Genres.Select(genre =>
        {
            var card = new TvTextCard
            {
                Style = TvTheme.Style(this, "TvTextCardStyle"),
                AccentText = AppStrings.Get("tv.home.genre"),
                TitleText = genre.Genre,
                SubtitleText = AppStrings.Get("tv.home.genre.count", genre.Count),
                CardWidth = 280,
                CardHeight = 150,
            };
            card.Click += (_, _) => _shell.NavigateToLibrary(genre.Genre);
            return (FrameworkElement)card;
        }).ToList();
        AddRow("tv.home.row.categories", cards, cards.Count > 0);
    }

    private void BuildAllRow(TvHomeData home)
    {
        var cards = home.AllSeries.Take(20).Select(series =>
        {
            var card = CreatePosterCard(series, 180, 270);
            card.Click += (_, _) => _shell.ShowDetail(series);
            return (FrameworkElement)card;
        }).ToList();
        var more = new TvTextCard
        {
            Style = TvTheme.Style(this, "TvTextCardStyle"),
            AccentText = AppStrings.Get("tv.home.allMore"),
            TitleText = AppStrings.Get("tv.home.row.all"),
            SubtitleText = AppStrings.Get("tv.home.allCount", home.AllSeries.Count),
            CardWidth = 280,
            CardHeight = 270,
        };
        more.Click += (_, _) => _shell.NavigateToLibrary(null);
        cards.Add(more);
        AddRow("tv.home.row.all", cards, cards.Count > 0);
    }

    private TvPosterCard CreatePosterCard(LibrarySeries series, double width, double height)
    {
        var (percent, label, completed) = TvAppContext.SeriesProgress(series);
        return new TvPosterCard
        {
            Style = TvTheme.Style(this, "TvPosterCardStyle"),
            PosterSource = TvImages.Load(series.PosterPath),
            TitleText = series.Title,
            SubtitleText = TvPresentation.MetadataLine(series),
            PosterWidth = width,
            PosterHeight = height,
            ShowProgress = percent > 0,
            ProgressPercent = percent,
            BadgeText = completed ? "✓" : percent > 0 ? label : string.Empty,
        };
    }

    private void AddRow(string titleKey, List<FrameworkElement> cards, bool visible)
    {
        if (!visible || cards.Count == 0) return;
        var outer = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        outer.Children.Add(new TextBlock
        {
            Text = AppStrings.Get(titleKey),
            FontSize = 24,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(64, 0, 64, 12),
        });
        var scroller = new ScrollViewer
        {
            Style = TvTheme.Style(this, "TvScrollViewerStyle"),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Margin = new Thickness(0, 0, 0, 18),
        };
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(64, 6, 64, 10) };
        foreach (var card in cards)
        {
            card.Margin = new Thickness(0, 0, 22, 0);
            panel.Children.Add(card);
        }
        scroller.Content = panel;
        outer.Children.Add(scroller);
        _content.Children.Add(outer);
        _rowInfo.Add((titleKey, scroller, panel, cards[0]));
        var zone = new TvZone(
            "row:" + titleKey,
            TvZoneKind.Horizontal,
            cards,
            onFocusChanged: index =>
            {
                BringCardIntoView(scroller, cards[index]);
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(DumpLayout));
            });
        RegisterZone(zone);
        _rowZones.Add(zone);
    }

    private static void BringCardIntoView(ScrollViewer scroller, FrameworkElement card)
    {
        card.BringIntoView();
        var transform = card.TransformToAncestor(scroller);
        var bounds = transform.TransformBounds(new Rect(0, 0, card.ActualWidth, card.ActualHeight));
        if (bounds.Left < 0) scroller.ScrollToHorizontalOffset(scroller.HorizontalOffset + bounds.Left - 64);
        else if (bounds.Right > scroller.ViewportWidth)
            scroller.ScrollToHorizontalOffset(scroller.HorizontalOffset + bounds.Right - scroller.ViewportWidth + 64);
    }

    protected override bool OnMoveOutOfZone(TvInputAction direction)
    {
        var index = ZoneIndexOf(ActiveZone);
        if (index < 0) return false;
        if (direction == TvInputAction.Down && index + 1 < Zones.Count)
        {
            FocusZone(Zones[index + 1], 0);
            return true;
        }
        if (direction == TvInputAction.Up && index > 0)
        {
            FocusZone(Zones[index - 1], 0);
            return true;
        }
        return false;
    }

    protected override void OnVisualParentChanged(DependencyObject oldParent)
    {
        base.OnVisualParentChanged(oldParent);
        if (!_built) return;
        _scroller.ScrollToVerticalOffset(_scroller.VerticalOffset);
    }
}
