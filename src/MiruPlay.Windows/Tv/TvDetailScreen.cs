using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MiruPlay.Windows.Models;
using MiruPlay.Windows.Services;

namespace MiruPlay.Windows.Tv;

/// <summary>Full-bleed backdrop detail page: poster, metadata, actions and episode row.</summary>
public sealed class TvDetailScreen : TvScreenBase
{
    private readonly TvAppContext _context;
    private readonly TvShellWindow _shell;
    private readonly Dictionary<string, VersionRef> _versions = new(StringComparer.Ordinal);
    private readonly List<LibraryEpisode> _episodes = [];
    private Grid _root = new();
    private StackPanel _infoPanel = new();
    private StackPanel _episodePanel = new();
    private ScrollViewer _episodeScroller = new();
    private LibrarySeries _series;
    private TvZone? _actionZone;
    private TvZone? _episodeZone;
    private TvZone? _overlayZone;
    private Border? _versionOverlay;
    private Button? _primaryButton;
    private Button? _rewatchButton;
    private Button? _versionChip;
    private Button? _favoriteChip;
    private bool _favorite;
    private bool _versionOverlayVisible;

    public TvDetailScreen(TvAppContext context, TvShellWindow shell, LibrarySeries series)
    {
        _context = context;
        _shell = shell;
        _series = context.FindSeries(series.Id) ?? series;
        TvTheme.Apply(this);
        Background = (Brush)FindResource("TvBackgroundBrush");
        Build();
    }

    public override TvScreenId Id => TvScreenId.Detail;

    public override string HintText => AppStrings.Get("tv.hint.detail");

    public override bool FocusInitial()
    {
        if (_actionZone is { Items.Count: > 0 })
        {
            FocusZone(_actionZone, 0);
            return true;
        }
        return base.FocusInitial();
    }

    public void DisposeScreen()
    {
        _context.LibraryChanged -= Context_LibraryChanged;
    }

    /// <summary>Refresh progress after leaving the player while the detail page stays on the stack.</summary>
    public void RefreshFromContext()
    {
        var refreshed = _context.FindSeries(_series.Id);
        if (refreshed is null) return;
        _series = refreshed;
        var memory = OnDeactivating();
        Build();
        if (memory is not null) RestoreFocus(memory);
        else FocusInitial();
    }

    private void Context_LibraryChanged(object? sender, EventArgs e) => RefreshFromContext();

    private void Build()
    {
        _context.LibraryChanged -= Context_LibraryChanged;
        _context.LibraryChanged += Context_LibraryChanged;
        _root = new Grid();
        if (_series.PosterPath is { Length: > 0 })
        {
            var backdrop = new Image
            {
                Source = TvImages.Load(_series.PosterPath),
                Stretch = Stretch.UniformToFill,
                Opacity = 0.42,
            };
            RenderOptions.SetBitmapScalingMode(backdrop, BitmapScalingMode.HighQuality);
            _root.Children.Add(backdrop);
        }
        _root.Children.Add(new Border
        {
            Background = new LinearGradientBrush(
                new GradientStopCollection
                {
                    new(Color.FromArgb(0xF2, 0x0B, 0x0B, 0x0D), 0),
                    new(Color.FromArgb(0xD9, 0x0B, 0x0B, 0x0D), 0.55),
                    new(Color.FromArgb(0xFF, 0x0B, 0x0B, 0x0D), 1),
                },
                new Point(0, 0.3),
                new Point(1, 0.85)),
        });

        var layout = new Grid { Margin = new Thickness(64, 36, 64, 20) };
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.Children.Add(BuildInfoArea());
        var episodes = BuildEpisodesArea();
        Grid.SetRow(episodes, 1);
        layout.Children.Add(episodes);
        _root.Children.Add(layout);
        Content = _root;

        ClearZones();
        BuildZones();
    }

