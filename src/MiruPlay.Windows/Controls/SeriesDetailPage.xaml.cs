using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MiruPlay.Windows.Models;
using MiruPlay.Windows.Services;

namespace MiruPlay.Windows.Controls;

public sealed class LibraryEpisodeEventArgs(LibraryEpisode episode) : EventArgs
{
    public LibraryEpisode Episode { get; } = episode;
}

public sealed class SeriesPlayRequestedEventArgs(SeriesPlayTarget target) : EventArgs
{
    public SeriesPlayTarget Target { get; } = target;
}

public sealed class UriEventArgs(Uri uri) : EventArgs
{
    public Uri Uri { get; } = uri;
}

public partial class SeriesDetailPage : UserControl
{
    private SeriesDetailViewState? _state;

    public SeriesDetailPage() => InitializeComponent();

    public event EventHandler? BackRequested;
    public event EventHandler<LibraryEpisodeEventArgs>? PlayRequested;
    public event EventHandler<SeriesPlayRequestedEventArgs>? TargetRequested;
    public event EventHandler<UriEventArgs>? MetadataRequested;
    public event EventHandler? EditMetadataRequested;

    public LibrarySeries? CurrentSeries => _state?.Series;

    public void Present(SeriesDetailViewState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var selectedSeason = SeasonSelector.SelectedItem as SeriesDetailSeasonViewState;
        _state = state;
        DataContext = state;
        SeasonSelector.ItemsSource = state.Seasons;
        SeasonSelector.SelectedItem = state.Seasons.FirstOrDefault(season => season.Number == selectedSeason?.Number)
            ?? (state.Seasons.Count > 0 ? state.Seasons[0] : null);
        ExtrasList.ItemsSource = state.Extras;
        ExtrasSection.Visibility = state.Extras.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        PrimaryActionButton.Tag = state.PrimaryAction?.Episode;
        PrimaryActionButton.Content = state.PrimaryAction?.Label;
        PrimaryActionButton.Visibility = state.PrimaryAction is null ? Visibility.Collapsed : Visibility.Visible;
        DetailEmptyState.Visibility = state.EmptyStateText is null ? Visibility.Collapsed : Visibility.Visible;
        SeasonHeader.Visibility = state.Seasons.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateResponsiveLayout(ActualWidth);
    }

    public void SetSeries(LibrarySeries series) => Present(SeriesDetailPresentation.BuildViewState(series));

    public void SetMetadataEditAvailable(bool available) =>
        EditMetadataButton.Visibility = available ? Visibility.Visible : Visibility.Collapsed;

    public void Clear()
    {
        _state = null;
        EpisodeList.ItemsSource = null;
        SeasonSelector.ItemsSource = null;
        ExtrasList.ItemsSource = null;
        PrimaryActionButton.Tag = null;
        PrimaryActionButton.Visibility = Visibility.Collapsed;
        EditMetadataButton.Visibility = Visibility.Collapsed;
        DataContext = null;
    }

    public void ClearSeries() => Clear();

    public bool FocusInitial()
    {
        if (PrimaryActionButton.Visibility == Visibility.Visible && PrimaryActionButton.Focus()) return true;
        if (SeasonSelector.Visibility == Visibility.Visible && SeasonSelector.Focus()) return true;
        return BackButton.Focus();
    }

    private void Back_Click(object sender, RoutedEventArgs e) => BackRequested?.Invoke(this, EventArgs.Empty);

    private void PrimaryAction_Click(object sender, RoutedEventArgs e)
    {
        if (PrimaryActionButton.Tag is LibraryEpisode episode) RaiseEpisode(episode);
    }

    private void Episode_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: LibraryEpisode episode }) RaiseEpisode(episode);
    }

    private void Extra_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: LibraryExtra extra })
            TargetRequested?.Invoke(this, new(new SeriesExtraPlayTarget(extra)));
    }

    private void RaiseEpisode(LibraryEpisode episode)
    {
        PlayRequested?.Invoke(this, new(episode));
        TargetRequested?.Invoke(this, new(new SeriesEpisodePlayTarget(episode)));
    }

    private void EditMetadata_Click(object sender, RoutedEventArgs e) => EditMetadataRequested?.Invoke(this, EventArgs.Empty);

    private void Metadata_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: Uri uri }) MetadataRequested?.Invoke(this, new(uri));
    }

    private void SeasonSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        EpisodeList.ItemsSource = (SeasonSelector.SelectedItem as SeriesDetailSeasonViewState)?.Episodes;
    }

    private void DetailRoot_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateResponsiveLayout(e.NewSize.Width);

    private void UpdateResponsiveLayout(double width)
    {
        var compact = width > 0 && width < 840;
        PosterBorder.Width = compact ? 132 : width < 1120 ? 180 : 200;
        PosterBorder.Height = compact ? 190 : width < 1120 ? 258 : 286;
        Grid.SetRow(DetailCopy, compact ? 1 : 0);
        Grid.SetColumn(DetailCopy, compact ? 0 : 1);
        Grid.SetColumnSpan(DetailCopy, compact ? 2 : 1);
        DetailCopy.Margin = compact ? new Thickness(0, 18, 0, 0) : new Thickness(26, 0, 0, 0);
        Grid.SetRow(PrimaryActionButton, compact ? 1 : 0);
        Grid.SetColumn(PrimaryActionButton, compact ? 0 : 2);
        Grid.SetColumnSpan(PrimaryActionButton, compact ? 3 : 1);
        PrimaryActionButton.HorizontalAlignment = compact ? HorizontalAlignment.Stretch : HorizontalAlignment.Right;
        PrimaryActionButton.Margin = compact ? new Thickness(0, 12, 0, 0) : new Thickness(0);
        CompactHeaderRow.Height = compact ? GridLength.Auto : new GridLength(0);
    }
}
