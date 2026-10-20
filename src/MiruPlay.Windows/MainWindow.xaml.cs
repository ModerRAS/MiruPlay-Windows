using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using MiruPlay.Windows.Controls;
using MiruPlay.Windows.Models;
using MiruPlay.Windows.Services;

namespace MiruPlay.Windows;

public partial class MainWindow : Window, IAsyncDisposable
{
    private readonly AppSettingsStore _settingsStore = new();
    private readonly PlaybackProgressStore _progressStore = new();
    private readonly WebControlTokenStore _webControlTokens = new();
    private readonly MetadataTokenStore _metadataTokens = new();
    private readonly CloudDriveAutomationStore _cloudDriveConfig = new();
    private readonly BangumiMetadataClient _bangumiMetadata;
    private readonly BangumiArchiveStore _bangumiArchive;
    private readonly TmdbMetadataClient _tmdbMetadata;
    private readonly TvMazeMetadataClient _tvMazeMetadata;
    private readonly MetadataSearchService _metadataSearch;
    private readonly BangumiPlaybackSyncService _bangumiPlaybackSync;
    private readonly RssSubscriptionStore _rssSubscriptions = new();
    private readonly CloudDriveCredentialStore _cloudDriveCredentials = new();
    private readonly CloudDriveGrpcClient _cloudDriveClient = new();
    private readonly RssFeedClient _rssFeedClient = new();
    private readonly RssProcessedStore _rssProcessed = new();
    private readonly CloudDriveRssRunner _cloudDriveRunner;
    private readonly CloudDriveRssScheduler _cloudDriveScheduler;
    private readonly MediaSourceAutoScanScheduler _mediaSourceAutoScanScheduler;
    private readonly MediaSourceRegistry _mediaSourceRegistry;
    private readonly RotatingLocalLogStore _localLogs = new();
    private readonly OpenObserveLogService _openObserveLogs;
    private readonly OpenObserveLogScheduler _openObserveLogScheduler;
    private readonly WindowsAppUpdater _appUpdater;
    private readonly ToppingController _topping = new();
    private readonly MusicLibraryService _musicLibrary = new();
    private readonly HeadlessTaskScheduler _tasks = new();
    private readonly WebControlServer _webControlServer;
    private const int SeriesPageSize = 40;
    private AppSettings _settings = new();
    private List<LibrarySeries> _catalogSeries = [];
    private List<LibrarySeries> _allSeries = [];
    private List<LibrarySeries> _filteredSeries = [];
    private readonly HashSet<string> _requestedPosterUrls = new(StringComparer.Ordinal);
    private readonly HashSet<IPlaybackSession> _supersededSessions = [];
    private readonly SemaphoreSlim _playbackStartLock = new(1, 1);
    private long _playbackGeneration;
    private int _visibleSeriesCount;
    private int _libraryGeneration;
    private long? _loadedSourceId;
    private CancellationTokenSource? _posterCacheCancellation;
    private bool _loadingSettings;
    private readonly bool _backgroundMode;
    private bool _shutdownStarted;
    private bool _shutdownComplete;
    private long? _editingRssId;
    private IPlaybackSession? _activeSession;
    private MpvPlaybackQueue? _activePlaybackQueue;
    private CancellationTokenSource? _scanCancellation;
    private readonly PlaybackNavigationState _playbackNavigation = new(PlaybackPageMode.Library, false);
    private LibrarySeries? _activePlaybackSeries;
    private LibraryEpisode? _activePlaybackEpisode;
    private WindowStyle _savedWindowStyle;
    private WindowState _savedWindowState;
    private ResizeMode _savedResizeMode;
    private GridLength _savedNavigationWidth;
    private bool _savedPlaybackSidebarVisible;
    private bool _isFullscreen;

    public MainWindow() : this(false) { }

    internal MainWindow(bool backgroundMode)
    {
        _backgroundMode = backgroundMode;
        InitializeComponent();
        PlaybackPageView.ActionRequested += PlaybackPage_ActionRequested;
        SeriesDetailPageView.BackRequested += SeriesDetailPage_BackRequested;
        SeriesDetailPageView.PlayRequested += SeriesDetailPage_PlayRequested;
        SeriesDetailPageView.MetadataRequested += SeriesDetailPage_MetadataRequested;
        SeriesDetailPageView.EditMetadataRequested += SeriesDetailPage_EditMetadataRequested;
        _settings = _settingsStore.Load();
        _bangumiMetadata = new BangumiMetadataClient(OutboundHttpProxy.CreateHandler(() => _cloudDriveConfig.Load()));
        _tmdbMetadata = new TmdbMetadataClient(OutboundHttpProxy.CreateHandler(() => _cloudDriveConfig.Load()));
        _tvMazeMetadata = new TvMazeMetadataClient(OutboundHttpProxy.CreateHandler(() => _cloudDriveConfig.Load()));
        var archiveClient = new BangumiArchiveClient(handler: OutboundHttpProxy.CreateHandler(() => _cloudDriveConfig.Load()));
        _bangumiArchive = new BangumiArchiveStore(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MiruPlay", "bangumi-archive"), archiveClient);
        _appUpdater = new WindowsAppUpdater(manifestUrl: _settings.AppUpdateManifestUrl);
        _metadataSearch = new MetadataSearchService(_bangumiMetadata, _tmdbMetadata, _tvMazeMetadata);
        _bangumiPlaybackSync = new BangumiPlaybackSyncService(
            _metadataTokens,
            _bangumiMetadata.UpdateEpisodeCollectionAsync);
        _mediaSourceRegistry = new MediaSourceRegistry(
            () => _settings,
            UpdateSettingsFromMediaSource,
            rssMediaCatalogReader: new RssMediaCatalogReader(_rssFeedClient, () => _cloudDriveConfig.Load()));
        _mediaSourceAutoScanScheduler = new MediaSourceAutoScanScheduler(
            () => _settings,
            _mediaSourceRegistry.List,
            ScanSourceAutomaticallyAsync);
        _openObserveLogs = new OpenObserveLogService(_localLogs, new OpenObserveTokenStore());
        _openObserveLogScheduler = new OpenObserveLogScheduler(
            () => _settings,
            UpdateSettingsFromMediaSource,
            _openObserveLogs,
            _tasks,
            localLogs: _localLogs);
        _cloudDriveRunner = new CloudDriveRssRunner(
            _cloudDriveConfig,
            _cloudDriveCredentials,
            _rssSubscriptions,
            _rssFeedClient,
            _rssProcessed,
            _cloudDriveClient,
            rescanWebDav: RescanLinkedWebDavAsync,
            localLogs: _localLogs);
        _cloudDriveScheduler = new CloudDriveRssScheduler(_cloudDriveConfig, _cloudDriveRunner, _tasks);
        _webControlServer = new WebControlServer(
            _settings.WebControlPort,
            _webControlTokens,
            () => _allSeries,
            GetPlaybackRuntimeStatus,
            PlayEpisodeFromWebAsync,
            ExecutePlaybackCommandFromWebAsync,
            () => _settings,
            UpdateSettingsFromWebControlAsync,
            new MediaSourceActions(
                _mediaSourceRegistry.List,
                _mediaSourceRegistry.TestAsync,
                AddSourceFromWebAsync,
                UpdateSourceFromWebAsync,
                RemoveSourceFromWebAsync,
                ScanSourceFromWebAsync),
            bangumiMetadata: _bangumiMetadata,
            tmdbMetadata: _tmdbMetadata,
            metadataTokens: _metadataTokens,
            rssSubscriptions: _rssSubscriptions,
            cloudDriveConfig: _cloudDriveConfig,
            cloudDriveCredentials: _cloudDriveCredentials,
            cloudDriveClient: _cloudDriveClient,
            rssFeedClient: _rssFeedClient,
            rssProcessed: _rssProcessed,
            cloudDriveRunner: _cloudDriveRunner,
            localLogs: _localLogs,
            openObserveLogs: _openObserveLogs,
            appUpdater: _appUpdater,
            tasks: _tasks,
            resolvePosterPath: ResolvePosterForWebAsync,
            applyAudioDsp: ApplyAudioDspFromWebAsync,
            metadataSources: _mediaSourceRegistry,
            refreshSource: RefreshSourceAfterMetadataAsync,
            bangumiArchive: _bangumiArchive,
            tvMazeMetadata: _tvMazeMetadata,
            metadataSearch: _metadataSearch,
            appControl: HandleAppControlAsync,
            installUpdate: HandleAppUpdateInstallAsync,
            getPlaybackDiagnostics: GetPlaybackNativeDiagnostics,
            topping: _topping,
            findAudioPreset: FindAudioPreset,
            musicLibrary: _musicLibrary,
            playMusic: PlayMusicFromWebAsync);
        ApplySettingsToView();
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
    }

    private void PresentSeriesDetails(LibrarySeries series)
    {
        SeriesDetailPageView.SetSeries(series);
        SeriesDetailPageView.SetMetadataEditAvailable(FindWritableMetadataSource(series) is not null);
    }

    private MediaSourceDefinition? FindWritableMetadataSource(LibrarySeries series) => series.Episodes
        .Select(episode => _settings.MediaSources?.FirstOrDefault(item => item.Id == episode.SourceId))
        .FirstOrDefault(source => source?.RecognitionMode == "DIRECTORY");

    private void SeriesDetailPage_EditMetadataRequested(object? sender, EventArgs e)
    {
        if (SeriesDetailPageView.CurrentSeries is not { } series) return;
        var source = FindWritableMetadataSource(series);
        var episode = source is null ? null : series.Episodes.FirstOrDefault(item => item.SourceId == source.Id);
        if (source is null || episode is null) return;
        var dialog = new MetadataEditorWindow(series, source.Id, _mediaSourceRegistry, _metadataTokens,
            _bangumiMetadata, _tmdbMetadata, _tvMazeMetadata, _bangumiArchive, _metadataSearch,
            source.ContentMode, RefreshSourceAfterMetadataAsync) { Owner = this };
        dialog.ShowDialog();
    }

    private Task RefreshSourceAfterMetadataAsync(long sourceId) =>
        Dispatcher.InvokeAsync(async () =>
        {
            var source = _mediaSourceRegistry.Get(sourceId);
            if (source is not null && _loadedSourceId == sourceId) await LoadSourceAsync(source);
        }).Task.Unwrap();

    private Task ApplyAudioDspFromWebAsync(AudioDspConfig config) =>
        Dispatcher.InvokeAsync(() => ApplyAudioDspConfigAsync(config)).Task.Unwrap();

    private async Task ApplyAudioDspConfigAsync(AudioDspConfig config)
    {
        var graph = AudioDspFilterGraphCompiler.Compile(
            config, AudioDspChannelLayout.Stereo, 48_000);
        if (_activeSession is { IsActive: true } session)
            await session.ApplyAudioDspAsync(graph).ConfigureAwait(true);
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        _localLogs.Write("INFO", _backgroundMode ? "MiruPlay background mode started" : "MiruPlay started", attributes: new Dictionary<string, string>
        {
            ["mode"] = CurrentContentMode,
            ["sourceCount"] = (_settings.MediaSources?.Count ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture),
        });
        if (_settings.WebControlEnabled)
        {
            try
            {
                await _webControlServer.StartAsync();
                _localLogs.Write("INFO", "WebControl started", attributes: new Dictionary<string, string>
                {
                    ["port"] = _settings.WebControlPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
                });
                StatusText.Text = $"WebControl 已启动：{_webControlServer.PreferredAccessUrl}";
            }
            catch (IOException error)
            {
                _localLogs.Write("ERROR", "WebControl failed to start", error);
                StatusText.Text = $"WebControl 启动失败：{error.Message}";
            }
        }
        UpdateWebControlView();
        if (_backgroundMode && _settings.WebControlEnabled && _webControlServer.IsRunning) Hide();
        else if (_backgroundMode) StatusText.Text = "后台启动未能启用 WebControl，窗口保持可见。";
        _openObserveLogScheduler.Start();
        _cloudDriveScheduler.Start();
        _mediaSourceAutoScanScheduler.Start();

        var contentMode = CurrentContentMode;
        var activeSource = _settings.ActiveSourceId is long sourceId
            ? _mediaSourceRegistry.Get(sourceId)
            : null;
        if (activeSource?.ContentMode != contentMode)
        {
            activeSource = (_settings.MediaSources ?? []).FirstOrDefault(source => source.ContentMode == contentMode);
        }
        if (activeSource is not null) await LoadSourceAsync(activeSource);
    }

    private async void OpenLibrary_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new LocalSourceDialog(
            sourceLocation: Directory.Exists(_settings.LibraryRoot) ? _settings.LibraryRoot : null)
        {
            Owner = this,
        };
        if (dialog.ShowDialog() != true) return;
        await AddOrActivateLocalSourceAsync(dialog.SourceName, dialog.SourceLocation, dialog.RecognitionMode);
    }