    private Grid BuildInfoArea()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var posterBorder = new Border
        {
            Width = 270,
            Height = 405,
            CornerRadius = new CornerRadius(12),
            Background = (Brush)FindResource("TvSurfaceAltBrush"),
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 4, 48, 0),
        };
        var poster = new Image { Source = TvImages.Load(_series.PosterPath), Stretch = Stretch.UniformToFill };
        RenderOptions.SetBitmapScalingMode(poster, BitmapScalingMode.HighQuality);
        posterBorder.Child = poster;
        grid.Children.Add(posterBorder);

        _infoPanel = new StackPanel { VerticalAlignment = VerticalAlignment.Top, MaxWidth = 1120 };
        _infoPanel.Children.Add(new TextBlock
        {
            Text = _series.Title,
            FontSize = 44,
            FontWeight = FontWeights.Bold,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        if (!string.IsNullOrWhiteSpace(_series.OriginalTitle) && _series.OriginalTitle != _series.Title)
        {
            _infoPanel.Children.Add(new TextBlock
            {
                Text = _series.OriginalTitle,
                FontSize = 21,
                Margin = new Thickness(0, 4, 0, 0),
                Foreground = (Brush)FindResource("TvTextMutedBrush"),
            });
        }
        _infoPanel.Children.Add(new TextBlock
        {
            Text = TvPresentation.MetadataLine(_series),
            FontSize = 18,
            Margin = new Thickness(0, 14, 0, 0),
            Foreground = (Brush)FindResource("TvTextSecondaryBrush"),
        });
        _infoPanel.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(_series.Summary) ? AppStrings.Get("tv.detail.noSummary") : _series.Summary,
            FontSize = 18,
            LineHeight = 27,
            Margin = new Thickness(0, 18, 0, 0),
            MaxHeight = 118,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)FindResource("TvTextSecondaryBrush"),
        });

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 26, 0, 0) };
        var resumeEpisode = TvAppContext.ResumeEpisode(_series);
        _primaryButton = new Button
        {
            Content = AppStrings.Get(IsInProgress(resumeEpisode) ? "tv.detail.resume" : "tv.detail.play"),
            Style = TvTheme.Style(this, "TvPrimaryButton"),
            FocusVisualStyle = null,
            Margin = new Thickness(0, 0, 14, 0),
        };
        _primaryButton.Click += (_, _) =>
        {
            if (resumeEpisode is null) return;
            _shell.StartPlayback(_series, resumeEpisode, resume: IsInProgress(resumeEpisode));
        };
        _rewatchButton = new Button
        {
            Content = AppStrings.Get("tv.detail.fromStart"),
            Style = TvTheme.Style(this, "TvSecondaryButton"),
            FocusVisualStyle = null,
            Margin = new Thickness(0, 0, 14, 0),
        };
        _rewatchButton.Click += (_, _) =>
        {
            var episode = TvAppContext.FirstEpisode(_series);
            if (episode is not null) _shell.StartPlayback(_series, episode, resume: false);
        };
        _versionChip = new Button
        {
            Content = AppStrings.Get("tv.detail.version"),
            Style = TvTheme.Style(this, "TvChipButton"),
            FocusVisualStyle = null,
            Margin = new Thickness(8, 4, 10, 4),
        };
        _versionChip.Click += (_, _) => ShowVersionOverlay();
        _favoriteChip = new Button
        {
            Content = AppStrings.Get("tv.detail.favorite"),
            Style = TvTheme.Style(this, "TvChipButton"),
            FocusVisualStyle = null,
            Margin = new Thickness(0, 4, 0, 4),
        };
        _favoriteChip.Click += (_, _) =>
        {
            _favorite = !_favorite;
            _favoriteChip.Content = AppStrings.Get(_favorite ? "tv.detail.favorited" : "tv.detail.favorite");
            _shell.ShowToast(AppStrings.Get(_favorite ? "tv.detail.favoriteOn" : "tv.detail.favoriteOff"));
        };
        actions.Children.Add(_primaryButton);
        actions.Children.Add(_rewatchButton);
        actions.Children.Add(_versionChip);
        actions.Children.Add(_favoriteChip);
        _infoPanel.Children.Add(actions);
        Grid.SetColumn(_infoPanel, 1);
        grid.Children.Add(_infoPanel);
        return grid;
    }

    private StackPanel BuildEpisodesArea()
    {
        var panel = new StackPanel { Margin = new Thickness(0, 24, 0, 0) };
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var title = new TextBlock
        {
            Text = AppStrings.Get("tv.detail.episodes"),
            FontSize = 24,
            FontWeight = FontWeights.SemiBold,
        };
        header.Children.Add(title);
        var count = new TextBlock
        {
            Text = AppStrings.Get("tv.detail.episodeCount", _series.Episodes.Count),
            FontSize = 16,
            Foreground = (Brush)FindResource("TvTextMutedBrush"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(count, 1);
        header.Children.Add(count);
        panel.Children.Add(header);

        _episodeScroller = new ScrollViewer
        {
            Style = TvTheme.Style(this, "TvScrollViewerStyle"),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Margin = new Thickness(0, 14, 0, 4),
        };
        _episodePanel = new StackPanel { Orientation = Orientation.Horizontal };
        _episodes.Clear();
        _versions.Clear();
        foreach (var season in _series.Seasons)
        {
            foreach (var group in season.Groups)
            {
                var episode = group.DefaultEpisode;
                _episodes.Add(episode);
                foreach (var version in group.Versions)
                    _versions[version.ProgressKey] = new VersionRef(season.Number, group.Number, version);
                var card = new TvEpisodeCard
                {
                    Style = TvTheme.Style(this, "TvEpisodeCardStyle"),
                    NumberText = group.DisplayNumber,
                    TitleText = group.DefaultEpisode.DisplayTitle,
                    SubtitleText = BuildEpisodeSubtitle(group),
                    ProgressPercent = group.DefaultEpisode.ProgressPercent,
                    ShowProgress = group.DefaultEpisode.IsInProgress,
                    IsWatched = group.Versions.All(version => version.IsCompleted),
                    IsCurrent = IsInProgress(group.DefaultEpisode),
                    Margin = new Thickness(0, 0, 16, 0),
                };
                var captured = group;
                card.Click += (_, _) => PlayGroup(captured);
                _episodePanel.Children.Add(card);
            }
        }
        _episodeScroller.Content = _episodePanel;
        panel.Children.Add(_episodeScroller);
        return panel;
    }

    private static string BuildEpisodeSubtitle(LibraryEpisodeGroup group)
    {
        if (group.HasVersions) return AppStrings.Get("tv.detail.versions", group.Versions.Count);
        return group.DefaultEpisode.IsCompleted
            ? AppStrings.Get("tv.detail.watched")
            : group.DefaultEpisode.IsInProgress
                ? TvPresentation.EpisodeProgressText(group.DefaultEpisode)
                : string.Empty;
    }

    private void PlayGroup(LibraryEpisodeGroup group)
    {
        var episode = group.DefaultEpisode;
        _shell.StartPlayback(_series, episode, resume: episode.IsInProgress);
    }

    private void BuildZones()
    {
        var actions = new List<FrameworkElement>();
        if (_primaryButton is not null) actions.Add(_primaryButton);
        if (_rewatchButton is not null) actions.Add(_rewatchButton);
        if (_versionChip is not null) actions.Add(_versionChip);
        if (_favoriteChip is not null) actions.Add(_favoriteChip);
        _actionZone = new TvZone("actions", TvZoneKind.Horizontal, actions);
        var cards = _episodePanel.Children.Cast<FrameworkElement>().ToList();
        _episodeZone = new TvZone(
            "episodes",
            TvZoneKind.Horizontal,
            cards,
            onFocusChanged: index => BringEpisodeIntoView(index));
        RegisterZones(_actionZone, _episodeZone);
    }

    private void BringEpisodeIntoView(int index)
    {
        if (index < 0 || index >= _episodePanel.Children.Count) return;
        var card = (FrameworkElement)_episodePanel.Children[index];
        card.BringIntoView();
        var transform = card.TransformToAncestor(_episodeScroller);
        var bounds = transform.TransformBounds(new Rect(0, 0, card.ActualWidth, card.ActualHeight));
        if (bounds.Right > _episodeScroller.ViewportWidth)
            _episodeScroller.ScrollToHorizontalOffset(_episodeScroller.HorizontalOffset + bounds.Right - _episodeScroller.ViewportWidth + 40);
        else if (bounds.Left < 0)
            _episodeScroller.ScrollToHorizontalOffset(_episodeScroller.HorizontalOffset + bounds.Left - 40);
    }

    private static bool IsInProgress(LibraryEpisode? episode) => episode is { IsInProgress: true };

    private void ShowVersionOverlay()
    {
        if (_versions.Count == 0 || _series.Episodes.Count == _series.Seasons.Sum(season => season.Groups.Count)) return;
        _versionOverlayVisible = true;
        var entries = _versions.Values
            .OrderBy(reference => reference.Season)
            .ThenBy(reference => reference.Number)
            .ThenBy(reference => reference.Episode.MediaPath, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var panel = new StackPanel { Margin = new Thickness(0) };
        panel.Children.Add(new TextBlock
        {
            Text = AppStrings.Get("tv.detail.versionTitle"),
            FontSize = 26,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 14),
        });
        var buttons = new List<FrameworkElement>();
        foreach (var reference in entries)
        {
            var button = new Button
            {
                Content = $"{reference.Season}/{reference.Number:0.##}  ·  {reference.Episode.VersionLabel}",
                Style = TvTheme.Style(this, "TvSecondaryButton"),
                FocusVisualStyle = null,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Margin = new Thickness(0, 0, 0, 10),
            };
            button.Click += (_, _) =>
            {
                HideVersionOverlay();
                _shell.StartPlayback(_series, reference.Episode, resume: reference.Episode.IsInProgress);
            };
            panel.Children.Add(button);
            buttons.Add(button);
        }
        var overlayPanel = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xF5, 0x12, 0x12, 0x17)),
            BorderBrush = (Brush)FindResource("TvBorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(32),
            Width = 760,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
        };
        _versionOverlay = overlayPanel;
        _root.Children.Add(overlayPanel);
        _overlayZone = new TvZone("versions", TvZoneKind.Vertical, buttons);
        RegisterZone(_overlayZone);
        FocusZone(_overlayZone, 0);
    }

    private void HideVersionOverlay()
    {
        _versionOverlayVisible = false;
        if (_versionOverlay is not null) _root.Children.Remove(_versionOverlay);
        _versionOverlay = null;
        if (_overlayZone is not null) UnregisterZone(_overlayZone);
        _overlayZone = null;
        RestoreFocus($"{_actionZone!.Id}:{Math.Max(0, _actionZone.Items.Count - 1)}");
    }

    public override bool HandleAction(TvInputAction action)
    {
        if (_versionOverlayVisible)
        {
            switch (action)
            {
                case TvInputAction.Up:
                case TvInputAction.Down:
                case TvInputAction.Left:
                case TvInputAction.Right:
                case TvInputAction.Select:
                    return base.HandleAction(action);
                default:
                    return false;
            }
        }
        return base.HandleAction(action);
    }

    public override bool HandleBack()
    {
        if (!_versionOverlayVisible) return false;
        HideVersionOverlay();
        return true;
    }

    protected override bool OnMoveOutOfZone(TvInputAction direction)
    {
        if (direction == TvInputAction.Down && ActiveZone == _actionZone)
        {
            if (_episodeZone is { Items.Count: > 0 }) FocusZone(_episodeZone, 0);
            return true;
        }
        if (direction == TvInputAction.Up && ActiveZone == _episodeZone)
        {
            FocusZone(_actionZone!, 0);
            return true;
        }
        return false;
    }

    private sealed record VersionRef(int Season, double Number, LibraryEpisode Episode);
}
