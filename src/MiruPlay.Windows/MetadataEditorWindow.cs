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
        AutomationProperties.SetName(_seriesList, "本地目录作品");
        AutomationProperties.SetName(_historyList, "元数据历史");
        AutomationProperties.SetName(_provider, "元数据提供方");
        AutomationProperties.SetName(_query, "元数据搜索词");
        AutomationProperties.SetName(_candidates, "在线元数据候选");
        AutomationProperties.SetName(_archiveQuery, "Bangumi Archive 搜索词");
        AutomationProperties.SetName(_archiveResults, "Bangumi Archive 搜索结果");
        AutomationProperties.SetName(_subjectCollectionType, "Bangumi 作品收藏状态");
        AutomationProperties.SetName(_episodeCollections, "Bangumi 分集收藏状态列表");
        AutomationProperties.SetName(_bangumiEpisodeId, "Bangumi 分集 ID");
        AutomationProperties.SetName(_episodeCollectionType, "Bangumi 分集状态");
        Title = $"编辑元数据 · {series.Title}";
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
            _status.Text = _seriesList.SelectedItems.Count > 1 ? "预览时会将相同元数据应用到所选作品。" : string.Empty;
        };
        Content = BuildContent();
        LoadSeriesValues();
        LoadHistory();
    }

    private DockPanel BuildContent()
    {
        var root = new DockPanel();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(16) };
        AddButton(buttons, "撤销", UndoAsync);
        AddButton(buttons, "预览", PreviewAsync);
        AddButton(buttons, "应用", ApplyAsync);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);

        var body = new StackPanel { Margin = new Thickness(20) };
        var online = new WrapPanel { Margin = new Thickness(0, 0, 0, 12) };
        _provider.Items.Add(new ComboBoxItem { Content = "全部来源", Tag = "all" });
        _provider.Items.Add(new ComboBoxItem { Content = "Bangumi", Tag = "bangumi" });
        _provider.Items.Add(new ComboBoxItem { Content = "TMDB", Tag = "tmdb" });
        _provider.Items.Add(new ComboBoxItem { Content = "TVMaze", Tag = "tvmaze" });
        _provider.SelectedIndex = 0;
        online.Children.Add(_provider);
        _query.Margin = new Thickness(8, 0, 8, 0);
        online.Children.Add(_query);
        AddButton(online, "搜索", SearchAsync);
        body.Children.Add(online);
        body.Children.Add(new TextBlock { Text = "作品", Margin = new Thickness(0, 4, 0, 4) });
        body.Children.Add(_seriesList);
        _candidates.SelectionChanged += CandidateSelected;
        body.Children.Add(_candidates);
        var archiveActions = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        AddButton(archiveActions, "下载最新 Archive", DownloadArchiveAsync);
        AddButton(archiveActions, "导入 Archive", ImportArchiveAsync);
        body.Children.Add(archiveActions);
        var archiveSearch = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        archiveSearch.Children.Add(_archiveQuery);
        AddButton(archiveSearch, "搜索 Archive", SearchArchive);
        body.Children.Add(archiveSearch);
        _archiveResults.SelectionChanged += ArchiveCandidateSelected;
        body.Children.Add(_archiveResults);
        body.Children.Add(new TextBlock { Text = "Bangumi 收藏与分集状态", Margin = new Thickness(0, 8, 0, 4) });
        AddCollectionOptions(_subjectCollectionType,
            (1, "想看"), (2, "看过"), (3, "在看"), (4, "搁置"), (5, "抛弃"));
        var collectionActions = new WrapPanel();
        collectionActions.Children.Add(_subjectCollectionType);
        AddButton(collectionActions, "读取收藏状态", LoadBangumiStatusAsync);
        AddButton(collectionActions, "保存作品收藏", SaveSubjectCollectionAsync);
        body.Children.Add(collectionActions);
        _episodeCollections.SelectionChanged += EpisodeCollectionSelected;
        body.Children.Add(_episodeCollections);
        AddCollectionOptions(_episodeCollectionType,
            (0, "未标记"), (1, "想看"), (2, "看过"), (3, "抛弃"));
        var episodeActions = new WrapPanel();
        _bangumiEpisodeId.Margin = new Thickness(0, 0, 8, 0);
        episodeActions.Children.Add(_bangumiEpisodeId);
        episodeActions.Children.Add(_episodeCollectionType);
        AddButton(episodeActions, "保存分集状态", SaveEpisodeCollectionAsync);
        body.Children.Add(episodeActions);
        body.Children.Add(_status);
        AddField(body, "标题", "title", 500);
        AddField(body, "原名", "original", 500);
        AddField(body, "简介", "plot", 20_000, multiline: true);
        AddField(body, "首播日期", "premiered", 32);
        AddField(body, "类型（逗号分隔）", "genres", 4_000);
        AddField(body, "Bangumi ID", "bangumi", 100);
        AddField(body, "TMDB ID", "tmdb", 100);
        AddField(body, "TVMaze ID", "tvmaze", 100);
        body.Children.Add(new TextBlock { Text = "元数据历史", Margin = new Thickness(0, 8, 0, 4) });
        body.Children.Add(_historyList);
        AddButton(body, "撤销所选历史", UndoAsync);
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
            _status.Text = "内容已更改，请重新预览。";
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
                    : result.Candidates.Count == 0 ? "没有匹配结果。" : $"找到 {result.Candidates.Count} 个聚合结果。";
                return;
            }
            IReadOnlyList<Candidate> candidates = provider switch
            {
                "bangumi" => (await _bangumi.SearchAsync(query)).Select(item => new Candidate(item.Id, item.TitleCn ?? item.Title, item.Title, item.Summary, item.AirDate, "bangumi")).ToList(),
                "tmdb" => (await _tmdb.SearchAsync(query, _tokens.Load().Tmdb ?? throw new InvalidOperationException("请先在设置中配置 TMDB Token。"))).Select(item => new Candidate(item.Id, item.Title, item.OriginalTitle, item.Summary, item.FirstAirDate, "tmdb")).ToList(),
                "tvmaze" => (await _tvMaze.SearchAsync(query)).Select(item => new Candidate(item.Id, item.Title, item.OriginalTitle, item.Summary, item.FirstAirDate, "tvmaze")).ToList(),
                _ => [],
            };
            _candidates.ItemsSource = candidates;
            if (candidates.Count == 0) _status.Text = "没有匹配结果。";
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
            _status.Text = $"Bangumi Archive 已更新：{snapshot.SubjectFileSizeBytes:N0} 字节。";
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
            Filter = "Bangumi Archive (*.zip;*.jsonl)|*.zip;*.jsonl|所有文件 (*.*)|*.*",
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            await using var input = File.OpenRead(dialog.FileName);
            var snapshot = await _archive.ImportAsync(input, Path.GetFileName(dialog.FileName), input.Length);
            _status.Text = $"Bangumi Archive 已导入：{snapshot.SubjectFileSizeBytes:N0} 字节。";
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
            _status.Text = hits.Count == 0 ? "Archive 没有匹配结果。" : $"找到 {hits.Count} 个 Archive 结果。";
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
            : throw new InvalidOperationException("请选择 Bangumi 状态。");

    private (int SubjectId, string Token) BangumiContext()
    {
        if (!int.TryParse(_fields["bangumi"].Text.Trim(), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var subjectId) || subjectId <= 0)
            throw new InvalidOperationException("请先填写有效的 Bangumi ID。");
        var token = _tokens.Load().Bangumi?.Trim();
        if (string.IsNullOrEmpty(token)) throw new InvalidOperationException("请先在设置中配置 Bangumi Access Token。");
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
                _status.Text = "该作品尚未加入 Bangumi 收藏。";
                return;
            }
            SelectCollectionType(_subjectCollectionType, subject.Type);
            var episodes = await _bangumi.GetEpisodeCollectionsAsync(subjectId, token);
            _episodeCollections.ItemsSource = episodes.Select(item => new EpisodeCollectionItem(item)).ToList();
            _status.Text = $"已读取 Bangumi 收藏和 {episodes.Count} 条分集状态。";
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
            _status.Text = "Bangumi 作品收藏已保存。";
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
                throw new InvalidOperationException("请选择或填写有效的 Bangumi 分集 ID。");
            await _bangumi.UpdateEpisodeCollectionAsync(episodeId, SelectedCollectionType(_episodeCollectionType), token);
            _status.Text = $"Bangumi 分集 {episodeId} 状态已保存。";
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
        _status.Text = $"已选择 Bangumi Archive 匹配：{item.Hit.AnimeId}。";
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
            if (selected.Count == 0) throw new InvalidOperationException("请至少选择一部本地目录作品。");
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
            _status.Text = $"元数据已应用到本地索引（{applied.Changes.Count} 部作品）。";
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
            _status.Text = "请先从历史列表选择一批已应用的元数据。";
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
            _status.Text = "本地元数据已撤销。";
        }
        catch (Exception error) when (error is ArgumentException or InvalidDataException or KeyNotFoundException or NotSupportedException or InvalidOperationException)
        {
            _status.Text = error.Message;
        }
    }

    private sealed record EpisodeCollectionItem(BangumiEpisodeCollectionState State)
    {
        public string Label => $"第 {State.EpisodeNumber} 集 · ID {State.EpisodeId} · 状态 {State.Type}";
    }

    private sealed record ArchiveHit(BangumiArchiveSearchHit Hit)
    {
        public string Label => $"{Hit.TitleCn ?? Hit.Title} · {Hit.AnimeId} · {Hit.Confidence:P0}";
    }

    private sealed record HistoryItem(MetadataBatch Batch)
    {
        public string Label => $"{Batch.State} · {Batch.Changes.Count} 部作品 · {DateTimeOffset.FromUnixTimeMilliseconds(Batch.CreatedAt).ToLocalTime():g}";
    }

    private sealed record Candidate(int Id, string Title, string? OriginalTitle, string? Summary, string? Premiered, string Provider, AggregatedMetadataCandidate? Aggregate = null)
    {
        public string Label => Aggregate is { } aggregate
            ? $"{Title} · {string.Join("/", aggregate.Providers.Select(item => item.Provider).Distinct())} · {aggregate.Recommendation} {aggregate.RerankScore:P0}"
            : $"{Title} · {Provider} · {Id}";
    }
}
