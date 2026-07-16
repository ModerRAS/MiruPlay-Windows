using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using MiruPlay.Windows.Models;
using MiruPlay.Windows.Services;

namespace MiruPlay.Windows;

public partial class MainWindow : Window, IAsyncDisposable
{
    private readonly AppSettingsStore _settingsStore = new();
    private readonly PlaybackProgressStore _progressStore = new();
    private readonly WebControlTokenStore _webControlTokens = new();
    private readonly MetadataTokenStore _metadataTokens = new();
    private readonly BangumiMetadataClient _bangumiMetadata = new();
    private readonly BangumiPlaybackSyncService _bangumiPlaybackSync;
    private readonly RssSubscriptionStore _rssSubscriptions = new();
    private readonly CloudDriveAutomationStore _cloudDriveConfig = new();
    private readonly CloudDriveCredentialStore _cloudDriveCredentials = new();
    private readonly CloudDriveGrpcClient _cloudDriveClient = new();
    private readonly RssFeedClient _rssFeedClient = new();
    private readonly RssProcessedStore _rssProcessed = new();
    private readonly CloudDriveRssRunner _cloudDriveRunner;
    private readonly CloudDriveRssScheduler _cloudDriveScheduler;
    private readonly MediaSourceRegistry _mediaSourceRegistry;
    private readonly WebControlServer _webControlServer;
    private const int SeriesPageSize = 40;
    private AppSettings _settings = new();
    private List<LibrarySeries> _allSeries = [];
    private List<LibrarySeries> _filteredSeries = [];
    private readonly HashSet<string> _requestedPosterUrls = new(StringComparer.Ordinal);
    private readonly HashSet<MpvPlaybackSession> _supersededSessions = [];
    private readonly SemaphoreSlim _playbackStartLock = new(1, 1);
    private long _playbackGeneration;
    private int _visibleSeriesCount;
    private int _libraryGeneration;
    private long? _loadedSourceId;
    private CancellationTokenSource? _posterCacheCancellation;
    private bool _loadingSettings;
    private bool _loadingPlaybackTracks;
    private bool _shutdownStarted;
    private bool _shutdownComplete;
    private long? _editingRssId;
    private MpvPlaybackSession? _activeSession;

    public MainWindow()
    {
        InitializeComponent();
        _settings = _settingsStore.Load();
        _bangumiPlaybackSync = new BangumiPlaybackSyncService(
            _metadataTokens,
            _bangumiMetadata.UpdateEpisodeCollectionAsync);
        _mediaSourceRegistry = new MediaSourceRegistry(() => _settings, UpdateSettingsFromMediaSource);
        _cloudDriveRunner = new CloudDriveRssRunner(
            _cloudDriveConfig,
            _cloudDriveCredentials,
            _rssSubscriptions,
            _rssFeedClient,
            _rssProcessed,
            _cloudDriveClient,
            rescanWebDav: RescanLinkedWebDavAsync);
        _cloudDriveScheduler = new CloudDriveRssScheduler(_cloudDriveConfig, _cloudDriveRunner);
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
            metadataTokens: _metadataTokens,
            rssSubscriptions: _rssSubscriptions,
            cloudDriveConfig: _cloudDriveConfig,
            cloudDriveCredentials: _cloudDriveCredentials,
            cloudDriveClient: _cloudDriveClient,
            rssFeedClient: _rssFeedClient,
            rssProcessed: _rssProcessed,
            cloudDriveRunner: _cloudDriveRunner,
            resolvePosterPath: ResolvePosterForWebAsync);
        ApplySettingsToView();
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (_settings.WebControlEnabled)
        {
            try
            {
                await _webControlServer.StartAsync();
                StatusText.Text = $"WebControl 已启动：{_webControlServer.PreferredAccessUrl}";
            }
            catch (IOException error)
            {
                StatusText.Text = $"WebControl 启动失败：{error.Message}";
            }
        }
        UpdateWebControlView();
        _cloudDriveScheduler.Start();

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
        var dialog = new OpenFolderDialog
        {
            Title = "选择包含 library.db 的媒体库目录",
            InitialDirectory = Directory.Exists(_settings.LibraryRoot) ? _settings.LibraryRoot : null,
        };
        if (dialog.ShowDialog(this) != true) return;