    private async void AddWebDavSource_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new WebDavSourceDialog { Owner = this };
        if (dialog.ShowDialog() != true) return;

        var password = dialog.TakePassword();
        BusyOverlay.Visibility = Visibility.Visible;
        StatusText.Text = $"正在连接 WebDAV 并验证{(dialog.RecognitionMode == "DIRECTORY" ? "目录" : " MLIP")}…";
        try
        {
            var source = await _mediaSourceRegistry.AddAsync(new MediaSourceRequest(
                dialog.SourceName,
                "WEBDAV",
                dialog.SourceLocation,
                Username: dialog.Username,
                Password: password,
                ContentMode: CurrentContentMode,
                RecognitionMode: dialog.RecognitionMode));
            await LoadSourceAsync(_mediaSourceRegistry.Get(source.Id)!);
        }
        catch (Exception error) when (error is HttpRequestException or IOException or InvalidDataException or InvalidOperationException or NotSupportedException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, error.Message, "无法添加 WebDAV", MessageBoxButton.OK, MessageBoxImage.Warning);
            StatusText.Text = error.Message;
        }
        finally
        {
            password = string.Empty;
            BusyOverlay.Visibility = Visibility.Collapsed;
        }
    }

    private async void AddRssMediaSource_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new RssMediaSourceDialog { Owner = this };
        if (dialog.ShowDialog() != true) return;
        try
        {
            var source = await _mediaSourceRegistry.AddAsync(new MediaSourceRequest(
                dialog.SourceName,
                "RSS",
                dialog.SourceLocation,
                ContentMode: CurrentContentMode,
                RecognitionMode: "RSS"));
            await LoadSourceAsync(_mediaSourceRegistry.Get(source.Id)!);
        }
        catch (Exception error) when (error is ArgumentException or HttpRequestException or IOException or InvalidDataException or InvalidOperationException or NotSupportedException or TaskCanceledException)
        {
            MessageBox.Show(this, error.Message, "无法添加 RSS 在线源", MessageBoxButton.OK, MessageBoxImage.Warning);
            StatusText.Text = error.Message;
        }
    }

    private async void AddSmbSource_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SmbSourceDialog { Owner = this };
        if (dialog.ShowDialog() != true) return;

        var password = dialog.TakePassword();
        BusyOverlay.Visibility = Visibility.Visible;
        StatusText.Text = $"正在连接 SMB 并验证{(dialog.RecognitionMode == "DIRECTORY" ? "目录" : " MLIP")}…";
        try
        {
            var source = await _mediaSourceRegistry.AddAsync(new MediaSourceRequest(
                dialog.SourceName,
                "SMB",
                dialog.SourceLocation,
                Username: dialog.Username,
                Password: password,
                Domain: dialog.Domain,
                ContentMode: CurrentContentMode,
                RecognitionMode: dialog.RecognitionMode));
            await LoadSourceAsync(_mediaSourceRegistry.Get(source.Id)!);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or InvalidOperationException or NotSupportedException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, error.Message, "无法添加 SMB", MessageBoxButton.OK, MessageBoxImage.Warning);
            StatusText.Text = error.Message;
        }
        finally
        {
            password = string.Empty;
            BusyOverlay.Visibility = Visibility.Collapsed;
        }
    }

    private async Task AddOrActivateLocalSourceAsync(string sourceName, string rootPath, string recognitionMode)
    {
        try
        {
            var fullPath = Path.GetFullPath(rootPath);
            var existing = (_settings.MediaSources ?? []).FirstOrDefault(source => SamePath(source.Location, fullPath));
            if (existing is null)
            {
                var name = string.IsNullOrWhiteSpace(sourceName)
                    ? Path.GetFileName(fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                    : sourceName;
                await _mediaSourceRegistry.AddAsync(new MediaSourceRequest(
                    name,
                    "LOCAL",
                    fullPath,
                    ContentMode: CurrentContentMode,
                    RecognitionMode: recognitionMode));
            }
            else if (!existing.RecognitionMode.Equals(recognitionMode, StringComparison.OrdinalIgnoreCase))
            {
                await _mediaSourceRegistry.UpdateAsync(existing.Id, new MediaSourceRequest(
                    sourceName,
                    "LOCAL",
                    fullPath,
                    ContentMode: CurrentContentMode,
                    RecognitionMode: recognitionMode));
            }
            else
            {
                _settings = _settings with { LibraryRoot = existing.Location, ActiveSourceId = existing.Id };
                _settingsStore.Save(_settings);
                ApplySettingsToView();
            }
            await LoadSourceAsync((_settings.MediaSources ?? []).First(source => SamePath(source.Location, fullPath)));
        }
        catch (Exception error) when (error is IOException or InvalidDataException or InvalidOperationException or NotSupportedException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            MessageBox.Show(this, error.Message, "无法添加媒体源", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task LoadLibraryAsync(string rootPath)
    {
        var source = (_settings.MediaSources ?? []).FirstOrDefault(item => item.Type == "LOCAL" && SamePath(item.Location, rootPath))
            ?? throw new InvalidOperationException("本地媒体源不存在。");
        await LoadSourceAsync(source);
    }

    private async Task LoadSourceAsync(MediaSourceDefinition source)
    {
        if (_loadedSourceId != source.Id)
        {
            if (_activePlaybackEpisode is not null) await LeavePlaybackAsync();
            ShowPage(SettingsPage.Visibility == Visibility.Visible);
        }
        var generation = ++_libraryGeneration;
        _posterCacheCancellation?.Cancel();
        _posterCacheCancellation?.Dispose();
        _posterCacheCancellation = new CancellationTokenSource();
        _loadedSourceId = null;
        _requestedPosterUrls.Clear();
        BusyOverlay.Visibility = Visibility.Visible;
        StatusText.Text = "正在读取媒体库…";
        try
        {
            var catalog = await Task.Run(() => _mediaSourceRegistry.LoadCatalog(source.Id));
            if (generation != _libraryGeneration) return;
            var series = catalog.Series.Select(item => item with
            {
                Episodes = item.Episodes.Select(episode => episode with { SourceId = source.Id }).ToList(),
            }).ToList();
            _catalogSeries = ApplyProgress(series);
            _allSeries = LibraryOrganization.Apply(_catalogSeries, _settings);
            _loadedSourceId = source.Id;
            UpdateContinueWatching();
            _settings = _settings with
            {
                ActiveSourceId = source.Id,
                LibraryRoot = source.Type == "LOCAL" ? source.Location : null,
                CurrentAppMode = source.ContentMode.ToLowerInvariant(),
            };
            _settingsStore.Save(_settings);
            LibraryRootValue.Text = source.Location;
            LibrarySchemaText.Text = source.Type == "RSS" ? "RSS 在线源" : $"MLIP v{catalog.SchemaVersion}";
            LibrarySummaryText.Text = $"{_allSeries.Count} 部作品 · {_allSeries.Sum(item => item.Episodes.Count)} 集";
            SearchBox.Text = string.Empty;
            ShowSeries(_allSeries);
            ApplySettingsToView();
            StatusText.Text = $"已载入 {_allSeries.Count} 部作品";
        }
        catch (Exception error) when (error is IOException or HttpRequestException or InvalidDataException or Microsoft.Data.Sqlite.SqliteException or NotSupportedException or TaskCanceledException or ArgumentException)
        {
            ClearLibraryView();
            StatusText.Text = error.Message;
            MessageBox.Show(this, error.Message, "无法打开媒体库", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            BusyOverlay.Visibility = Visibility.Collapsed;
        }
    }

    private void ShowSeries(List<LibrarySeries> series)
    {
        _filteredSeries = series;
        _visibleSeriesCount = Math.Min(SeriesPageSize, series.Count);
        RenderSeriesPage();
        UpdateBrowseSections();
        EmptyState.Visibility = series.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_allSeries.Count > 0 && series.Count == 0)
        {
            EmptyStateTitle.Text = "没有匹配的内容";
            EmptyStateDescription.Text = "换一个标题、原名或类型关键词。";
        }
        else
        {
            EmptyStateTitle.Text = "还没有媒体库";
            EmptyStateDescription.Text = "选择一个由 anime-organizer 生成、根目录包含 library.db 的媒体目录。";
        }
    }

    private void LoadMore_Click(object sender, RoutedEventArgs e)
    {
        _visibleSeriesCount = Math.Min(_visibleSeriesCount + SeriesPageSize, _filteredSeries.Count);
        RenderSeriesPage();
    }

    private void RenderSeriesPage()
    {
        // ponytail: WPF WrapPanel is not virtualized; page cards until a proven virtualizing panel is needed.
        SeriesList.ItemsSource = _filteredSeries.Take(_visibleSeriesCount).ToList();
        LoadMoreButton.Visibility = _visibleSeriesCount < _filteredSeries.Count
            ? Visibility.Visible
            : Visibility.Collapsed;
        _ = CacheVisiblePostersAsync(_libraryGeneration);
    }

    private async Task<string?> ResolvePosterForWebAsync(LibrarySeries series, CancellationToken cancellationToken)
    {
        if (series.PosterUri?.IsFile == true) return series.PosterPath;
        if (_loadedSourceId is not long sourceId ||
            _mediaSourceRegistry.Get(sourceId)?.Type != "WEBDAV" ||
            series.PosterUri?.Scheme is not ("http" or "https")) return null;
        return await _mediaSourceRegistry.CacheArtworkAsync(sourceId, series.PosterPath!, cancellationToken);
    }

    private async Task CacheVisiblePostersAsync(int generation)
    {
        if (_loadedSourceId is not long sourceId ||
            _mediaSourceRegistry.Get(sourceId)?.Type != "WEBDAV") return;
        var pending = _filteredSeries
            .Take(_visibleSeriesCount)
            .Where(series => series.PosterUri?.Scheme is "http" or "https")
            .Where(series => _requestedPosterUrls.Add(series.PosterPath!))
            .ToList();
        if (pending.Count == 0) return;

        var cached = new System.Collections.Concurrent.ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        try
        {
            await Parallel.ForEachAsync(
                pending,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = 4,
                    CancellationToken = _posterCacheCancellation?.Token ?? CancellationToken.None,
                },
                async (series, cancellationToken) =>
                {
                    try
                    {
                        cached[series.Uuid] = await _mediaSourceRegistry.CacheArtworkAsync(
                            sourceId,
                            series.PosterPath!,
                            cancellationToken);
                    }
                    catch (Exception error) when (error is HttpRequestException or IOException or InvalidDataException or UnauthorizedAccessException)
                    {
                        Debug.WriteLine($"WebDAV poster cache failed: {error.Message}");
                    }
                });
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (cached.IsEmpty || generation != _libraryGeneration || _loadedSourceId != sourceId) return;

        _catalogSeries = _catalogSeries.Select(series =>
            cached.TryGetValue(series.Uuid, out var posterPath) ? series with { PosterPath = posterPath } : series).ToList();
        _allSeries = LibraryOrganization.Apply(_catalogSeries, _settings);
        var replacements = _allSeries.ToDictionary(series => series.Uuid, StringComparer.Ordinal);
        _filteredSeries = _filteredSeries.Select(series => replacements.GetValueOrDefault(series.Uuid, series)).ToList();
        if (DetailPanel.DataContext is LibrarySeries detail && replacements.TryGetValue(detail.Uuid, out var replacement))
        {
            DetailPanel.DataContext = replacement;
        }
        RenderSeriesPage();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplySearchFilter();

    private void ApplySearchFilter()
    {
        var query = SearchBox.Text.Trim();
        if (query.Length == 0)
        {
            ShowSeries(_allSeries);
            return;
        }

        ShowSeries(_allSeries.Where(series =>
            series.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
            (series.OriginalTitle?.Contains(query, StringComparison.CurrentCultureIgnoreCase) ?? false) ||
            series.Genres.Any(genre => genre.Contains(query, StringComparison.CurrentCultureIgnoreCase))).ToList());
    }

    private void SeriesCard_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: LibrarySeries series }) return;
        DetailPanel.Visibility = Visibility.Collapsed;
        DetailColumn.Width = new GridLength(0);
        PresentSeriesDetails(series);
        SeriesDetailPageView.Visibility = Visibility.Visible;
        LibraryPage.Visibility = Visibility.Collapsed;
        SettingsPage.Visibility = Visibility.Collapsed;
        _playbackNavigation.SetMode(PlaybackPageMode.Detail);
    }

    private void SeriesDetailPage_BackRequested(object? sender, EventArgs e)
    {
        SeriesDetailPageView.ClearSeries();
        SeriesDetailPageView.Visibility = Visibility.Collapsed;
        ShowPage(showSettings: false);
        _playbackNavigation.SetMode(PlaybackPageMode.Library);
    }

    private async void SeriesDetailPage_PlayRequested(object? sender, LibraryEpisodeEventArgs e) =>
        await PlayEpisodeAsync(e.Episode);

    private void SeriesDetailPage_MetadataRequested(object? sender, UriEventArgs e) => OpenExternalUri(e.Uri);

    private void CloseDetail_Click(object sender, RoutedEventArgs e)
    {
        DetailPanel.Visibility = Visibility.Collapsed;
        DetailColumn.Width = new GridLength(0);
        DetailPanel.DataContext = null;
    }

    private void OpenMetadataLink_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: Uri uri }) return;
        OpenExternalUri(uri);
    }

    private void OpenExternalUri(Uri uri)
    {
        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            StatusText.Text = $"无法打开元数据页面：{error.Message}";
        }
    }

    private void ShowPlaybackPage(LibrarySeries? series, MpvPlaybackQueue? queue, LibraryEpisode episode)
    {
        SeriesDetailPageView.Visibility = Visibility.Collapsed;
        LibraryPage.Visibility = Visibility.Collapsed;
        SettingsPage.Visibility = Visibility.Collapsed;
        PlaybackPageView.SetEpisodeSidebarVisible(!_isFullscreen);
        PlaybackPageView.Prepare(series, queue, episode);
        PlaybackPageView.Visibility = Visibility.Visible;
        _playbackNavigation.SetMode(PlaybackPageMode.Playback);
    }

    private void HidePlaybackPage(bool showDetail)
    {
        if (_isFullscreen) SetFullscreen(false);
        var series = _activePlaybackSeries;
        PlaybackPageView.ClearSession();
        PlaybackPageView.Visibility = Visibility.Collapsed;
        if (showDetail && series is not null)
        {
            PresentSeriesDetails(series);
            SeriesDetailPageView.Visibility = Visibility.Visible;
            LibraryPage.Visibility = Visibility.Collapsed;
            SettingsPage.Visibility = Visibility.Collapsed;
            _playbackNavigation.SetMode(PlaybackPageMode.Detail);
        }
        else
        {
            ShowPage(showSettings: false);
            _playbackNavigation.SetMode(PlaybackPageMode.Library);
        }
        _activePlaybackSeries = null;
        _activePlaybackEpisode = null;
    }

    private async void PlayEpisode_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: LibraryEpisode episode }) await PlayEpisodeAsync(episode);
    }

    private async Task<bool> PlayEpisodeAsync(
        LibraryEpisode episode,
        long? startPositionMs = null,
        bool showErrorDialog = true,
        long? endPositionMs = null)
    {
        var launchGeneration = Interlocked.Increment(ref _playbackGeneration);
        await _playbackStartLock.WaitAsync();
        try
        {
            if (launchGeneration != Interlocked.Read(ref _playbackGeneration)) return false;
            if (_activeSession is { } previous)
            {
                _supersededSessions.Add(previous);
                previous.SubtitleTracksChanged -= ActiveSession_SubtitleTracksChanged;
                previous.AudioTracksChanged -= ActiveSession_AudioTracksChanged;
                previous.PlaybackInfoChanged -= ActiveSession_PlaybackInfoChanged;
                await previous.DisposeAsync();
                if (ReferenceEquals(_activeSession, previous)) _activeSession = null;
            }

            var source = episode.SourceId > 0 ? _mediaSourceRegistry.Get(episode.SourceId) : null;
            if (episode.ExternalAudioPaths.Count == 0)
            {
                var localSidecars = ExternalAudioPathMatcher.FindLocalSiblings(episode.MediaPath);
                if (localSidecars.Count > 0) episode = episode with { ExternalAudioPaths = localSidecars };
            }
            if (episode.ExternalAudioPaths.Count == 0 && source?.Type == "WEBDAV")
            {
                using var sidecarTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                var remoteSidecars = await _mediaSourceRegistry.DiscoverWebDavSidecarsAsync(
                    source.Id,
                    episode.MediaPath,
                    sidecarTimeout.Token);
                if (remoteSidecars.Count > 0) episode = episode with { ExternalAudioPaths = remoteSidecars };
            }
            var playbackProxy = source?.Type == "WEBDAV"
                ? _mediaSourceRegistry.CreatePlaybackProxy(source.Id, episode)
                : null;
            var series = _allSeries.FirstOrDefault(item => item.Episodes.Any(candidate => candidate.ProgressKey == episode.ProgressKey));
            _activePlaybackQueue = series is null ? null : new MpvPlaybackQueue(series.Episodes, episode.ProgressKey);
            _activePlaybackSeries = series;
            _activePlaybackEpisode = episode;
            IntPtr? windowHandle = null;
            if (MpvPlayerLauncher.FindLibMpv(_settings.LibMpvPath) is not null)
            {
                ShowPlaybackPage(series, _activePlaybackQueue, episode);
                UpdateLayout();
                PlaybackPageView.VideoHost.ValidateForMpv();
                windowHandle = PlaybackPageView.VideoHost.Handle;
            }
            var launch = await MpvPlayerLauncher.PlayDetailedAsync(
                episode,
                _settings,
                _progressStore,
                startPositionMs,
                playbackProxy: playbackProxy,
                windowHandle: windowHandle,
                videoOptions: new MpvWindowsVideoOptions(),
                allowDirectHttp: source?.Type == "RSS",
                endPositionMs: endPositionMs);
            var session = launch.Session;
            if (launchGeneration != Interlocked.Read(ref _playbackGeneration))
            {
                if (session is not null) await session.DisposeAsync();
                return false;
            }
            if (launch.UsedSystemPlayerFallback)
            {
                HidePlaybackPage(showDetail: series is not null);
                _activePlaybackQueue = null;
                _activePlaybackSeries = null;
                _activePlaybackEpisode = null;
            }
            _activeSession = session;
            _localLogs.Write("INFO", "Playback started", attributes: new Dictionary<string, string>
            {
                ["episodeId"] = episode.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["sourceId"] = episode.SourceId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["backend"] = launch.UsedSystemPlayerFallback ? "system" : "libmpv",
            });
            StatusText.Text = launch.UsedSystemPlayerFallback
                ? $"已交给 Windows 播放器：{episode.DisplayTitle}（能力受限）"
                : $"正在播放并记录进度：{episode.DisplayNumber} {episode.DisplayTitle}";
            if (session is not null)
            {
                session.SubtitleTracksChanged += ActiveSession_SubtitleTracksChanged;
                session.AudioTracksChanged += ActiveSession_AudioTracksChanged;
                session.PlaybackInfoChanged += ActiveSession_PlaybackInfoChanged;
                PlaybackPageView.UpdateSession(session);
                _ = RefreshAfterPlaybackAsync(session, episode, launchGeneration);
            }
            return true;
        }
        catch (Exception error) when (error is IOException or System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
        {
            _localLogs.Write("ERROR", "Playback failed", error, new Dictionary<string, string>
            {
                ["episodeId"] = episode.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["sourceId"] = episode.SourceId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            });
            if (_activePlaybackEpisode?.ProgressKey == episode.ProgressKey)
                HidePlaybackPage(showDetail: _activePlaybackSeries is not null);
            StatusText.Text = $"无法播放：{error.Message}";
            if (showErrorDialog)
            {
                MessageBox.Show(this, error.Message, "无法播放", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            return false;
        }
        finally
        {
            _playbackStartLock.Release();
        }
    }

    private async Task RefreshAfterPlaybackAsync(IPlaybackSession session, LibraryEpisode playedEpisode, long launchGeneration)
    {
        await session.Completion;
        await session.DisposeAsync();
        session.SubtitleTracksChanged -= ActiveSession_SubtitleTracksChanged;
        session.AudioTracksChanged -= ActiveSession_AudioTracksChanged;
        session.PlaybackInfoChanged -= ActiveSession_PlaybackInfoChanged;
        _localLogs.Write(session.WasCompleted ? "INFO" : "WARN", "Playback session ended", attributes: new Dictionary<string, string>
        {
            ["episodeId"] = playedEpisode.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["durationMs"] = session.DurationMs.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["completed"] = session.WasCompleted.ToString(),
        });
        var wasSuperseded = _supersededSessions.Remove(session);
        LibrarySeries? completedPlaybackSeries = null;
        if (ReferenceEquals(_activeSession, session))
        {
            if (!wasSuperseded) completedPlaybackSeries = _activePlaybackSeries;
            _activeSession = null;
            _activePlaybackQueue = null;
        }
        LibraryEpisode? nextEpisode = null;
        if (!wasSuperseded && session.WasCompleted && _settings.PlaybackEndAction == "play_next_episode")
        {
            var series = _allSeries.FirstOrDefault(item => item.Episodes.Any(episode => episode.ProgressKey == playedEpisode.ProgressKey));
            if (series is not null)
            {
                var queue = new MpvPlaybackQueue(series.Episodes, playedEpisode.ProgressKey);
                if (_settings.EpisodeVersionSelectionPolicy == "manual" && queue.NextEntry is { Versions.Count: > 1 } entry)
                {
                    var dialog = new EpisodeVersionSelectionWindow(entry.Versions) { Owner = this };
                    if (dialog.ShowDialog() == true && dialog.Version is { } selected && queue.Next() is not null)
                        nextEpisode = queue.SelectVersion(selected.MediaPath);
                }
                else
                {
                    nextEpisode = queue.Next();
                }
            }
        }

        string? syncStatus = null;
        if (!wasSuperseded)
        {
            try
            {
                if (await _bangumiPlaybackSync.MarkCompletedAsync(playedEpisode, session.WasCompleted) == BangumiPlaybackSyncResult.Updated)
                    syncStatus = "播放进度已保存，Bangumi 分集状态已同步";
            }
            catch (Exception error) when (error is HttpRequestException or InvalidDataException)
            {
                syncStatus = $"播放进度已保存；Bangumi 同步失败：{error.Message}";
            }
        }

        var selectedSeriesId = SeriesDetailPageView.CurrentSeries?.Id ?? (DetailPanel.DataContext as LibrarySeries)?.Id;
        _catalogSeries = ApplyProgress(_catalogSeries);
        _allSeries = LibraryOrganization.Apply(_catalogSeries, _settings);
        if (completedPlaybackSeries is not null)
            completedPlaybackSeries = _allSeries.FirstOrDefault(series => series.Id == completedPlaybackSeries.Id) ?? completedPlaybackSeries;
        UpdateContinueWatching();
        ApplySearchFilter();
        if (selectedSeriesId is not null)
        {
            var refreshedSeries = _allSeries.FirstOrDefault(series => series.Id == selectedSeriesId);
            DetailPanel.DataContext = refreshedSeries;
            if (refreshedSeries is not null && SeriesDetailPageView.CurrentSeries is not null)
                PresentSeriesDetails(refreshedSeries);
            if (completedPlaybackSeries?.Id == refreshedSeries?.Id)
                completedPlaybackSeries = refreshedSeries;
        }
        if (!wasSuperseded) StatusText.Text = session.LastError is { } playbackError ? $"播放失败：{playbackError}" : syncStatus ?? "播放进度已保存";
        if (nextEpisode is not null && launchGeneration == Interlocked.Read(ref _playbackGeneration))
        {
            await PlayEpisodeAsync(nextEpisode);
        }
        else if (completedPlaybackSeries is not null && _activeSession is null)
        {
            _activePlaybackSeries = completedPlaybackSeries;
            HidePlaybackPage(showDetail: true);
        }
    }

    private List<LibrarySeries> ApplyProgress(IReadOnlyList<LibrarySeries> series)
    {
        var progressByEpisode = _progressStore.GetAll();
        return series.Select(item => item with
        {
            Episodes = item.Episodes.Select(episode =>
            {
                var progress = progressByEpisode.GetValueOrDefault(episode.ProgressKey);
                return progress is null ? episode : episode with
                {
                    WatchedPositionMs = progress.PositionMs,
                    WatchedDurationMs = progress.DurationMs,
                    LastWatchedEpochMs = progress.LastWatchedEpochMs,
                    PlayCount = progress.PlayCount,
                };
            }).ToList(),
        }).ToList();
    }

    private void UpdateBrowseSections()
    {
        var recent = _filteredSeries
            .Where(series => series.LastWatchedEpochMs > 0)
            .OrderByDescending(series => series.LastWatchedEpochMs)
            .Take(8)
            .ToList();
        RecentList.ItemsSource = recent;
        RecentSection.Visibility = recent.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        var featured = _filteredSeries
            .OrderByDescending(series => series.Episodes.Count)
            .ThenByDescending(series => series.Year ?? 0)
            .ThenBy(series => series.Title, StringComparer.CurrentCultureIgnoreCase)
            .Take(6)
            .ToList();
        FeaturedList.ItemsSource = featured;
        FeaturedSection.Visibility = featured.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        var groups = _settings.PosterWallArrangement == "RELEASE_SEASON"
            ? _filteredSeries.GroupBy(LibraryOrganization.ReleaseSeasonTitle)
                .Select(group => new LibraryGenreGroup(group.Key, group.ToList())).ToList()
            : _filteredSeries
                .SelectMany(series => series.Genres.DefaultIfEmpty("未分类"), (series, genre) => new { series, genre })
                .GroupBy(item => item.genre, StringComparer.CurrentCultureIgnoreCase)
                .OrderBy(group => group.Key, StringComparer.CurrentCultureIgnoreCase)
                .Select(group => new LibraryGenreGroup(group.Key, group.Select(item => item.series).ToList()))
                .ToList();
        GenreGroupList.ItemsSource = groups;
        GenreGroupList.Visibility = groups.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateContinueWatching()
    {
        var items = _allSeries
            .SelectMany(series => series.Episodes
                .Where(episode => episode.IsInProgress)
                .Select(episode => new ContinueWatchingItem(series.Title, episode)))
            .OrderByDescending(item => item.Episode.LastWatchedEpochMs)
            .Take(4)
            .ToList();
        ContinueList.ItemsSource = items;
        ContinueSection.Visibility = items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        AllSeriesTitle.Visibility = items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    public async ValueTask DisposeAsync()
    {
        _localLogs.Write("INFO", "MiruPlay shutdown started");
        _posterCacheCancellation?.Cancel();
        _posterCacheCancellation?.Dispose();
        _scanCancellation?.Cancel();
        _scanCancellation?.Dispose();
        var session = _activeSession;
        _activeSession = null;
        if (session is not null)
        {
            _supersededSessions.Add(session);
            session.SubtitleTracksChanged -= ActiveSession_SubtitleTracksChanged;
            session.AudioTracksChanged -= ActiveSession_AudioTracksChanged;
            session.PlaybackInfoChanged -= ActiveSession_PlaybackInfoChanged;
            await session.DisposeAsync();
        }
        await _mediaSourceAutoScanScheduler.DisposeAsync();
        await _cloudDriveScheduler.DisposeAsync();
        await _openObserveLogScheduler.DisposeAsync();
        await _tasks.DisposeAsync();
        await _webControlServer.DisposeAsync();
        _bangumiArchive.Dispose();
        _bangumiMetadata.Dispose();
        _tmdbMetadata.Dispose();
        _tvMazeMetadata.Dispose();
        _appUpdater.Dispose();
        _mediaSourceRegistry.Dispose();
        GC.SuppressFinalize(this);
    }

    private Task<bool> HandleAppUpdateInstallAsync()
    {
        if (_shutdownStarted) return Task.FromResult(false);
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("无法确定当前程序路径。");
        _ = _appUpdater.StartInstallerHandoff(executable, Environment.ProcessId, _backgroundMode);
        _localLogs.Write("WARN", "Staged Windows installer handoff accepted");
        _ = Dispatcher.BeginInvoke(new Action(async () =>
        {
            await Task.Delay(750);
            RequestShutdown("exit");
        }), System.Windows.Threading.DispatcherPriority.Background);
        return Task.FromResult(true);
    }

    private Task<bool> HandleAppControlAsync(string action)
    {
        if (action is not ("restart" or "exit") || _shutdownStarted) return Task.FromResult(false);
        RequestShutdown(action);
        return Task.FromResult(true);
    }

    private void RequestShutdown(string action)
    {
        if (_shutdownStarted) return;
        _shutdownStarted = true;
        var restart = action == "restart";
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("无法确定当前程序路径。");
        _ = Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                _localLogs.Write("WARN", restart ? "Remote restart requested" : "Remote exit requested");
                await DisposeAsync();
                _shutdownComplete = true;
                ((App)Application.Current).ReleaseInstanceMutex();
                Close();
                if (restart)
                {
                    var start = new ProcessStartInfo(executable) { UseShellExecute = true };
                    if (_backgroundMode) start.ArgumentList.Add("--background");
                    Process.Start(start);
                }
            }
            catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                _localLogs.Write("ERROR", "Remote application control failed", error);
            }
        });
    }

    private async void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_shutdownComplete) return;
        e.Cancel = true;
        if (_shutdownStarted) return;
        _shutdownStarted = true;
        try
        {
            await DisposeAsync();
        }
        finally
        {
            _shutdownComplete = true;
            Close();
        }
    }

    private AudioDspPreset? FindAudioPreset(string id) =>
        _settings.AudioDsp?.Presets?.FirstOrDefault(preset => preset.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    private MpvNativeDiagnostics? GetPlaybackNativeDiagnostics()
    {
        var session = _activeSession;
        if (session is null || !session.IsActive) return null;
        try
        {
            return session.GetNativeDiagnostics();
        }
        catch (ObjectDisposedException)
        {
            return null;
        }
    }

    private PlaybackRuntimeStatus GetPlaybackRuntimeStatus()
    {
        var session = _activeSession;
        if (session is null)
        {
            return new PlaybackRuntimeStatus();
        }
        return new PlaybackRuntimeStatus(
            State: session.IsActive ? (session.IsPaused ? "PAUSED" : "PLAYING") : "ENDED",
            Uri: session.Episode.MediaPath,
            EpisodeId: session.Episode.ApiId,
            Title: $"{session.Episode.DisplayNumber} · {session.Episode.DisplayTitle}",
            PositionMs: session.PositionMs,
            DurationMs: session.DurationMs,
            IsPlaying: session.IsPlaying,
            Error: session.LastError,
            SubtitleTracks: session.SubtitleTracks,
            SelectedSubtitleTrackId: session.SelectedSubtitleTrackId,
            AudioTracks: session.AudioTracks,
            SelectedAudioTrackId: session.SelectedAudioTrackId,
            Speed: session.Speed,
            VolumePercent: session.VolumePercent,
            IsMuted: session.IsMuted,
            CanPlayPrevious: _activePlaybackQueue?.CanPlayPrevious ?? false,
            CanPlayNext: _activePlaybackQueue?.CanPlayNext ?? false,
            NextEpisodeVersions: _settings.EpisodeVersionSelectionPolicy == "manual" && _activePlaybackQueue?.NextEntry is { Versions.Count: > 1 } entry
                ? entry.Versions.Select(version => new PlaybackVersionChoice(version.ApiId, version.VersionLabel)).ToArray()
                : []);
    }

    private async Task<bool> PlayQueueNextAsync(bool allowManualPrompt)
    {
        var queue = _activePlaybackQueue;
        var entry = queue?.NextEntry;
        if (queue is null || entry is null) return false;
        string? selectedPath = null;
        if (allowManualPrompt && _settings.EpisodeVersionSelectionPolicy == "manual" && entry.Versions.Count > 1)
        {
            var dialog = new EpisodeVersionSelectionWindow(entry.Versions) { Owner = this };
            if (dialog.ShowDialog() != true) return false;
            selectedPath = dialog.Version?.MediaPath;
        }
        var next = queue.Next();
        if (selectedPath is not null) next = queue.SelectVersion(selectedPath);
        return next is not null && await PlayEpisodeAsync(next, showErrorDialog: false);
    }

    private async Task<bool> PlayMusicFromWebAsync(WebControlServer.MusicPlayRequest request)
    {
        var source = _mediaSourceRegistry.Get(request.SourceId);
        if (source is null || source.Type != "LOCAL") return false;
        var root = Path.GetFullPath(source.Location);
        string full;
        try
        {
            full = Path.GetFullPath(request.File);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
        if (!full.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(full) ||
            !MusicLibraryService.IsAudioFile(full))
            return false;
        var relative = Path.GetRelativePath(root, full);
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes($"{request.SourceId}:{relative}:{request.StartMs}")))[..16];
        var title = string.IsNullOrWhiteSpace(request.Title) ? Path.GetFileNameWithoutExtension(full) ?? full : request.Title;
        var episode = new LibraryEpisode(
            0,
            $"music-{hash}",
            $"music:{request.SourceId}:{relative}:{request.StartMs}",
            1,
            Math.Max(1, request.Number),
            Math.Max(1, request.Number),
            title,
            full,
            request.EndMs > request.StartMs
                ? TimeSpan.FromMilliseconds(request.EndMs - request.StartMs)
                : TimeSpan.Zero,
            []) with
        {
            SourceId = request.SourceId,
        };
        return await Dispatcher.InvokeAsync(() => PlayEpisodeAsync(
            episode,
            request.StartMs > 0 ? request.StartMs : null,
            showErrorDialog: false,
            endPositionMs: request.EndMs > request.StartMs ? request.EndMs : null)).Task.Unwrap();
    }

    private async Task<bool> PlayEpisodeFromWebAsync(string episodeId, long? startPositionMs)
    {
        var episode = _allSeries.SelectMany(series => series.Episodes).FirstOrDefault(item => item.ApiId == episodeId);
        if (episode is null) return false;
        var started = await Dispatcher
            .InvokeAsync(() => PlayEpisodeAsync(episode, startPositionMs, showErrorDialog: false))
            .Task
            .Unwrap();
        if (!started) throw new InvalidOperationException("播放启动失败；请检查播放器路径和媒体地址。");
        return true;
    }

    private Task<PlaybackRuntimeStatus> ExecutePlaybackCommandFromWebAsync(PlaybackControlCommand command) =>
        Dispatcher.InvokeAsync(async () =>
        {
            var session = _activeSession ?? throw new InvalidOperationException("当前没有可控制的 mpv 播放会话。");
            var kind = command.Command.Trim().ToLowerInvariant();
            if (kind is "previous" or "next")
            {
                if (kind == "next")
                {
                    var queue = _activePlaybackQueue;
                    if (_settings.EpisodeVersionSelectionPolicy == "manual" && queue?.NextEntry is { Versions.Count: > 1 } entry)
                    {
                        if (command.VersionEpisodeId is null) return GetPlaybackRuntimeStatus();
                        var selected = entry.Versions.FirstOrDefault(version => version.ApiId == command.VersionEpisodeId)
                            ?? throw new InvalidOperationException("所选版本已不在下一集列表中。");
                        if (queue.Next() is null || queue.SelectVersion(selected.MediaPath) is not { } next ||
                            !await PlayEpisodeAsync(next, showErrorDialog: false))
                            throw new InvalidOperationException("下一集版本播放启动失败。");
                    }
                    else
                    {
                        if (command.VersionEpisodeId is not null) throw new InvalidOperationException("当前下一集不需要版本选择。");
                        if (!await PlayQueueNextAsync(allowManualPrompt: false)) throw new InvalidOperationException("当前没有可播放的下一集。");
                    }
                }
                else
                {
                    var previous = _activePlaybackQueue?.Previous();
                    if (previous is null) throw new InvalidOperationException("当前没有上一集。");
                    if (!await PlayEpisodeAsync(previous, showErrorDialog: false)) throw new InvalidOperationException(StatusText.Text);
                }
            }
            else if (kind == "stop") await LeavePlaybackAsync();
            else
            {
                await session.ExecuteCommandAsync(command);
                if (ReferenceEquals(_activeSession, session)) PlaybackPageView.UpdateSession(session);
            }
            return GetPlaybackRuntimeStatus();
        }).Task.Unwrap();

    private void ActiveSession_SubtitleTracksChanged(object? sender, EventArgs e)
    {
        if (sender is not IPlaybackSession session) return;
        _ = Dispatcher.InvokeAsync(() =>
        {
            if (ReferenceEquals(_activeSession, session)) PlaybackPageView.UpdateSession(session);
        });
    }

    private void ActiveSession_AudioTracksChanged(object? sender, EventArgs e)
    {
        if (sender is not IPlaybackSession session) return;
        _ = Dispatcher.InvokeAsync(() =>
        {
            if (ReferenceEquals(_activeSession, session)) PlaybackPageView.UpdateSession(session);
        });
    }

    private void ActiveSession_PlaybackInfoChanged(object? sender, EventArgs e)
    {
        if (sender is not IPlaybackSession session) return;
        _ = Dispatcher.InvokeAsync(() =>
        {
            if (!ReferenceEquals(_activeSession, session)) return;
            PlaybackPageView.UpdateSession(session);
        });
    }

    private async void PlayExtra_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: LibraryExtra extra }) return;
        var sourceId = (DetailPanel.DataContext as LibrarySeries)?.Episodes is { Count: > 0 } episodes ? episodes[0].SourceId : 0;
        var episode = new LibraryEpisode(
            extra.Id,
            $"extra-{extra.Id}",
            $"extra:{sourceId}:{extra.Id}",
            1,
            extra.Ordinal,
            extra.SortOrder,
            extra.Title,
            extra.MediaPath,
            extra.Duration,
            [])
        {
            SourceId = sourceId,
        };
        await PlayEpisodeAsync(episode);
    }

    private async void PlaybackPage_ActionRequested(object? sender, PlaybackPageActionEventArgs e)
    {
        try
        {
            switch (e.Action)
            {
                case PlaybackBackAction or PlaybackStopAction:
                    await LeavePlaybackAsync();
                    break;
                case PlaybackToggleFullscreenAction:
                    SetFullscreen(!_isFullscreen);
                    break;
                case PlaybackExitFullscreenAction:
                    await ExecutePlaybackInputAsync(new(PlaybackInputAction.ExitFullscreen));
                    break;
                case PlaybackPreviousAction:
                    if (_activePlaybackQueue?.Previous() is { } previous) await PlayEpisodeAsync(previous, showErrorDialog: false);
                    break;
                case PlaybackNextAction:
                    await PlayQueueNextAsync(allowManualPrompt: true);
                    break;
                case PlaybackSelectEpisodeAction select:
                    await PlayEpisodeAsync(select.Episode, showErrorDialog: false);
                    break;
                case PlaybackRetryAction:
                    if (_activePlaybackEpisode is { } episode) await PlayEpisodeAsync(episode, showErrorDialog: false);
                    break;
                default:
                    if (_activeSession is not { } session) return;
                    switch (e.Action)
                    {
                        case PlaybackToggleAction: await session.TogglePauseAsync(); break;
                        case PlaybackSeekAction seek when seek.IsAbsolute: await session.SeekAsync(seek.ValueMs); break;
                        case PlaybackSeekAction seek: await session.SeekRelativeAsync(seek.ValueMs); break;
                        case PlaybackSelectAudioTrackAction audio: await session.SetAudioTrackAsync(audio.TrackId); break;
                        case PlaybackSelectSubtitleTrackAction subtitle: await session.SetSubtitleTrackAsync(subtitle.TrackId); break;
                        case PlaybackSetSpeedAction speed: await session.SetSpeedAsync(speed.Speed); break;
                        case PlaybackSetVolumeAction volume: await session.SetVolumeAsync(volume.VolumePercent); break;
                        case PlaybackSetMutedAction mute: await session.SetMutedAsync(mute.IsMuted); break;
                    }
                    if (ReferenceEquals(_activeSession, session)) PlaybackPageView.UpdateSession(session);
                    break;
            }
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or ArgumentException or TimeoutException)
        {
            PlaybackPageView.ShowCommandError(error.Message);
            StatusText.Text = $"播放控制失败：{error.Message}";
        }
    }

    private async Task ExecutePlaybackInputAsync(PlaybackInputCommand command)
    {
        if (command.Action == PlaybackInputAction.ToggleFullscreen)
        {
            SetFullscreen(!_isFullscreen);
            return;
        }

        if (command.Action == PlaybackInputAction.ExitFullscreen)
        {
            if (_isFullscreen) SetFullscreen(false);
            else await LeavePlaybackAsync();
            return;
        }

        if (_activeSession is not { } session) return;
        try
        {
            switch (command.Action)
            {
                case PlaybackInputAction.TogglePlayback:
                    await session.TogglePauseAsync();
                    break;
                case PlaybackInputAction.SeekRelative:
                    await session.SeekRelativeAsync(command.DeltaMs);
                    break;
                case PlaybackInputAction.ToggleMute:
                    await session.ExecuteCommandAsync(new MpvPlaybackCommand("cycle_mute"));
                    break;
                case PlaybackInputAction.VolumeUp:
                    await session.ExecuteCommandAsync(new MpvPlaybackCommand("volume", DeltaMs: 5));
                    break;
                case PlaybackInputAction.VolumeDown:
                    await session.ExecuteCommandAsync(new MpvPlaybackCommand("volume", DeltaMs: -5));
                    break;
            }
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or ArgumentOutOfRangeException or TimeoutException)
        {
            StatusText.Text = $"播放控制失败：{error.Message}";
        }
    }

    private async Task LeavePlaybackAsync()
    {
        Interlocked.Increment(ref _playbackGeneration);
        var session = _activeSession;
        if (session is not null)
        {
            _supersededSessions.Add(session);
            session.SubtitleTracksChanged -= ActiveSession_SubtitleTracksChanged;
            session.AudioTracksChanged -= ActiveSession_AudioTracksChanged;
            session.PlaybackInfoChanged -= ActiveSession_PlaybackInfoChanged;
            await session.DisposeAsync();
            if (ReferenceEquals(_activeSession, session)) _activeSession = null;
        }
        _activePlaybackQueue = null;
        HidePlaybackPage(showDetail: _activePlaybackSeries is not null);
    }

    private void SetFullscreen(bool enabled)
    {
        if (_isFullscreen == enabled) return;
        if (enabled)
        {
            _savedWindowStyle = WindowStyle;
            _savedWindowState = WindowState;
            _savedResizeMode = ResizeMode;
            _savedNavigationWidth = NavigationColumn.Width;
            _savedPlaybackSidebarVisible = true;
            _isFullscreen = true;
            _playbackNavigation.SetFullscreen(true);
            NavigationColumn.Width = new GridLength(0);
            PlaybackPageView.SetFullscreen(true);
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Maximized;
        }
        else
        {
            PlaybackPageView.SetFullscreen(false);
            WindowState = WindowState.Normal;
            WindowStyle = _savedWindowStyle;
            ResizeMode = _savedResizeMode;
            NavigationColumn.Width = _savedNavigationWidth;
            PlaybackPageView.SetEpisodeSidebarVisible(_savedPlaybackSidebarVisible);
            WindowState = _savedWindowState;
            _isFullscreen = false;
            _playbackNavigation.SetFullscreen(false);
        }
    }

    private async Task<MediaSourceInfoDto> AddSourceFromWebAsync(MediaSourceRequest request)
    {
        var source = await _mediaSourceRegistry.AddAsync(request);
        await Dispatcher.InvokeAsync(() => LoadSourceAsync(_mediaSourceRegistry.Get(source.Id)!)).Task.Unwrap();
        return source;
    }

    private async Task<MediaSourceInfoDto> UpdateSourceFromWebAsync(long sourceId, MediaSourceRequest request)
    {
        var source = await _mediaSourceRegistry.UpdateAsync(sourceId, request);
        if (_settings.ActiveSourceId == sourceId)
        {
            await Dispatcher.InvokeAsync(() => LoadSourceAsync(_mediaSourceRegistry.Get(sourceId)!)).Task.Unwrap();
        }
        return source;
    }

    private async Task RemoveSourceFromWebAsync(long sourceId)
    {
        var removedLoadedSource = _loadedSourceId == sourceId;
        if (removedLoadedSource) _posterCacheCancellation?.Cancel();
        _mediaSourceRegistry.Remove(sourceId);
        if (!removedLoadedSource) return;
        await Dispatcher.InvokeAsync(async () =>
        {
            if (_activePlaybackEpisode?.SourceId == sourceId) await LeavePlaybackAsync();
            ShowPage(SettingsPage.Visibility == Visibility.Visible);
            var active = _settings.ActiveSourceId is long activeId ? _mediaSourceRegistry.Get(activeId) : null;
            if (active is null) ClearLibraryView();
            else await LoadSourceAsync(active);
        }).Task.Unwrap();
    }

    private async Task<CloudDriveIngestionSummary> RescanLinkedWebDavAsync(long sourceId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var source = _mediaSourceRegistry.Get(sourceId);
        if (source?.Type != "WEBDAV") throw new InvalidOperationException("CloudDrive/RSS 回扫来源必须是已配置的 WebDAV 来源。");
        var result = await ScanSourceFromWebAsync(sourceId, cancellationToken);
        return ToCloudDriveIngestionSummary(result);
    }

    internal static CloudDriveIngestionSummary ToCloudDriveIngestionSummary(SourceScanResponse result)
    {
        if (result.Error is not null) throw new InvalidDataException($"WebDAV 回扫失败：{result.Error}");
        return new CloudDriveIngestionSummary(Indexed: result.EpisodesFound);
    }

    private async Task<SourceScanResponse> ScanSourceFromWebAsync(long sourceId, CancellationToken cancellationToken = default)
    {
        var result = await _mediaSourceRegistry.ScanAsync(sourceId, cancellationToken).ConfigureAwait(false);
        _localLogs.Write(result.Error is null ? "INFO" : "ERROR",
            result.Error is null ? "WebControl media source scan completed" : "WebControl media source scan failed",
            attributes: new Dictionary<string, string>
            {
                ["sourceId"] = sourceId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["episodes"] = result.EpisodesFound.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["newEpisodes"] = result.NewEpisodes.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["error"] = result.Error is null ? string.Empty : RotatingLocalLogStore.Redact(result.Error),
            });
        var source = _mediaSourceRegistry.Get(sourceId);
        if (result.Error is null && source is not null && _settings.ActiveSourceId == sourceId)
        {
            await Dispatcher.InvokeAsync(() => LoadSourceAsync(source)).Task.Unwrap();
        }
        return result;
    }

    private async void ActivateSource_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: MediaSourceDefinition source }) return;
        await LoadSourceAsync(source);
    }

    private async void ScanSource_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: MediaSourceDefinition source }) return;
        try
        {
            var result = await ScanSourceForUiAsync(source.Id);
            StatusText.Text = result.Error is null
                ? $"已扫描 {source.Name}：{result.EpisodesFound} 集，新增 {result.NewEpisodes}，删除 {result.DeletedEpisodes}"
                : result.Error;
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "扫描已取消";
        }
    }

    private async Task<SourceScanResponse> ScanSourceForUiAsync(long sourceId)
    {
        _scanCancellation?.Cancel();
        _scanCancellation?.Dispose();
        var scanCancellation = new CancellationTokenSource();
        _scanCancellation = scanCancellation;
        var cancellationToken = scanCancellation.Token;
        var progress = new Progress<DirectoryScanProgress>(value =>
        {
            BusyProgressBar.IsIndeterminate = false;
            BusyProgressText.Text = value.FilesDiscovered > 0
                ? $"已处理 {value.FilesProcessed}/{value.FilesDiscovered} 个文件 · {value.EpisodesFound} 集"
                : "正在发现媒体文件…";
        });
        BusyOverlay.Visibility = Visibility.Visible;
        BusyOperationText.Text = "正在扫描媒体源…";
        BusyProgressText.Text = "正在发现媒体文件…";
        BusyProgressBar.IsIndeterminate = true;
        CancelScanButton.Visibility = Visibility.Visible;
        try
        {
            SourceScanResponse? result = null;
            var started = await _tasks.RunTrackedAsync($"media-source-scan-{sourceId}", $"媒体源扫描 {sourceId}", async token =>
            {
                result = await _mediaSourceRegistry.ScanAsync(sourceId, progress, token).ConfigureAwait(true);
                if (result.Error is not null) throw new InvalidOperationException(result.Error);
            }, cancellationToken);
            if (result is null) return new SourceScanResponse(sourceId, string.Empty, 0, 0, 0, $"任务 media-source-scan-{sourceId} 已在运行。");
            _localLogs.Write(started ? "INFO" : "ERROR", started ? "Media source scan completed" : "Media source scan failed",
                attributes: new Dictionary<string, string>
                {
                    ["sourceId"] = sourceId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["episodes"] = result.EpisodesFound.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["newEpisodes"] = result.NewEpisodes.ToString(System.Globalization.CultureInfo.InvariantCulture),
                });
            var source = _mediaSourceRegistry.Get(sourceId);
            if (result.Error is null && source is not null && _settings.ActiveSourceId == sourceId)
                await LoadSourceAsync(source);
            return result;
        }
        finally
        {
            CancelScanButton.Visibility = Visibility.Collapsed;
            BusyProgressBar.IsIndeterminate = true;
            BusyProgressText.Text = string.Empty;
            if (ReferenceEquals(_scanCancellation, scanCancellation))
            {
                _scanCancellation = null;
            }
            scanCancellation.Dispose();
            BusyOverlay.Visibility = Visibility.Collapsed;
        }
    }

    private void CancelScan_Click(object sender, RoutedEventArgs e) => _scanCancellation?.Cancel();

    private async void EditSource_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: MediaSourceDefinition source }) return;
        MediaSourceRequest? request = null;
        if (source.Type == "LOCAL")
        {
            var dialog = new LocalSourceDialog(source.Name, source.Location, source.RecognitionMode) { Owner = this };
            if (dialog.ShowDialog() != true) return;
            request = new MediaSourceRequest(
                dialog.SourceName,
                "LOCAL",
                dialog.SourceLocation,
                ContentMode: source.ContentMode,
                RecognitionMode: dialog.RecognitionMode);
        }
        else if (source.Type == "RSS")
        {
            var dialog = new RssMediaSourceDialog(source.Name, source.Location) { Owner = this };
            if (dialog.ShowDialog() != true) return;
            request = new MediaSourceRequest(dialog.SourceName, "RSS", dialog.SourceLocation, ContentMode: source.ContentMode, RecognitionMode: "RSS");
        }
        else if (source.Type == "WEBDAV")
        {
            var dialog = new WebDavSourceDialog(source.Name, source.Location, recognitionMode: source.RecognitionMode) { Owner = this };
            if (dialog.ShowDialog() != true) return;
            var password = dialog.TakePassword();
            request = new MediaSourceRequest(
                dialog.SourceName,
                "WEBDAV",
                dialog.SourceLocation,
                Username: dialog.Username,
                Password: password,
                ContentMode: source.ContentMode,
                RecognitionMode: dialog.RecognitionMode);
        }
        else if (source.Type == "SMB")
        {
            var dialog = new SmbSourceDialog(source.Name, source.Location, recognitionMode: source.RecognitionMode) { Owner = this };
            if (dialog.ShowDialog() != true) return;
            var password = dialog.TakePassword();
            request = new MediaSourceRequest(
                dialog.SourceName,
                "SMB",
                dialog.SourceLocation,
                Username: dialog.Username,
                Password: password,
                Domain: dialog.Domain,
                ContentMode: source.ContentMode,
                RecognitionMode: dialog.RecognitionMode);
        }
        if (request is null) return;

        BusyOverlay.Visibility = Visibility.Visible;
        try
        {
            await _mediaSourceRegistry.UpdateAsync(source.Id, request);
            var updated = _mediaSourceRegistry.Get(source.Id)!;
            ApplySettingsToView();
            if (_settings.ActiveSourceId == source.Id) await LoadSourceAsync(updated);
            StatusText.Text = $"已更新媒体源：{updated.Name}";
        }
        catch (Exception error) when (error is ArgumentException or HttpRequestException or IOException or InvalidDataException or InvalidOperationException or NotSupportedException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, error.Message, "无法更新媒体源", MessageBoxButton.OK, MessageBoxImage.Warning);
            StatusText.Text = error.Message;
        }
        finally
        {
            BusyOverlay.Visibility = Visibility.Collapsed;
        }
    }

    private async void RemoveSource_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: MediaSourceDefinition source }) return;
        var confirmation = MessageBox.Show(
            this,
            $"删除媒体源“{source.Name}”？不会删除媒体文件或 library.db。",
            "删除媒体源",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirmation != MessageBoxResult.Yes) return;
        await RemoveSourceFromWebAsync(source.Id);
        StatusText.Text = $"已删除媒体源：{source.Name}";
    }

    private void ClearLibraryView()
    {
        ShowPage(SettingsPage.Visibility == Visibility.Visible);
        _libraryGeneration++;
        _posterCacheCancellation?.Cancel();
        _posterCacheCancellation?.Dispose();
        _posterCacheCancellation = null;
        _loadedSourceId = null;
        _requestedPosterUrls.Clear();
        _catalogSeries = [];
        _allSeries = [];
        UpdateContinueWatching();
        ShowSeries(_allSeries);
        LibraryRootValue.Text = "尚未设置";
        LibrarySchemaText.Text = "MLIP v1-v4";
        LibrarySummaryText.Text = "选择一个媒体源以开始";
        StatusText.Text = "尚未选择媒体源";
    }

    private static bool SamePath(string? first, string? second) =>
        first is not null && second is not null &&
        Path.GetFullPath(first).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Equals(
                Path.GetFullPath(second).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);

    private string CurrentContentMode => _settings.CurrentAppMode == "drama" ? "DRAMA" : "ANIME";

    private async Task ApplyAppModeAsync(string mode)
    {
        var normalized = mode.Equals("drama", StringComparison.OrdinalIgnoreCase) ? "drama" : "anime";
        _settings = _settings with { CurrentAppMode = normalized };
        _settingsStore.Save(_settings);
        ApplySettingsToView();
        var contentMode = normalized == "drama" ? "DRAMA" : "ANIME";
        var source = (_settings.MediaSources ?? []).FirstOrDefault(item =>
            item.ContentMode.Equals(contentMode, StringComparison.Ordinal) && item.Id == _settings.ActiveSourceId)
            ?? (_settings.MediaSources ?? []).FirstOrDefault(item => item.ContentMode.Equals(contentMode, StringComparison.Ordinal));
        if (source is null)
        {
            ClearLibraryView();
            StatusText.Text = normalized == "drama" ? "电视剧模式尚未配置媒体源" : "动漫模式尚未配置媒体源";
            return;
        }
        if (_loadedSourceId != source.Id) await LoadSourceAsync(source);
    }

    private async void AppMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingSettings || sender is not ComboBox { SelectedItem: ComboBoxItem { Tag: string mode } }) return;
        await ApplyAppModeAsync(mode);
    }

    private AppSettings UpdateSettingsFromMediaSource(Func<AppSettings, AppSettings> update) =>
        Dispatcher.Invoke(() =>
        {
            _settings = update(_settings);
            _settingsStore.Save(_settings);
            ApplySettingsToView();
            return _settings;
        });

    private Task<AppSettings> UpdateSettingsFromWebControlAsync(Func<AppSettings, AppSettings> update) =>
        Dispatcher.InvokeAsync(async () =>
        {
            var previous = _settings;
            _settings = update(_settings);
            _settingsStore.Save(_settings);
            ApplySettingsToView();
            if (previous.AppUpdateManifestUrl != _settings.AppUpdateManifestUrl)
                _appUpdater.ConfigureManifest(_settings.AppUpdateManifestUrl);
            if (previous.CurrentAppMode != _settings.CurrentAppMode) await ApplyAppModeAsync(_settings.CurrentAppMode);
            else if (previous.MergeSameAnimeEnabled != _settings.MergeSameAnimeEnabled ||
                previous.PosterWallArrangement != _settings.PosterWallArrangement)
                RefreshLibraryOrganization();
            return _settings;
        }).Task.Unwrap();

    private async void LibraryNav_Click(object sender, RoutedEventArgs e)
    {
        if (_activePlaybackEpisode is not null) await LeavePlaybackAsync();
        ShowPage(showSettings: false);
    }

    private async void SettingsNav_Click(object sender, RoutedEventArgs e)
    {
        if (_activePlaybackEpisode is not null) await LeavePlaybackAsync();
        ShowPage(showSettings: true);
    }

    private async void OpenAudioDsp_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new AudioDspDialog(_settings.AudioDsp ?? AudioDspConfig.Neutral(), _topping)
        {
            Owner = this,
        };
        if (dialog.ShowDialog() != true || dialog.Result is not { } config) return;

        try
        {
            await ApplyAudioDspConfigAsync(config);
            _settings = _settings with { AudioDsp = config };
            _settingsStore.Save(_settings);
            ApplySettingsToView();
            StatusText.Text = "音频 DSP 已应用到当前 MiruPlay mpv，并已保存。";
        }
        catch (Exception error) when (error is ArgumentException or IOException or InvalidOperationException or TimeoutException)
        {
            StatusText.Text = $"音频 DSP 应用失败：{error.Message}";
        }
    }

    private void ShowPage(bool showSettings)
    {
        if (_isFullscreen) SetFullscreen(false);
        SeriesDetailPageView.ClearSeries();
        SeriesDetailPageView.Visibility = Visibility.Collapsed;
        PlaybackPageView.Visibility = Visibility.Collapsed;
        CloseDetail_Click(this, new RoutedEventArgs());
        _playbackNavigation.SetMode(showSettings ? PlaybackPageMode.Settings : PlaybackPageMode.Library);
        LibraryPage.Visibility = showSettings ? Visibility.Collapsed : Visibility.Visible;
        SettingsPage.Visibility = showSettings ? Visibility.Visible : Visibility.Collapsed;
        LibraryNavButton.Tag = showSettings ? null : "Selected";
        SettingsNavButton.Tag = showSettings ? "Selected" : null;
    }

    private async void WebControlEnabled_Click(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings) return;
        var previous = _settings.WebControlEnabled;
        var enabled = WebControlEnabledCheckBox.IsChecked == true;
        try
        {
            if (enabled) await _webControlServer.StartAsync();
            else await _webControlServer.DisposeAsync();
            _settings = _settings with { WebControlEnabled = enabled };
            _settingsStore.Save(_settings);
            StatusText.Text = enabled ? "WebControl 已启动" : "WebControl 已停止";
        }
        catch (IOException error)
        {
            _settings = _settings with { WebControlEnabled = previous };
            WebControlEnabledCheckBox.IsChecked = previous;
            StatusText.Text = $"WebControl 启动失败：{error.Message}";
        }
        UpdateWebControlView();
    }

    private void SaveBangumiToken_Click(object sender, RoutedEventArgs e)
    {
        var token = BangumiTokenBox.Password;
        BangumiTokenBox.Clear();
        try
        {
            _metadataTokens.SaveBangumi(token);
            BangumiTokenStatusText.Text = "Bangumi Token 已保存在加密存储中。";
        }
        catch (ArgumentException error)
        {
            BangumiTokenStatusText.Text = error.Message;
        }
        finally
        {
            token = string.Empty;
        }
    }

    private void ClearBangumiToken_Click(object sender, RoutedEventArgs e)
    {
        BangumiTokenBox.Clear();
        _metadataTokens.ClearBangumi();
        BangumiTokenStatusText.Text = "当前未设置 Bangumi Token。";
    }

    private void SaveTmdbToken_Click(object sender, RoutedEventArgs e)
    {
        var token = TmdbTokenBox.Password;
        TmdbTokenBox.Clear();
        try
        {
            _metadataTokens.SaveTmdb(token);
            TmdbTokenStatusText.Text = "TMDB Token 已保存在加密存储中。";
        }
        catch (ArgumentException error)
        {
            TmdbTokenStatusText.Text = error.Message;
        }
        finally
        {
            token = string.Empty;
        }
    }

    private void ClearTmdbToken_Click(object sender, RoutedEventArgs e)
    {
        TmdbTokenBox.Clear();
        _metadataTokens.ClearTmdb();
        TmdbTokenStatusText.Text = "当前未设置 TMDB Token。";
    }

    private void SaveCloudDriveConfig_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var current = _cloudDriveConfig.Load();
            var mode = CloudDriveLibraryModeBox.SelectedItem is ComboBoxItem { Tag: "SINGLE_DIRECTORY" }
                ? CloudDriveLibraryMode.SingleDirectory
                : CloudDriveLibraryMode.OrganizedLibrary;
            var interval = int.TryParse(CloudDriveIntervalBox.Text.Trim(), out var parsed) ? parsed : 30;
            var saved = _cloudDriveConfig.Save(current with
            {
                EndpointUrl = CloudDriveEndpointBox.Text,
                Username = CloudDriveUsernameBox.Text,
                InboxPath = CloudDriveInboxBox.Text,
                LibraryPath = CloudDriveLibraryBox.Text,
                LibraryMode = mode,
                IntervalMinutes = interval,
                Enabled = CloudDriveEnabledCheckBox.IsChecked == true,
            });
            ApplyCloudDriveConfig(saved);
            CloudDriveStatusText.Text = "CloudDrive/RSS 自动化设置已保存。";
        }
        catch (Exception error) when (error is ArgumentException or InvalidDataException)
        {
            CloudDriveStatusText.Text = error.Message;
        }
    }

    private async void RunCloudDriveNow_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            CloudDriveStatusText.Text = "正在执行 CloudDrive/RSS 同步...";
            var status = await _cloudDriveRunner.RunAsync();
            var summary = status.Summary!;
            CloudDriveStatusText.Text = $"同步完成：提交 {summary.Submitted}，跳过 {summary.Skipped}，失败 {summary.Failed}。";
            RefreshRssSubscriptions();
        }
        catch (Exception error) when (error is ArgumentException or HttpRequestException or InvalidOperationException or InvalidDataException)
        {
            CloudDriveStatusText.Text = error.Message;
        }
    }

    private async void LoginCloudDrive_Click(object sender, RoutedEventArgs e)
    {
        var password = CloudDrivePasswordBox.Password;
        try
        {
            CloudDriveStatusText.Text = "正在登录 CloudDrive2...";
            var login = await _cloudDriveClient.LoginAsync(CloudDriveEndpointBox.Text, CloudDriveUsernameBox.Text, password);
            _ = await _cloudDriveClient.GetApiTokenInfoAsync(CloudDriveEndpointBox.Text, login.Token);
            _cloudDriveCredentials.SavePassword(CloudDriveEndpointBox.Text, password);
            _cloudDriveCredentials.SaveToken(CloudDriveEndpointBox.Text, login.Token);
            CloudDrivePasswordBox.Clear();
            UpdateCloudDriveCredentialStatus();
        }
        catch (Exception error) when (error is ArgumentException or HttpRequestException or InvalidOperationException)
        {
            CloudDriveStatusText.Text = error.Message;
        }
        finally
        {
            password = string.Empty;
        }
    }

    private async void VerifyCloudDriveToken_Click(object sender, RoutedEventArgs e)
    {
        var token = CloudDriveTokenBox.Password;
        try
        {
            CloudDriveStatusText.Text = "正在验证 CloudDrive2 API Token...";
            var info = await _cloudDriveClient.GetApiTokenInfoAsync(CloudDriveEndpointBox.Text, token);
            _cloudDriveCredentials.SaveToken(CloudDriveEndpointBox.Text, token);
            CloudDriveTokenBox.Clear();
            var label = string.IsNullOrWhiteSpace(info.FriendlyName) ? info.RootDir : info.FriendlyName;
            CloudDriveStatusText.Text = $"CloudDrive2 API Token 已验证并保存：{label}";
        }
        catch (Exception error) when (error is ArgumentException or HttpRequestException or InvalidOperationException)
        {
            CloudDriveStatusText.Text = error.Message;
        }
        finally
        {
            token = string.Empty;
        }
    }

    private void ClearCloudDriveCredentials_Click(object sender, RoutedEventArgs e)
    {
        CloudDrivePasswordBox.Clear();
        CloudDriveTokenBox.Clear();
        _cloudDriveCredentials.Clear();
        UpdateCloudDriveCredentialStatus();
    }

    private void UpdateCloudDriveCredentialStatus()
    {
        try
        {
            var credentials = _cloudDriveCredentials.LoadForEndpoint(CloudDriveEndpointBox.Text);
            CloudDriveStatusText.Text = (credentials.Token, credentials.Password) switch
            {
                (not null, not null) => "CloudDrive2 Token 和密码已保存在加密存储中。",
                (not null, null) => "CloudDrive2 Token 已保存在加密存储中。",
                (null, not null) => "CloudDrive2 密码已保存在加密存储中。",
                _ => "CloudDrive2 凭据尚未配置。",
            };
        }
        catch (InvalidOperationException)
        {
            CloudDriveStatusText.Text = "当前 CloudDrive2 服务地址尚无已验证凭据。";
        }
    }

    private void ApplyCloudDriveConfig(CloudDriveAutomationConfig config)
    {
        CloudDriveEndpointBox.Text = config.EndpointUrl;
        CloudDriveUsernameBox.Text = config.Username;
        CloudDriveInboxBox.Text = config.InboxPath;
        CloudDriveLibraryBox.Text = config.LibraryPath;
        CloudDriveIntervalBox.Text = config.IntervalMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture);
        CloudDriveEnabledCheckBox.IsChecked = config.Enabled;
        var modeTag = config.LibraryMode == CloudDriveLibraryMode.SingleDirectory ? "SINGLE_DIRECTORY" : "ORGANIZED_LIBRARY";
        CloudDriveLibraryModeBox.SelectedItem = CloudDriveLibraryModeBox.Items.OfType<ComboBoxItem>().First(item => Equals(item.Tag, modeTag));
    }

    private void SaveRssSubscription_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var request = new RssSubscriptionRequest(
                _editingRssId ?? 0,
                RssNameBox.Text,
                RssUrlBox.Text,
                RssFilterBox.Text,
                RssEnabledCheckBox.IsChecked == true);
            var saved = _editingRssId is long id
                ? _rssSubscriptions.Update(id, request)
                : _rssSubscriptions.Add(request);
            ClearRssForm();
            RefreshRssSubscriptions();
            RssStatusText.Text = $"RSS 订阅已保存：{saved.Name}";
        }
        catch (Exception error) when (error is ArgumentException or InvalidDataException or KeyNotFoundException)
        {
            RssStatusText.Text = error.Message;
        }
    }

    private async void PreviewRssSubscription_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: RssSubscriptionInfo subscription }) return;
        try
        {
            RssStatusText.Text = $"正在拉取：{subscription.Name}";
            var config = _cloudDriveConfig.Load();
            var items = await _rssFeedClient.FetchAsync(
                subscription.Url,
                config.RssProxyEnabled,
                config.RssProxyHost,
                config.RssProxyPort);
            var decisions = RssSubmissionPlanner.Plan(items, subscription.FilterRegex);
            var ready = decisions.Count(decision => decision.Status == RssSubmissionStatus.WouldSubmit);
            var processed = decisions.Count(decision => decision.ItemKey is not null && _rssProcessed.IsProcessed(subscription.Id, decision.ItemKey));
            RssStatusText.Text = $"预览完成：{decisions.Count} 项，可提交 {ready} 项，已处理 {processed} 项。";
        }
        catch (Exception error) when (error is ArgumentException or HttpRequestException or InvalidDataException or System.Xml.XmlException or TimeoutException)
        {
            RssStatusText.Text = error.Message;
        }
    }

    private void EditRssSubscription_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: RssSubscriptionInfo subscription }) return;
        _editingRssId = subscription.Id;
        RssNameBox.Text = subscription.Name;
        RssUrlBox.Text = subscription.Url;
        RssFilterBox.Text = subscription.FilterRegex ?? string.Empty;
        RssEnabledCheckBox.IsChecked = subscription.Enabled;
        RssStatusText.Text = $"正在编辑：{subscription.Name}";
    }

    private void DeleteRssSubscription_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: RssSubscriptionInfo subscription }) return;
        if (MessageBox.Show(this, $"删除 RSS 订阅“{subscription.Name}”？", "删除 RSS", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try
        {
            _rssSubscriptions.Remove(subscription.Id);
            if (_editingRssId == subscription.Id) ClearRssForm();
            RefreshRssSubscriptions();
            RssStatusText.Text = "RSS 订阅已删除。";
        }
        catch (KeyNotFoundException error)
        {
            RssStatusText.Text = error.Message;
        }
    }

    private void RefreshRssSubscriptions()
    {
        var values = _rssSubscriptions.List();
        RssSubscriptionList.ItemsSource = values;
        if (_editingRssId is null) RssStatusText.Text = values.Count == 0 ? "尚未配置 RSS 订阅。" : $"已加载 {values.Count} 个 RSS 订阅。";
    }

    private void ClearRssForm()
    {
        _editingRssId = null;
        RssNameBox.Clear();
        RssUrlBox.Clear();
        RssFilterBox.Clear();
        RssEnabledCheckBox.IsChecked = true;
    }

    private void RotateWebControlToken_Click(object sender, RoutedEventArgs e)
    {
        _webControlTokens.Rotate();
        UpdateWebControlView();
    }

    private void CopyWebControlUrl_Click(object sender, RoutedEventArgs e)
    {
        var url = _webControlServer.PreferredAccessUrl;
        Clipboard.SetText($"{url}?token={Uri.EscapeDataString(_webControlTokens.AccessToken)}");
        StatusText.Text = "WebControl 访问地址已复制";
    }

    private void UpdateWebControlView()
    {
        WebControlEnabledCheckBox.IsChecked = _settings.WebControlEnabled;
        WebControlUrlValue.Text = _webControlServer.PreferredAccessUrl;
        WebControlTokenValue.Text = _webControlTokens.AccessToken;
    }

    private async Task<SourceScanResponse> ScanSourceAutomaticallyAsync(long sourceId, CancellationToken cancellationToken)
    {
        var result = await ScanSourceTrackedAsync(sourceId, cancellationToken);
        if (result.Error is null && _loadedSourceId == sourceId)
        {
            await Dispatcher.InvokeAsync(async () =>
            {
                var source = _mediaSourceRegistry.Get(sourceId);
                if (source is not null) await LoadSourceAsync(source);
            }).Task.Unwrap();
        }
        return result;
    }

    private async Task<SourceScanResponse> ScanSourceTrackedAsync(long sourceId, CancellationToken cancellationToken)
    {
        SourceScanResponse? result = null;
        var id = $"media-source-scan-{sourceId}";
        try
        {
            var started = await _tasks.RunTrackedAsync(id, $"媒体源扫描 {sourceId}", async token =>
            {
                result = await _mediaSourceRegistry.ScanAsync(sourceId, token).ConfigureAwait(false);
                if (result.Error is not null) throw new InvalidOperationException(result.Error);
            }, cancellationToken).ConfigureAwait(false);
            if (result is null)
                return new SourceScanResponse(sourceId, string.Empty, 0, 0, 0, $"任务 {id} 已在运行。");
            _localLogs.Write(started ? "INFO" : "ERROR", started ? "Media source scan completed" : "Media source scan failed",
                attributes: new Dictionary<string, string>
                {
                    ["sourceId"] = sourceId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["episodes"] = result.EpisodesFound.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["newEpisodes"] = result.NewEpisodes.ToString(System.Globalization.CultureInfo.InvariantCulture),
                });
            return result;
        }
        catch (Exception error) when (error is OperationCanceledException or InvalidOperationException or IOException or UnauthorizedAccessException or HttpRequestException or InvalidDataException or NotSupportedException or ArgumentException)
        {
            _localLogs.Write(error is OperationCanceledException ? "WARN" : "ERROR", "Media source scan did not complete", error,
                new Dictionary<string, string> { ["sourceId"] = sourceId.ToString(System.Globalization.CultureInfo.InvariantCulture) });
            return result ?? new SourceScanResponse(sourceId, string.Empty, 0, 0, 0, error.Message);
        }
    }

    private async void AutoScanEnabled_Click(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings) return;
        _settings = _settings with { AutoScanEnabled = AutoScanEnabledCheckBox.IsChecked == true };
        _settingsStore.Save(_settings);
        ApplySettingsToView();
        if (_settings.AutoScanEnabled)
            await _mediaSourceAutoScanScheduler.RunIfDueAsync(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    private async void AutoScanInterval_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingSettings || AutoScanIntervalBox.SelectedItem is not ComboBoxItem { Tag: string value } ||
            !int.TryParse(value, out var hours)) return;
        _settings = _settings with { AutoScanIntervalHours = hours };
        _settingsStore.Save(_settings);
        ApplySettingsToView();
        if (_settings.AutoScanEnabled)
            await _mediaSourceAutoScanScheduler.RunIfDueAsync(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    private void LibraryOrganization_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings || PosterArrangementBox.SelectedItem is not ComboBoxItem { Tag: string arrangement }) return;
        _settings = _settings with
        {
            MergeSameAnimeEnabled = MergeSameAnimeCheckBox.IsChecked == true,
            PosterWallArrangement = arrangement,
        };
        _settingsStore.Save(_settings);
        RefreshLibraryOrganization();
    }

    private void RefreshLibraryOrganization()
    {
        _allSeries = LibraryOrganization.Apply(_catalogSeries, _settings);
        UpdateContinueWatching();
        ApplySearchFilter();
        LibrarySummaryText.Text = $"{_allSeries.Count} 部作品 · {_allSeries.Sum(item => item.Episodes.Count)} 集";
        if (SeriesDetailPageView.CurrentSeries is { } detail &&
            _allSeries.FirstOrDefault(item => item.ApiId == detail.ApiId || item.AlternateApiIds.Contains(detail.ApiId, StringComparer.Ordinal)) is { } refreshed)
            PresentSeriesDetails(refreshed);
        if (_activePlaybackEpisode is not { } episode) return;
        _activePlaybackSeries = _allSeries.FirstOrDefault(item => item.Episodes.Any(candidate => candidate.ProgressKey == episode.ProgressKey));
        _activePlaybackQueue = _activePlaybackSeries is null ? null : new MpvPlaybackQueue(_activePlaybackSeries.Episodes, episode.ProgressKey);
        PlaybackPageView.Prepare(_activePlaybackSeries, _activePlaybackQueue, episode);
        PlaybackPageView.SetFullscreen(_isFullscreen);
        if (_activeSession is not null) PlaybackPageView.UpdateSession(_activeSession);
    }

    private void EpisodeVersionSelectionPolicy_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingSettings || EpisodeVersionSelectionPolicyBox.SelectedItem is not ComboBoxItem { Tag: string policy }) return;
        _settings = _settings with { EpisodeVersionSelectionPolicy = policy };
        _settingsStore.Save(_settings);
    }

    private void PlaybackEndAction_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingSettings || PlaybackEndAction.SelectedItem is not ComboBoxItem { Tag: string value }) return;
        _settings = _settings with { PlaybackEndAction = value };
        _settingsStore.Save(_settings);
    }

    private void SubtitlePreference_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingSettings || SubtitlePreference.SelectedItem is not ComboBoxItem { Tag: string value }) return;
        _settings = _settings with { PreferredSubtitleLanguage = value };
        _settingsStore.Save(_settings);
    }

    private sealed record ContinueWatchingItem(string SeriesTitle, LibraryEpisode Episode)
    {
        public string AccessibleName => $"继续播放 {SeriesTitle} {Episode.DisplayNumber}";
    }

    private void ApplySettingsToView()
    {
        _loadingSettings = true;
        try
        {
            LibraryRootValue.Text = _settings.LibraryRoot ?? "尚未设置";
            SourceList.ItemsSource = _settings.MediaSources ?? [];
            AppModeSelector.SelectedItem = AppModeSelector.Items
                .OfType<ComboBoxItem>()
                .First(item => Equals(item.Tag, _settings.CurrentAppMode));
            SettingsAppMode.SelectedItem = SettingsAppMode.Items
                .OfType<ComboBoxItem>()
                .First(item => Equals(item.Tag, _settings.CurrentAppMode));
            var metadataTokens = _metadataTokens.Load();
            BangumiTokenStatusText.Text = string.IsNullOrEmpty(metadataTokens.Bangumi)
                ? "当前未设置 Bangumi Token。"
                : "Bangumi Token 已保存在加密存储中。";
            TmdbTokenStatusText.Text = string.IsNullOrEmpty(metadataTokens.Tmdb)
                ? "当前未设置 TMDB Token。"
                : "TMDB Token 已保存在加密存储中。";
            ApplyCloudDriveConfig(_cloudDriveConfig.Load());
            UpdateCloudDriveCredentialStatus();
            RefreshRssSubscriptions();
            UpdateWebControlView();
            var audioDsp = (_settings.AudioDsp ?? AudioDspConfig.Neutral()).Normalize();
            AudioDspEnabledCheckBox.IsChecked = audioDsp.Enabled;
            AudioDspSummaryText.Text = audioDsp.Enabled
                ? $"DSP 已启用：{audioDsp.SelectedPresetId}（{audioDsp.Presets!.Count} 个预设）"
                : "DSP 已关闭";
            MergeSameAnimeCheckBox.IsChecked = _settings.MergeSameAnimeEnabled;
            PosterArrangementBox.SelectedItem = PosterArrangementBox.Items.OfType<ComboBoxItem>()
                .First(item => Equals(item.Tag, _settings.PosterWallArrangement));
            EpisodeVersionSelectionPolicyBox.SelectedItem = EpisodeVersionSelectionPolicyBox.Items.OfType<ComboBoxItem>()
                .First(item => Equals(item.Tag, _settings.EpisodeVersionSelectionPolicy));
            AutoScanEnabledCheckBox.IsChecked = _settings.AutoScanEnabled;
            AutoScanIntervalBox.IsEnabled = _settings.AutoScanEnabled;
            AutoScanIntervalBox.SelectedItem = AutoScanIntervalBox.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(item => Equals(item.Tag, _settings.AutoScanIntervalHours.ToString(System.Globalization.CultureInfo.InvariantCulture)))
                ?? AutoScanIntervalBox.Items[1];
            AutoScanStatusText.Text = _settings.AutoScanEnabled
                ? $"已启用，每 {_settings.AutoScanIntervalHours} 小时检查一次。"
                : "自动扫描已关闭。";
            PlaybackEndAction.SelectedItem = PlaybackEndAction.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(item => Equals(item.Tag, _settings.PlaybackEndAction))
                ?? PlaybackEndAction.Items[0];
            SubtitlePreference.SelectedItem = SubtitlePreference.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(item => Equals(item.Tag, _settings.PreferredSubtitleLanguage))
                ?? SubtitlePreference.Items[0];
        }
        finally
        {
            _loadingSettings = false;
        }
    }
}
