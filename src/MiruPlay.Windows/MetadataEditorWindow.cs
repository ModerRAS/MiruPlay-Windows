using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using Microsoft.Win32;
using MiruPlay.Windows.Models;
using MiruPlay.Windows.Services;

namespace MiruPlay.Windows;

public sealed class MetadataEditorWindow : Window
{
    private static readonly string[] MetadataProviders = ["bangumi", "tmdb", "tvmaze"];
    private readonly LibrarySeries _series;
    private readonly long _sourceId;
    private readonly MediaSourceRegistry _sources;
    private readonly MetadataTokenStore _tokens;
    private readonly BangumiMetadataClient _bangumi;
    private readonly TmdbMetadataClient _tmdb;
    private readonly TvMazeMetadataClient _tvMaze;
    private readonly BangumiArchiveStore _archive;
    private readonly MetadataSearchService _metadataSearch;
    private readonly string _contentMode;
    private readonly Func<long, Task> _refresh;
    private readonly ListBox _seriesList = new() { SelectionMode = SelectionMode.Extended, MinHeight = 110, MaxHeight = 180 };
    private readonly ListBox _historyList = new() { MinHeight = 70, MaxHeight = 120, DisplayMemberPath = nameof(HistoryItem.Label) };
    private readonly Dictionary<string, TextBox> _fields = new(StringComparer.Ordinal);
    private readonly ComboBox _provider = new() { Width = 130, SelectedIndex = 0 };
    private readonly TextBox _query = new() { MinWidth = 250 };
    private readonly ComboBox _candidates = new() { DisplayMemberPath = nameof(Candidate.Label), MinWidth = 340 };
    private readonly TextBox _archiveQuery = new() { MinWidth = 250 };
    private readonly ListBox _archiveResults = new() { DisplayMemberPath = nameof(ArchiveHit.Label), MinHeight = 70, MaxHeight = 140 };
    private readonly ComboBox _subjectCollectionType = new() { Width = 160 };
    private readonly ListBox _episodeCollections = new() { DisplayMemberPath = nameof(EpisodeCollectionItem.Label), MinHeight = 60, MaxHeight = 110 };
    private readonly TextBox _bangumiEpisodeId = new() { MinWidth = 120, MaxLength = 20 };
    private readonly ComboBox _episodeCollectionType = new() { Width = 150 };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8) };
    private MetadataBatch? _batch;

    public MetadataEditorWindow(
        LibrarySeries series,
        long sourceId,
        MediaSourceRegistry sources,
        MetadataTokenStore tokens,
        BangumiMetadataClient bangumi,
        TmdbMetadataClient tmdb,
        TvMazeMetadataClient tvMaze,
        BangumiArchiveStore archive,
        MetadataSearchService metadataSearch,
        string contentMode,
        Func<long, Task> refresh)
    {
        _series = series;
        _sourceId = sourceId;
        _sources = sources;
        _tokens = tokens;
        _bangumi = bangumi;
        _tmdb = tmdb;
        _tvMaze = tvMaze;
        _archive = archive;
        _metadataSearch = metadataSearch;
        _contentMode = contentMode.Equals("DRAMA", StringComparison.OrdinalIgnoreCase) ? "drama" : "anime";
        _refresh = refresh;
        AutomationProperties.SetName(_seriesList, AppStrings.Get("wpf.edit.localWorks"));
        AutomationProperties.SetName(_historyList, AppStrings.Get("wpf.edit.historyTitle"));
        AutomationProperties.SetName(_provider, AppStrings.Get("wpf.edit.provider"));
        AutomationProperties.SetName(_query, AppStrings.Get("wpf.edit.query"));
        AutomationProperties.SetName(_candidates, AppStrings.Get("wpf.edit.candidates"));
        AutomationProperties.SetName(_archiveQuery, AppStrings.Get("wpf.edit.archiveQuery"));
        AutomationProperties.SetName(_archiveResults, AppStrings.Get("wpf.edit.archiveResults"));
        AutomationProperties.SetName(_subjectCollectionType, AppStrings.Get("wpf.edit.subjectCollection"));
        AutomationProperties.SetName(_episodeCollections, AppStrings.Get("wpf.edit.episodeList"));
        AutomationProperties.SetName(_bangumiEpisodeId, AppStrings.Get("wpf.edit.episodeId"));
        AutomationProperties.SetName(_episodeCollectionType, AppStrings.Get("wpf.edit.episodeStatus"));
        Title = AppStrings.Get("wpf.edit.title", series.Title);
        Width = 720;
        Height = 820;
        MinWidth = 560;
        MinHeight = 600;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var sourceSeries = _sources.LoadCatalog(_sourceId).Series;
        _seriesList.ItemsSource = sourceSeries;
        _seriesList.DisplayMemberPath = nameof(LibrarySeries.Title);
        var episodeKeys = series.Episodes.Select(item => item.ProgressKey).ToHashSet(StringComparer.Ordinal);
        var initialSeries = sourceSeries.FirstOrDefault(item => item.ApiId == series.ApiId)
            ?? sourceSeries.FirstOrDefault(item => series.AlternateApiIds.Contains(item.ApiId, StringComparer.Ordinal) ||
                item.AlternateApiIds.Contains(series.ApiId, StringComparer.Ordinal))
            ?? sourceSeries.FirstOrDefault(item => item.Episodes.Any(episode => episodeKeys.Contains(episode.ProgressKey)));
        if (initialSeries is not null) _seriesList.SelectedItems.Add(initialSeries);
        _seriesList.SelectionChanged += (_, _) =>
        {
            _batch = null;
            if (_seriesList.SelectedItems.Count == 1) LoadSeriesValues();
            _status.Text = _seriesList.SelectedItems.Count > 1 ? AppStrings.Get("wpf.edit.batchNote") : string.Empty;
        };
        Content = BuildContent();
        LoadSeriesValues();
        LoadHistory();
    }

    private DockPanel BuildContent()
    {
        var root = new DockPanel();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(16) };
        AddButton(buttons, AppStrings.Get("meta.undo"), UndoAsync);
        AddButton(buttons, AppStrings.Get("wpf.edit.preview"), PreviewAsync);
        AddButton(buttons, AppStrings.Get("wpf.apply"), ApplyAsync);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);

        var body = new StackPanel { Margin = new Thickness(20) };
        var online = new WrapPanel { Margin = new Thickness(0, 0, 0, 12) };
        _provider.Items.Add(new ComboBoxItem { Content = AppStrings.Get("meta.allSources"), Tag = "all" });
        _provider.Items.Add(new ComboBoxItem { Content = "Bangumi", Tag = "bangumi" });
        _provider.Items.Add(new ComboBoxItem { Content = "TMDB", Tag = "tmdb" });
        _provider.Items.Add(new ComboBoxItem { Content = "TVMaze", Tag = "tvmaze" });
        _provider.SelectedIndex = 0;
        online.Children.Add(_provider);
        _query.Margin = new Thickness(8, 0, 8, 0);
        online.Children.Add(_query);
        AddButton(online, AppStrings.Get("meta.search"), SearchAsync);
        body.Children.Add(online);
        body.Children.Add(new TextBlock { Text = AppStrings.Get("wpf.edit.works"), Margin = new Thickness(0, 4, 0, 4) });
        body.Children.Add(_seriesList);
        _candidates.SelectionChanged += CandidateSelected;
        body.Children.Add(_candidates);
        var archiveActions = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        AddButton(archiveActions, AppStrings.Get("wpf.edit.downloadArchive"), DownloadArchiveAsync);
        AddButton(archiveActions, AppStrings.Get("wpf.edit.importArchive"), ImportArchiveAsync);
        body.Children.Add(archiveActions);
        var archiveSearch = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        archiveSearch.Children.Add(_archiveQuery);
        AddButton(archiveSearch, AppStrings.Get("meta.searchArchive"), SearchArchive);
        body.Children.Add(archiveSearch);
        _archiveResults.SelectionChanged += ArchiveCandidateSelected;
        body.Children.Add(_archiveResults);
        body.Children.Add(new TextBlock { Text = AppStrings.Get("wpf.edit.subjectSection"), Margin = new Thickness(0, 8, 0, 4) });
        AddCollectionOptions(_subjectCollectionType,
            (1, AppStrings.Get("bangumi.want")), (2, AppStrings.Get("bangumi.done")), (3, AppStrings.Get("bangumi.watching")), (4, AppStrings.Get("bangumi.onHold")), (5, AppStrings.Get("bangumi.dropped")));
        var collectionActions = new WrapPanel();
        collectionActions.Children.Add(_subjectCollectionType);
        AddButton(collectionActions, AppStrings.Get("wpf.edit.readCollection"), LoadBangumiStatusAsync);
        AddButton(collectionActions, AppStrings.Get("wpf.edit.saveSubject"), SaveSubjectCollectionAsync);
        body.Children.Add(collectionActions);
        _episodeCollections.SelectionChanged += EpisodeCollectionSelected;
        body.Children.Add(_episodeCollections);
        AddCollectionOptions(_episodeCollectionType,
            (0, AppStrings.Get("bangumi.unmarked")), (1, AppStrings.Get("bangumi.want")), (2, AppStrings.Get("bangumi.done")), (3, AppStrings.Get("bangumi.dropped")));
        var episodeActions = new WrapPanel();
        _bangumiEpisodeId.Margin = new Thickness(0, 0, 8, 0);
        episodeActions.Children.Add(_bangumiEpisodeId);
        episodeActions.Children.Add(_episodeCollectionType);
        AddButton(episodeActions, AppStrings.Get("wpf.edit.saveEpisode"), SaveEpisodeCollectionAsync);
        body.Children.Add(episodeActions);
        body.Children.Add(_status);
        AddField(body, AppStrings.Get("meta.title"), "title", 500);
        AddField(body, AppStrings.Get("meta.original"), "original", 500);
        AddField(body, AppStrings.Get("meta.summary"), "plot", 20_000, multiline: true);
        AddField(body, AppStrings.Get("meta.premiered"), "premiered", 32);
        AddField(body, AppStrings.Get("meta.genres"), "genres", 4_000);
        AddField(body, "Bangumi ID", "bangumi", 100);
        AddField(body, "TMDB ID", "tmdb", 100);
        AddField(body, "TVMaze ID", "tvmaze", 100);
        body.Children.Add(new TextBlock { Text = AppStrings.Get("wpf.edit.historyTitle"), Margin = new Thickness(0, 8, 0, 4) });
        body.Children.Add(_historyList);
        AddButton(body, AppStrings.Get("wpf.edit.undoHistory"), UndoAsync);
        var scroll = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        root.Children.Add(scroll);
        return root;
    }

    private void AddField(Panel parent, string label, string key, int maxLength, bool multiline = false)
    {
        var box = new TextBox { MaxLength = maxLength, Margin = new Thickness(0, 3, 0, 10), AcceptsReturn = multiline, TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap };
        if (multiline) box.Height = 120;
        AutomationProperties.SetName(box, label);
        _fields.Add(key, box);
        box.TextChanged += (_, _) =>
        {
            if (key == "bangumi")
            {
                _episodeCollections.ItemsSource = Array.Empty<EpisodeCollectionItem>();
                _bangumiEpisodeId.Clear();
            }
            if (_batch is null) return;
            _batch = null;
            _status.Text = AppStrings.Get("wpf.edit.contentChanged");
        };
        parent.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 5, 0, 0) });
        parent.Children.Add(box);
    }

    private static void AddButton(Panel parent, string text, Func<Task> action)
    {
        var button = new Button { Content = text, MinWidth = 84, Margin = new Thickness(4), Padding = new Thickness(10, 6, 10, 6) };
        button.Click += async (_, _) => await action();
        parent.Children.Add(button);
    }

    private void LoadSeriesValues()
    {
        var series = (_seriesList.SelectedItems.Count > 0
            ? _seriesList.SelectedItems[0] as LibrarySeries
            : null) ?? _series;
        _fields["title"].Text = series.Title;
        _fields["original"].Text = series.OriginalTitle ?? string.Empty;
        _fields["plot"].Text = series.Summary;
        _fields["premiered"].Text = series.AirDate ?? string.Empty;
        _fields["genres"].Text = string.Join(", ", series.Genres);
        foreach (var provider in new[] { "bangumi", "tmdb", "tvmaze" })
            _fields[provider].Text = series.ExternalId(provider)?.Value ?? string.Empty;
    }

    private string[] SelectedSeriesIds() => _seriesList.SelectedItems
        .Cast<LibrarySeries>()
        .Select(item => item.ApiId)
        .ToArray();

    private void ReloadSeriesList(IEnumerable<string> selectedIds)
    {
        var sourceSeries = _sources.LoadCatalog(_sourceId).Series;
        _seriesList.ItemsSource = sourceSeries;
        foreach (var id in selectedIds)
        {
            var item = sourceSeries.FirstOrDefault(series => series.ApiId == id);
            if (item is not null) _seriesList.SelectedItems.Add(item);
        }
    }

    private void LoadHistory()
    {
        _historyList.ItemsSource = _sources.MetadataHistory(_sourceId)
            .Select(batch => new HistoryItem(batch))
            .ToList();
    }

    private async Task SearchAsync()
    {
        try
        {
            var query = _query.Text.Trim();
            var provider = (_provider.SelectedItem as ComboBoxItem)?.Tag as string;
            if (provider == "all")
            {
                var selected = _seriesList.SelectedItems.OfType<LibrarySeries>().FirstOrDefault();
                var paths = selected?.Episodes.Take(3).Select(item => item.MediaPath).ToArray() ?? [];
                var result = await _metadataSearch.SearchAsync(new MetadataSearchRequest(
                    query, _contentMode, selected?.Title, null, selected?.OriginalTitle,
                    null, paths, _fields["title"].Text,
                    selected?.Year), _tokens.Load().Tmdb);
                _candidates.ItemsSource = result.Candidates.Select(item => new Candidate(
                    0, item.LocalizedTitle ?? item.Title, item.OriginalTitle ?? item.Title, item.Summary, item.FirstAirDate, "aggregate", item)).ToList();
                _status.Text = result.ProviderErrors.Count > 0
                    ? string.Join(" · ", result.ProviderErrors)
                    : result.Candidates.Count == 0 ? AppStrings.Get("wpf.edit.noMatch") : AppStrings.Get("wpf.edit.foundAggregate", result.Candidates.Count);
                return;
            }
            IReadOnlyList<Candidate> candidates = provider switch
            {
                "bangumi" => (await _bangumi.SearchAsync(query)).Select(item => new Candidate(item.Id, item.TitleCn ?? item.Title, item.Title, item.Summary, item.AirDate, "bangumi")).ToList(),
                "tmdb" => (await _tmdb.SearchAsync(query, _tokens.Load().Tmdb ?? throw new InvalidOperationException(AppStrings.Get("wpf.edit.needTmdb")))).Select(item => new Candidate(item.Id, item.Title, item.OriginalTitle, item.Summary, item.FirstAirDate, "tmdb")).ToList(),
                "tvmaze" => (await _tvMaze.SearchAsync(query)).Select(item => new Candidate(item.Id, item.Title, item.OriginalTitle, item.Summary, item.FirstAirDate, "tvmaze")).ToList(),
                _ => [],
            };
            _candidates.ItemsSource = candidates;
            if (candidates.Count == 0) _status.Text = AppStrings.Get("wpf.edit.noMatch");
        }
        catch (Exception error) when (error is HttpRequestException or InvalidOperationException or ArgumentException or InvalidDataException)
        {
            _status.Text = error.Message;
        }
    }

    private void CandidateSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_candidates.SelectedItem is not Candidate item) return;
        _fields["title"].Text = item.Title;
        _fields["original"].Text = item.OriginalTitle ?? string.Empty;
        _fields["plot"].Text = item.Summary ?? string.Empty;
        _fields["premiered"].Text = item.Premiered ?? string.Empty;
        if (item.Aggregate is { } aggregate)
        {
            foreach (var provider in MetadataProviders) _fields[provider].Clear();
            foreach (var candidate in aggregate.Providers)
                if (candidate.Provider.ToLowerInvariant() is "bangumi" or "tmdb" or "tvmaze")
                    _fields[candidate.Provider.ToLowerInvariant()].Text = candidate.Id;
        }
        else
        {
            _fields[item.Provider].Text = item.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    private async Task DownloadArchiveAsync()
    {
        try
        {
            var snapshot = await _archive.DownloadLatestAsync();
            _status.Text = AppStrings.Get("wpf.edit.archiveUpdated", snapshot.SubjectFileSizeBytes.ToString("N0", CultureInfo.CurrentCulture));
        }
        catch (Exception error) when (error is HttpRequestException or IOException or InvalidDataException or OperationCanceledException)
        {
            _status.Text = error.Message;
        }
    }

    private async Task ImportArchiveAsync()
    {
        var dialog = new OpenFileDialog
        {
            CheckFileExists = true,
            Multiselect = false,
            Filter = "Bangumi Archive (*.zip;*.jsonl)|*.zip;*.jsonl|" + AppStrings.Get("wpf.common.allFiles") + " (*.*)|*.*",
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            await using var input = File.OpenRead(dialog.FileName);
            var snapshot = await _archive.ImportAsync(input, Path.GetFileName(dialog.FileName), input.Length);
            _status.Text = AppStrings.Get("wpf.edit.archiveImported", snapshot.SubjectFileSizeBytes.ToString("N0", CultureInfo.CurrentCulture));
        }
        catch (Exception error) when (error is IOException or InvalidDataException or OperationCanceledException)
        {
            _status.Text = error.Message;
        }
    }

    private Task SearchArchive()
    {
        try
        {
            var hits = _archive.Search(_archiveQuery.Text, 20);
            _archiveResults.ItemsSource = hits.Select(hit => new ArchiveHit(hit)).ToList();
            _status.Text = hits.Count == 0 ? AppStrings.Get("wpf.edit.archiveNoMatch") : AppStrings.Get("wpf.edit.archiveFound", hits.Count);
        }
        catch (Exception error) when (error is ArgumentException or InvalidDataException or IOException)
        {
            _status.Text = error.Message;
        }
        return Task.CompletedTask;
    }

    private static void AddCollectionOptions(ComboBox combo, params (int Value, string Label)[] options)
    {
        foreach (var (value, label) in options)
            combo.Items.Add(new ComboBoxItem { Content = $"{label} ({value})", Tag = value });
        combo.SelectedIndex = 0;
    }

    private static int SelectedCollectionType(ComboBox combo) =>
        (combo.SelectedItem as ComboBoxItem)?.Tag is int value
            ? value
            : throw new InvalidOperationException(AppStrings.Get("wpf.edit.selectStatus"));

    private (int SubjectId, string Token) BangumiContext()
    {
        if (!int.TryParse(_fields["bangumi"].Text.Trim(), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var subjectId) || subjectId <= 0)
            throw new InvalidOperationException(AppStrings.Get("wpf.edit.needBangumiId"));
        var token = _tokens.Load().Bangumi?.Trim();
        if (string.IsNullOrEmpty(token)) throw new InvalidOperationException(AppStrings.Get("wpf.edit.needBangumiToken"));
        return (subjectId, token);
    }

    private async Task LoadBangumiStatusAsync()
    {
        try
        {
            var (subjectId, token) = BangumiContext();
            var subject = await _bangumi.GetSubjectCollectionAsync(subjectId, token);
            if (subject is null)
            {
                _episodeCollections.ItemsSource = Array.Empty<EpisodeCollectionItem>();
                _status.Text = AppStrings.Get("wpf.edit.notInCollection");
                return;
            }
            SelectCollectionType(_subjectCollectionType, subject.Type);
            var episodes = await _bangumi.GetEpisodeCollectionsAsync(subjectId, token);
            _episodeCollections.ItemsSource = episodes.Select(item => new EpisodeCollectionItem(item)).ToList();
            _status.Text = AppStrings.Get("wpf.edit.collectionLoaded", episodes.Count);
        }
        catch (Exception error) when (error is HttpRequestException or InvalidOperationException or ArgumentException or InvalidDataException)
        {
            _status.Text = error.Message;
        }
    }

    private async Task SaveSubjectCollectionAsync()
    {
        try
        {
            var (subjectId, token) = BangumiContext();
            await _bangumi.UpsertSubjectCollectionAsync(subjectId, SelectedCollectionType(_subjectCollectionType), token);
            _status.Text = AppStrings.Get("wpf.edit.subjectSaved");
        }
        catch (Exception error) when (error is HttpRequestException or InvalidOperationException or ArgumentException)
        {
            _status.Text = error.Message;
        }
    }

    private async Task SaveEpisodeCollectionAsync()
    {
        try
        {
            var (_, token) = BangumiContext();
            if (!int.TryParse(_bangumiEpisodeId.Text.Trim(), System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out var episodeId) || episodeId <= 0)
                throw new InvalidOperationException(AppStrings.Get("wpf.edit.needEpisodeId"));
            await _bangumi.UpdateEpisodeCollectionAsync(episodeId, SelectedCollectionType(_episodeCollectionType), token);
            _status.Text = AppStrings.Get("wpf.edit.episodeSaved", episodeId);
        }
        catch (Exception error) when (error is HttpRequestException or InvalidOperationException or ArgumentException)
        {
            _status.Text = error.Message;
        }
    }

    private void EpisodeCollectionSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_episodeCollections.SelectedItem is not EpisodeCollectionItem item) return;
        _bangumiEpisodeId.Text = item.State.EpisodeId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        SelectCollectionType(_episodeCollectionType, item.State.Type);
    }

    private static void SelectCollectionType(ComboBox combo, int value)
    {
        combo.SelectedItem = combo.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => item.Tag is int option && option == value);
    }

    private void ArchiveCandidateSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_archiveResults.SelectedItem is not ArchiveHit item) return;
        _fields["title"].Text = item.Hit.TitleCn ?? item.Hit.Title;
        _fields["original"].Text = item.Hit.Title;
        _fields["plot"].Text = item.Hit.Summary ?? string.Empty;
        _fields["premiered"].Text = item.Hit.AirDate ?? string.Empty;
        _fields["bangumi"].Text = item.Hit.AnimeId;
        _status.Text = AppStrings.Get("wpf.edit.archivePicked", item.Hit.AnimeId);
    }

    private Task PreviewAsync()
    {
        try
        {
            var ids = MetadataProviders
                .Where(provider => _fields[provider].Text.Trim().Length > 0)
                .Select(provider => new NfoUniqueId(provider, _fields[provider].Text.Trim(), provider == "bangumi"))
                .ToArray();
            var metadata = new NfoTvShowMetadata(
                Title: _fields["title"].Text.Trim(),
                OriginalTitle: _fields["original"].Text.Trim(),
                Plot: _fields["plot"].Text.Trim(),
                Genres: _fields["genres"].Text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries),
                Premiered: _fields["premiered"].Text.Trim(),
                UniqueIds: ids);
            var selected = _seriesList.SelectedItems.Cast<LibrarySeries>().ToList();
            if (selected.Count == 0) throw new InvalidOperationException(AppStrings.Get("wpf.edit.selectWorks"));
            _batch = _sources.PreviewMetadata(_sourceId,
                selected.Select(item => new SeriesMetadataEdit(item.ApiId, metadata)).ToArray());
            _status.Text = string.Join(Environment.NewLine, _batch.Changes.Select(change => $"{change.BeforeTitle}  →  {change.Metadata.Title}"));
        }
        catch (Exception error) when (error is ArgumentException or InvalidDataException or KeyNotFoundException or NotSupportedException or InvalidOperationException)
        {
            _batch = null;
            _status.Text = error.Message;
        }
        return Task.CompletedTask;
    }

    private async Task ApplyAsync()
    {
        if (_batch is null) return;
        try
        {
            var selectedIds = SelectedSeriesIds();
            var applied = _sources.ApplyMetadata(_sourceId, _batch.Id);
            _batch = null;
            _status.Text = AppStrings.Get("wpf.edit.appliedMeta", applied.Changes.Count);
            LoadHistory();
            await _refresh(_sourceId);
            ReloadSeriesList(selectedIds);
            LoadSeriesValues();
        }
        catch (Exception error) when (error is ArgumentException or InvalidDataException or KeyNotFoundException or NotSupportedException or InvalidOperationException)
        {
            _status.Text = error.Message;
        }
    }

    private async Task UndoAsync()
    {
        if (_historyList.SelectedItem is not HistoryItem selected)
        {
            _status.Text = AppStrings.Get("wpf.edit.selectBatch");
            return;
        }
        try
        {
            var selectedIds = SelectedSeriesIds();
            _sources.UndoMetadata(_sourceId, selected.Batch.Id);
            _batch = null;
            LoadHistory();
            await _refresh(_sourceId);
            ReloadSeriesList(selectedIds);
            LoadSeriesValues();
            _status.Text = AppStrings.Get("meta.undone");
        }
        catch (Exception error) when (error is ArgumentException or InvalidDataException or KeyNotFoundException or NotSupportedException or InvalidOperationException)
        {
            _status.Text = error.Message;
        }
    }

    private sealed record EpisodeCollectionItem(BangumiEpisodeCollectionState State)
    {
        public string Label => AppStrings.Get("wpf.edit.episodeLine", State.EpisodeNumber, State.EpisodeId, State.Type);
    }

    private sealed record ArchiveHit(BangumiArchiveSearchHit Hit)
    {
        public string Label => $"{Hit.TitleCn ?? Hit.Title} · {Hit.AnimeId} · {Hit.Confidence:P0}";
    }

    private sealed record HistoryItem(MetadataBatch Batch)
    {
        public string Label => Batch.State + " · " + AppStrings.Get("meta.historyWorks", Batch.Changes.Count) + " · " + DateTimeOffset.FromUnixTimeMilliseconds(Batch.CreatedAt).ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
    }

    private sealed record Candidate(int Id, string Title, string? OriginalTitle, string? Summary, string? Premiered, string Provider, AggregatedMetadataCandidate? Aggregate = null)
    {
        public string Label => Aggregate is { } aggregate
            ? $"{Title} · {string.Join("/", aggregate.Providers.Select(item => item.Provider).Distinct())} · {aggregate.Recommendation} {aggregate.RerankScore:P0}"
            : $"{Title} · {Provider} · {Id}";
    }
}