        await AddOrActivateLocalSourceAsync(dialog.FolderName);
    }

    private async void AddWebDavSource_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new WebDavSourceDialog { Owner = this };
        if (dialog.ShowDialog() != true) return;

        var password = dialog.TakePassword();
        BusyOverlay.Visibility = Visibility.Visible;
        StatusText.Text = "正在连接 WebDAV 并验证 MLIP…";
        try
        {
            var source = await _mediaSourceRegistry.AddAsync(new MediaSourceRequest(
                dialog.SourceName,
                "WEBDAV",
                dialog.SourceLocation,
                Username: dialog.Username,
                Password: password,
                ContentMode: CurrentContentMode,
                RecognitionMode: "MLIP"));
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

    private async void AddSmbSource_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SmbSourceDialog { Owner = this };
        if (dialog.ShowDialog() != true) return;

        var password = dialog.TakePassword();
        BusyOverlay.Visibility = Visibility.Visible;
        StatusText.Text = "正在连接 SMB 并验证 MLIP…";
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
                RecognitionMode: "MLIP"));
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

    private async Task AddOrActivateLocalSourceAsync(string rootPath)
    {
        try
        {
            var fullPath = Path.GetFullPath(rootPath);
            var existing = (_settings.MediaSources ?? []).FirstOrDefault(source => SamePath(source.Location, fullPath));
            if (existing is null)
            {
                var name = Path.GetFileName(fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                await _mediaSourceRegistry.AddAsync(new MediaSourceRequest(
                    name,
                    "LOCAL",
                    fullPath,
                    ContentMode: CurrentContentMode,
                    RecognitionMode: "MLIP"));
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
            _allSeries = ApplyProgress(series);
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
            LibrarySchemaText.Text = $"MLIP v{catalog.SchemaVersion} · {catalog.Series.Count} 部作品";
            LibrarySummaryText.Text = $"{catalog.Series.Count} 部作品 · {catalog.Series.Sum(item => item.Episodes.Count)} 集";
            SearchBox.Text = string.Empty;
            ShowSeries(_allSeries);
            ApplySettingsToView();
            StatusText.Text = $"已载入 {catalog.Series.Count} 部作品";
        }
        catch (Exception error) when (error is IOException or InvalidDataException or Microsoft.Data.Sqlite.SqliteException or NotSupportedException)
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

        _allSeries = _allSeries.Select(series =>
            cached.TryGetValue(series.Uuid, out var posterPath) ? series with { PosterPath = posterPath } : series).ToList();
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
        DetailPanel.DataContext = series;
        DetailColumn.Width = new GridLength(410);
        DetailPanel.Visibility = Visibility.Visible;
    }

    private void CloseDetail_Click(object sender, RoutedEventArgs e)
    {
        DetailPanel.Visibility = Visibility.Collapsed;
        DetailColumn.Width = new GridLength(0);
        DetailPanel.DataContext = null;
    }

    private void OpenMetadataLink_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: Uri uri }) return;
        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            StatusText.Text = $"无法打开元数据页面：{error.Message}";
        }
    }

    private async void PlayEpisode_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: LibraryEpisode episode }) await PlayEpisodeAsync(episode);
    }

    private async Task<bool> PlayEpisodeAsync(
        LibraryEpisode episode,
        long? startPositionMs = null,
        bool showErrorDialog = true)
    {
        var launchGeneration = Interlocked.Increment(ref _playbackGeneration);
        await _playbackStartLock.WaitAsync();
        try
        {
            if (_activeSession is { } previous)
            {
                _supersededSessions.Add(previous);
                previous.SubtitleTracksChanged -= ActiveSession_SubtitleTracksChanged;
                await previous.DisposeAsync();
                if (ReferenceEquals(_activeSession, previous)) _activeSession = null;
            }

            var credential = episode.SourceId > 0 ? _mediaSourceRegistry.GetCredential(episode.SourceId) : null;
            var session = await MpvPlayerLauncher.PlayAsync(
                episode,
                _settings,
                _progressStore,
                startPositionMs,
                credential: credential);
            _activeSession = session;
            StatusText.Text = session is null
                ? $"已交给 Windows 播放器：{episode.DisplayTitle}"
                : $"正在播放并记录进度：{episode.DisplayNumber} {episode.DisplayTitle}";
            if (session is not null)
            {
                session.SubtitleTracksChanged += ActiveSession_SubtitleTracksChanged;
                UpdateActivePlaybackControls(session);
                _ = RefreshAfterPlaybackAsync(session, episode, launchGeneration);
            }
            return true;
        }
        catch (Exception error) when (error is IOException or System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
        {
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

    private async Task RefreshAfterPlaybackAsync(MpvPlaybackSession session, LibraryEpisode playedEpisode, long launchGeneration)
    {
        await session.Completion;
        session.SubtitleTracksChanged -= ActiveSession_SubtitleTracksChanged;
        var wasSuperseded = _supersededSessions.Remove(session);
        if (ReferenceEquals(_activeSession, session))
        {
            _activeSession = null;
            ActivePlaybackControls.Visibility = Visibility.Collapsed;
        }
        LibraryEpisode? nextEpisode = null;
        if (!wasSuperseded && session.WasCompleted && _settings.PlaybackEndAction == "play_next_episode")
        {
            var series = _allSeries.FirstOrDefault(item => item.Episodes.Any(episode => episode.ProgressKey == playedEpisode.ProgressKey));
            if (series is not null) nextEpisode = NextEpisodeResolver.NextAfter(series.Episodes, playedEpisode.ProgressKey);
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

        var selectedSeriesId = (DetailPanel.DataContext as LibrarySeries)?.Id;
        _allSeries = ApplyProgress(_allSeries);
        UpdateContinueWatching();
        ApplySearchFilter();
        if (selectedSeriesId is not null)
        {
            DetailPanel.DataContext = _allSeries.FirstOrDefault(series => series.Id == selectedSeriesId);
        }
        if (!wasSuperseded) StatusText.Text = syncStatus ?? "播放进度已保存";
        if (nextEpisode is not null && launchGeneration == Interlocked.Read(ref _playbackGeneration))
        {
            await PlayEpisodeAsync(nextEpisode);
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
        _posterCacheCancellation?.Cancel();
        _posterCacheCancellation?.Dispose();
        var session = _activeSession;
        _activeSession = null;
        if (session is not null)
        {
            _supersededSessions.Add(session);
            await session.DisposeAsync();
        }
        await _cloudDriveScheduler.DisposeAsync();
        await _webControlServer.DisposeAsync();
        _bangumiMetadata.Dispose();
        _mediaSourceRegistry.Dispose();
        GC.SuppressFinalize(this);
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
            SelectedSubtitleTrackId: session.SelectedSubtitleTrackId);
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
            await session.ExecuteCommandAsync(command);
            if (command.Command.Trim().Equals("stop", StringComparison.OrdinalIgnoreCase) &&
                ReferenceEquals(_activeSession, session))
            {
                _supersededSessions.Add(session);
                _activeSession = null;
                ActivePlaybackControls.Visibility = Visibility.Collapsed;
            }
            return GetPlaybackRuntimeStatus();
        }).Task.Unwrap();

    private void ActiveSession_SubtitleTracksChanged(object? sender, EventArgs e)
    {
        if (sender is not MpvPlaybackSession session) return;
        _ = Dispatcher.InvokeAsync(() =>
        {
            if (ReferenceEquals(_activeSession, session)) UpdateActivePlaybackControls(session);
        });
    }

    private void UpdateActivePlaybackControls(MpvPlaybackSession session)
    {
        _loadingPlaybackTracks = true;
        try
        {
            ActivePlaybackControls.Visibility = Visibility.Visible;
            var choices = new List<SubtitleChoice> { new(null, "关闭字幕") };
            choices.AddRange(session.SubtitleTracks.Select(track => new SubtitleChoice(track.Id, track.DisplayLabel)));
            ActiveSubtitleTrack.ItemsSource = choices;
            ActiveSubtitleTrack.SelectedItem = choices.FirstOrDefault(choice => choice.TrackId == session.SelectedSubtitleTrackId)
                ?? choices[0];
        }
        finally
        {
            _loadingPlaybackTracks = false;
        }
    }

    private async void ActiveSubtitleTrack_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingPlaybackTracks || ActiveSubtitleTrack.SelectedItem is not SubtitleChoice choice || _activeSession is not { } session) return;
        try
        {
            await session.ExecuteCommandAsync(new PlaybackControlCommand("subtitle", SubtitleTrackId: choice.TrackId));
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or ArgumentOutOfRangeException or TimeoutException)
        {
            StatusText.Text = $"切换字幕失败：{error.Message}";
            if (ReferenceEquals(_activeSession, session)) UpdateActivePlaybackControls(session);
        }
    }

    private async void TogglePlayback_Click(object sender, RoutedEventArgs e) =>
        await ExecuteActivePlaybackCommandAsync("toggle");

    private async void StopPlayback_Click(object sender, RoutedEventArgs e) =>
        await ExecuteActivePlaybackCommandAsync("stop");

    private async Task ExecuteActivePlaybackCommandAsync(string command)
    {
        if (_activeSession is not { } session) return;
        try
        {
            await session.ExecuteCommandAsync(new PlaybackControlCommand(command));
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or TimeoutException)
        {
            StatusText.Text = $"播放控制失败：{error.Message}";
        }
    }

    private sealed record SubtitleChoice(int? TrackId, string Label);

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
        if (_loadedSourceId == sourceId) _posterCacheCancellation?.Cancel();
        _mediaSourceRegistry.Remove(sourceId);
        await Dispatcher.InvokeAsync(async () =>
        {
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
        var result = await ScanSourceFromWebAsync(sourceId);
        return ToCloudDriveIngestionSummary(result);
    }

    internal static CloudDriveIngestionSummary ToCloudDriveIngestionSummary(SourceScanResponse result)
    {
        if (result.Error is not null) throw new InvalidDataException($"WebDAV 回扫失败：{result.Error}");
        return new CloudDriveIngestionSummary(Indexed: result.EpisodesFound);
    }

    private async Task<SourceScanResponse> ScanSourceFromWebAsync(long sourceId)
    {
        var result = await _mediaSourceRegistry.ScanAsync(sourceId);
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
        var result = await ScanSourceFromWebAsync(source.Id);
        StatusText.Text = result.Error is null ? $"已扫描 {source.Name}" : result.Error;
    }

    private async void EditSource_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: MediaSourceDefinition source }) return;
        MediaSourceRequest? request = null;
        if (source.Type == "LOCAL")
        {
            var dialog = new OpenFolderDialog
            {
                Title = "选择更新后的 MLIP 媒体库目录",
                InitialDirectory = Directory.Exists(source.Location) ? source.Location : null,
            };
            if (dialog.ShowDialog(this) != true) return;
            request = new MediaSourceRequest(
                source.Name,
                "LOCAL",
                dialog.FolderName,
                ContentMode: source.ContentMode,
                RecognitionMode: "MLIP");
        }
        else if (source.Type == "WEBDAV")
        {
            var dialog = new WebDavSourceDialog(source.Name, source.Location) { Owner = this };
            if (dialog.ShowDialog() != true) return;
            var password = dialog.TakePassword();
            request = new MediaSourceRequest(
                dialog.SourceName,
                "WEBDAV",
                dialog.SourceLocation,
                Username: dialog.Username,
                Password: password,
                ContentMode: source.ContentMode,
                RecognitionMode: "MLIP");
        }
        else if (source.Type == "SMB")
        {
            var dialog = new SmbSourceDialog(source.Name, source.Location) { Owner = this };
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
                RecognitionMode: "MLIP");
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
        _libraryGeneration++;
        _posterCacheCancellation?.Cancel();
        _posterCacheCancellation?.Dispose();
        _posterCacheCancellation = null;
        _loadedSourceId = null;
        _requestedPosterUrls.Clear();
        _allSeries = [];
        UpdateContinueWatching();
        ShowSeries(_allSeries);
        LibraryRootValue.Text = "尚未设置";
        LibrarySchemaText.Text = "MLIP v1-v3";
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
            var previousMode = _settings.CurrentAppMode;
            _settings = update(_settings);
            _settingsStore.Save(_settings);
            ApplySettingsToView();
            if (previousMode != _settings.CurrentAppMode) await ApplyAppModeAsync(_settings.CurrentAppMode);
            return _settings;
        }).Task.Unwrap();

    private void LibraryNav_Click(object sender, RoutedEventArgs e) => ShowPage(showSettings: false);

    private void SettingsNav_Click(object sender, RoutedEventArgs e) => ShowPage(showSettings: true);

    private void ShowPage(bool showSettings)
    {
        LibraryPage.Visibility = showSettings ? Visibility.Collapsed : Visibility.Visible;
        SettingsPage.Visibility = showSettings ? Visibility.Visible : Visibility.Collapsed;
        LibraryNavButton.Tag = showSettings ? null : "Selected";
        SettingsNavButton.Tag = showSettings ? "Selected" : null;
    }

    private void ChoosePlayer_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择 mpv.exe",
            Filter = "mpv 播放器 (mpv.exe)|mpv.exe|可执行文件 (*.exe)|*.exe",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(this) != true) return;

        _settings = _settings with { PlayerPath = dialog.FileName };
        _settingsStore.Save(_settings);
        ApplySettingsToView();
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
            PlayerPathValue.Text = MpvPlayerLauncher.FindMpv(_settings.PlayerPath)
                ?? "自动检测；找不到时使用 Windows 默认播放器";
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
