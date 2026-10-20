using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using MiruPlay.Windows.Models;

namespace MiruPlay.Windows.Services;

public sealed record MediaSourceDefinition(
    long Id,
    string Name,
    string Type,
    string Location,
    string ContentMode = "ANIME",
    string RecognitionMode = "MLIP",
    string MlipMetadataMode = "LIBRARY_DB_LOCAL_PRIORITY",
    bool DisableOnlineMetadata = false,
    bool IsConnected = false,
    long LastScanned = 0)
{
    [JsonIgnore]
    public string TypeLabel => (Type.ToUpperInvariant(), RecognitionMode.ToUpperInvariant()) switch
    {
        ("LOCAL", "DIRECTORY") => AppStrings.Get("reg.localDir"),
        ("LOCAL", _) => AppStrings.Get("reg.localMlip"),
        ("WEBDAV", "DIRECTORY") => AppStrings.Get("reg.davDir"),
        ("WEBDAV", _) => "WebDAV MLIP",
        ("SMB", "DIRECTORY") => AppStrings.Get("reg.smbDir"),
        ("SMB", _) => "SMB MLIP",
        ("RSS", _) => AppStrings.Get("sources.rss"),
        _ => Type,
    };

    [JsonIgnore]
    public string StatusText => IsConnected ? AppStrings.Get("reg.connected") : AppStrings.Get("reg.disconnected");
}

public sealed record MediaSourceRequest(
    string Name,
    string Type,
    string Location,
    string? DisplayName = null,
    string? Username = null,
    string? Password = null,
    string? Domain = null,
    string ContentMode = "ANIME",
    string RecognitionMode = "MLIP",
    string MlipMetadataMode = "LIBRARY_DB_LOCAL_PRIORITY",
    bool DisableOnlineMetadata = false);

public sealed record MediaSourceInfoDto(
    long Id,
    string Name,
    string Type,
    string ContentMode,
    IReadOnlyDictionary<string, string> ConnectionInfo,
    bool IsConnected,
    long LastScanned);

public sealed record SourceTestResponse(bool Connected, string Message);

public sealed record SourceScanResponse(
    long SourceId,
    string AnimeName,
    int EpisodesFound,
    int NewEpisodes,
    int UpdatedEpisodes,
    string? Error = null,
    int DeletedEpisodes = 0);

public sealed class MediaSourceRegistry : IDisposable
{
    private readonly Func<AppSettings> _getSettings;
    private readonly Func<Func<AppSettings, AppSettings>, AppSettings> _updateSettings;
    private readonly MediaSourceCredentialStore _credentials;
    private readonly WebDavMlipClient _webDav;
    private readonly SmbConnectionManager _smbConnections;
    private readonly Lazy<DirectoryLibraryIndex> _directoryIndex;
    private readonly WebDavDirectoryEnumerator _webDavDirectory;
    private readonly RssMediaCatalogReader _rssMediaCatalogReader;
    private readonly SemaphoreSlim _scanLock = new(1, 1);
    private readonly object _sync = new();

    public MediaSourceRegistry(
        Func<AppSettings> getSettings,
        Action<AppSettings> saveSettings,
        MediaSourceCredentialStore? credentials = null,
        WebDavMlipClient? webDav = null,
        SmbConnectionManager? smbConnections = null,
        DirectoryLibraryIndex? directoryIndex = null,
        WebDavDirectoryEnumerator? webDavDirectory = null,
        RssMediaCatalogReader? rssMediaCatalogReader = null)
        : this(
            getSettings,
            update =>
            {
                var updated = update(getSettings());
                saveSettings(updated);
                return updated;
            },
            credentials,
            webDav,
            smbConnections,
            directoryIndex,
            webDavDirectory,
            rssMediaCatalogReader)
    {
    }

    public MediaSourceRegistry(
        Func<AppSettings> getSettings,
        Func<Func<AppSettings, AppSettings>, AppSettings> updateSettings,
        MediaSourceCredentialStore? credentials = null,
        WebDavMlipClient? webDav = null,
        SmbConnectionManager? smbConnections = null,
        DirectoryLibraryIndex? directoryIndex = null,
        WebDavDirectoryEnumerator? webDavDirectory = null,
        RssMediaCatalogReader? rssMediaCatalogReader = null)
    {
        _getSettings = getSettings;
        _updateSettings = updateSettings;
        _credentials = credentials ?? new MediaSourceCredentialStore();
        _webDav = webDav ?? new WebDavMlipClient();
        _smbConnections = smbConnections ?? new SmbConnectionManager();
        _directoryIndex = new Lazy<DirectoryLibraryIndex>(() => directoryIndex ?? new DirectoryLibraryIndex());
        _webDavDirectory = webDavDirectory ?? new WebDavDirectoryEnumerator();
        _rssMediaCatalogReader = rssMediaCatalogReader ?? new RssMediaCatalogReader();
    }

    public IReadOnlyList<MediaSourceInfoDto> List() =>
        Sources(_getSettings()).Select(ToDto).ToList();

    public async Task<SourceTestResponse> TestAsync(MediaSourceRequest request)
    {
        try
        {
            var validated = await ValidateAsync(request).ConfigureAwait(false);
            return validated.Type == "RSS"
                ? new SourceTestResponse(true, AppStrings.Get("reg.rssAvailable", validated.EpisodeCount, validated.SeriesCount))
                : validated.RecognitionMode == "DIRECTORY"
                    ? new SourceTestResponse(true, AppStrings.Get("reg.dirReadable"))
                    : new SourceTestResponse(true, AppStrings.Get("reg.mlipInfo", validated.SchemaVersion, validated.SeriesCount));
        }
        catch (Exception error) when (IsSourceFailure(error))
        {
            return new SourceTestResponse(false, error.Message);
        }
    }

    public async Task<MediaSourceInfoDto> AddAsync(MediaSourceRequest request)
    {
        var validated = await ValidateAsync(request).ConfigureAwait(false);
        lock (_sync)
        {
            MediaSourceDefinition? addedSource = null;
            try
            {
                _updateSettings(settings =>
                {
                    var sources = Sources(settings).ToList();
                    if (sources.Any(source => SameLocation(source.Location, validated.Location)))
                    {
                        throw new InvalidOperationException(AppStrings.Get("reg.srcExists"));
                    }
                    var sourceId = sources.Count == 0 ? 1 : sources.Max(item => item.Id) + 1;
                    addedSource = ToDefinition(request, sourceId, validated);
                    SaveCredential(sourceId, addedSource.Type, validated.Credential);
                    sources.Add(addedSource);
                    return settings with
                    {
                        LibraryRoot = addedSource.Type == "LOCAL" ? addedSource.Location : null,
                        ActiveSourceId = sourceId,
                        CurrentAppMode = addedSource.ContentMode.ToLowerInvariant(),
                        MediaSources = sources,
                        MediaSourceSchemaVersion = 1,
                    };
                });
                return ToDto(addedSource!);
            }
            catch
            {
                if (addedSource is not null)
                {
                    _credentials.Delete(addedSource.Id);
                    if (addedSource.Type == "SMB") _smbConnections.Disconnect(addedSource.Location);
                }
                throw;
            }
        }
    }

    public async Task<MediaSourceInfoDto> UpdateAsync(long sourceId, MediaSourceRequest request)
    {
        var existing = Get(sourceId) ?? throw new KeyNotFoundException(AppStrings.Get("reg.srcMissing"));
        var effectiveRequest = WithFallbackCredential(sourceId, request);
        var existingCredential = _credentials.Get(sourceId);
        var replaceSmbCredentials = existing.Type == "SMB" &&
            effectiveRequest.Type.Trim().Equals("SMB", StringComparison.OrdinalIgnoreCase) &&
            SmbPath.ShareRoot(existing.Location).Equals(
                SmbPath.ShareRoot(effectiveRequest.Location),
                StringComparison.OrdinalIgnoreCase) &&
            existingCredential != CredentialFromRequest(effectiveRequest);
        ValidatedSource validated;
        try
        {
            validated = await ValidateAsync(effectiveRequest, replaceSmbCredentials).ConfigureAwait(false);
        }
        catch (Exception validationError) when (replaceSmbCredentials)
        {
            try
            {
                _smbConnections.EnsureConnected(existing.Location, existingCredential, replaceOwnedConnection: true);
            }
            catch (Exception rollbackError) when (rollbackError is IOException or InvalidOperationException or UnauthorizedAccessException)
            {
                throw new InvalidOperationException(
                    AppStrings.Get("reg.smbCredFailed"),
                    new AggregateException(validationError, rollbackError));
            }
            throw;
        }
        lock (_sync)
        {
            MediaSourceDefinition? updatedSource = null;
            IReadOnlyList<MediaSourceDefinition> updatedSources = [];
            _updateSettings(settings =>
            {
                var sources = Sources(settings).ToList();
                var index = sources.FindIndex(source => source.Id == sourceId);
                if (index < 0) throw new KeyNotFoundException(AppStrings.Get("reg.srcMissing"));
                if (sources.Any(source => source.Id != sourceId && SameLocation(source.Location, validated.Location)))
                {
                    throw new InvalidOperationException(AppStrings.Get("reg.srcExists"));
                }
                updatedSource = ToDefinition(request, sourceId, validated);
                SaveCredential(sourceId, updatedSource.Type, validated.Credential);
                var wasActive = settings.ActiveSourceId == sourceId;
                sources[index] = updatedSource;
                updatedSources = sources;
                return settings with
                {
                    LibraryRoot = wasActive ? (updatedSource.Type == "LOCAL" ? updatedSource.Location : null) : settings.LibraryRoot,
                    ActiveSourceId = settings.ActiveSourceId,
                    CurrentAppMode = wasActive ? updatedSource.ContentMode.ToLowerInvariant() : settings.CurrentAppMode,
                    MediaSources = sources,
                    MediaSourceSchemaVersion = 1,
                };
            });
            var savedSource = updatedSource!;
            if (existing.Type == "WEBDAV" && !SameLocation(existing.Location, savedSource.Location))
            {
                _webDav.DeleteCache(existing.Location);
            }
            if (existing.Type == "RSS" && !SameLocation(existing.Location, savedSource.Location))
                _rssMediaCatalogReader.Invalidate(existing.Location);
            if (existing.Type == "SMB" &&
                (savedSource.Type != "SMB" || !SmbPath.ShareRoot(existing.Location).Equals(
                    SmbPath.ShareRoot(savedSource.Location),
                    StringComparison.OrdinalIgnoreCase)) &&
                !updatedSources.Any(item => item.Id != sourceId && item.Type == "SMB" &&
                    SmbPath.ShareRoot(item.Location).Equals(SmbPath.ShareRoot(existing.Location), StringComparison.OrdinalIgnoreCase)))
            {
                _smbConnections.Disconnect(existing.Location);
            }
            if ((existing.RecognitionMode == "DIRECTORY" || savedSource.RecognitionMode == "DIRECTORY") &&
                (existing.Type != savedSource.Type ||
                 existing.RecognitionMode != savedSource.RecognitionMode ||
                 !SameLocation(existing.Location, savedSource.Location)))
            {
                _directoryIndex.Value.Remove(sourceId);
            }
            return ToDto(savedSource);
        }
    }

    public void Remove(long sourceId)
    {
        lock (_sync)
        {
            MediaSourceDefinition? removedSource = null;
            IReadOnlyList<MediaSourceDefinition> remainingSources = [];
            _updateSettings(settings =>
            {
                var sources = Sources(settings).ToList();
                removedSource = sources.FirstOrDefault(item => item.Id == sourceId)
                    ?? throw new KeyNotFoundException(AppStrings.Get("reg.srcMissing"));
                sources.Remove(removedSource);
                var wasActive = settings.ActiveSourceId == sourceId;
                var currentMode = settings.CurrentAppMode.ToUpperInvariant();
                var next = wasActive
                    ? sources.FirstOrDefault(item => item.ContentMode == currentMode) ?? sources.FirstOrDefault()
                    : null;
                remainingSources = sources;
                return settings with
                {
                    LibraryRoot = wasActive ? (next?.Type == "LOCAL" ? next.Location : null) : settings.LibraryRoot,
                    ActiveSourceId = wasActive ? next?.Id : settings.ActiveSourceId,
                    CurrentAppMode = wasActive && next is not null ? next.ContentMode.ToLowerInvariant() : settings.CurrentAppMode,
                    MediaSources = sources,
                    MediaSourceSchemaVersion = 1,
                };
            });
            _credentials.Delete(sourceId);
            if (_directoryIndex.IsValueCreated) _directoryIndex.Value.Remove(sourceId);
            if (removedSource!.Type == "WEBDAV") _webDav.DeleteCache(removedSource.Location);
            if (removedSource.Type == "RSS") _rssMediaCatalogReader.Invalidate(removedSource.Location);
            if (removedSource.Type == "SMB" && !remainingSources.Any(item => item.Type == "SMB" &&
                    SmbPath.ShareRoot(item.Location).Equals(SmbPath.ShareRoot(removedSource.Location), StringComparison.OrdinalIgnoreCase)))
            {
                _smbConnections.Disconnect(removedSource.Location);
            }
        }
    }

    public async Task<SourceScanResponse> ScanAsync(long sourceId) =>
        await ScanAsync(sourceId, progress: null, CancellationToken.None).ConfigureAwait(false);

    public Task<SourceScanResponse> ScanAsync(long sourceId, CancellationToken cancellationToken) =>
        ScanAsync(sourceId, progress: null, cancellationToken);

    public async Task<SourceScanResponse> ScanAsync(
        long sourceId,
        IProgress<DirectoryScanProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        await _scanLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ScanCoreAsync(sourceId, progress, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _scanLock.Release();
        }
    }

    private async Task<SourceScanResponse> ScanCoreAsync(
        long sourceId,
        IProgress<DirectoryScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        var source = Get(sourceId) ?? throw new KeyNotFoundException(AppStrings.Get("reg.srcMissing"));
        try
        {
            if (source.Type == "RSS")
            {
                _rssMediaCatalogReader.Invalidate(source.Location);
                var catalog = await _rssMediaCatalogReader.LoadAsync(source.Location, cancellationToken).ConfigureAwait(false);
                SetConnectionState(sourceId, connected: true, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                return new SourceScanResponse(sourceId, source.Name, catalog.Series.Sum(item => item.Episodes.Count), 0, 0);
            }
            if (source.RecognitionMode == "DIRECTORY")
            {
                var result = source.Type switch
                {
                    "LOCAL" => await _directoryIndex.Value.ScanLocalAsync(
                        sourceId,
                        source.Location,
                        progress,
                        cancellationToken).ConfigureAwait(false),
                    "WEBDAV" => await ScanWebDavDirectoryAsync(
                        sourceId,
                        source,
                        progress,
                        cancellationToken).ConfigureAwait(false),
                    "SMB" => await ScanSmbDirectoryAsync(
                        sourceId,
                        source,
                        progress,
                        cancellationToken).ConfigureAwait(false),
                    _ => throw new NotSupportedException(AppStrings.Get("reg.notImplDir", source.Type)),
                };
                SetConnectionState(sourceId, connected: true, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                return new SourceScanResponse(
                    sourceId,
                    source.Name,
                    result.EpisodesFound,
                    result.NewEpisodes,
                    result.UpdatedEpisodes,
                    DeletedEpisodes: result.DeletedEpisodes);
            }
            var validated = source.Type switch
            {
                "LOCAL" => await ValidateLocalAsync(source.Location).ConfigureAwait(false),
                "WEBDAV" => await ValidateWebDavAsync(
                    source.Location,
                    _credentials.Get(sourceId)).ConfigureAwait(false),
                "SMB" => await ValidateSmbAsync(
                    source.Location,
                    _credentials.Get(sourceId)).ConfigureAwait(false),
                _ => throw new NotSupportedException(AppStrings.Get("reg.notImplSrc", source.Type)),
            };
            SetConnectionState(sourceId, connected: true, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            return new SourceScanResponse(sourceId, source.Name, validated.EpisodeCount, 0, 0);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error) when (IsSourceFailure(error))
        {
            SetConnectionState(sourceId, connected: false, source.LastScanned);
            return new SourceScanResponse(sourceId, source.Name, 0, 0, 0, error.Message);
        }
    }

    public MediaSourceDefinition? Get(long sourceId) =>
        Sources(_getSettings()).FirstOrDefault(source => source.Id == sourceId);

    public MetadataBatch PreviewMetadata(long sourceId, IReadOnlyList<SeriesMetadataEdit> edits)
    {
        var source = WritableDirectorySource(sourceId);
        return _directoryIndex.Value.PreviewMetadata(sourceId, source.Location, edits);
    }

    public MetadataBatch ApplyMetadata(long sourceId, string batchId)
    {
        var source = WritableDirectorySource(sourceId);
        return _directoryIndex.Value.ApplyMetadata(sourceId, source.Location, batchId);
    }

    public MetadataBatch UndoMetadata(long sourceId, string batchId)
    {
        var source = WritableDirectorySource(sourceId);
        return _directoryIndex.Value.UndoMetadata(sourceId, source.Location, batchId);
    }

    public IReadOnlyList<MetadataBatch> MetadataHistory(long sourceId)
    {
        _ = WritableDirectorySource(sourceId);
        return _directoryIndex.Value.MetadataHistory(sourceId);
    }

    private MediaSourceDefinition WritableDirectorySource(long sourceId)
    {
        var source = Get(sourceId) ?? throw new KeyNotFoundException(AppStrings.Get("reg.srcMissing"));
        if (source.RecognitionMode != "DIRECTORY")
            throw new NotSupportedException(AppStrings.Get("reg.dirOnly"));
        return source;
    }

    public LibraryCatalog LoadCatalog(long sourceId)
    {
        var source = Get(sourceId) ?? throw new KeyNotFoundException(AppStrings.Get("reg.srcMissing"));
        if (source.Type == "RSS")
        {
            var catalog = _rssMediaCatalogReader.LoadAsync(source.Location).GetAwaiter().GetResult();
            return catalog with
            {
                Series = catalog.Series.Select(series => series with
                {
                    Episodes = series.Episodes.Select(episode => episode with { SourceId = sourceId }).ToList(),
                }).ToList(),
            };
        }
        return source.RecognitionMode == "DIRECTORY"
            ? _directoryIndex.Value.LoadDirectory(sourceId, source.Location)
            : source.Type switch
            {
                "LOCAL" => MlipLibraryReader.Load(source.Location),
                "WEBDAV" => _webDav.LoadCachedCatalog(source.Location),
                "SMB" => LoadSmbCatalog(source),
                _ => throw new NotSupportedException(AppStrings.Get("reg.notImplSrc", source.Type)),
            };
    }

    private async Task<DirectoryScanResult> ScanWebDavDirectoryAsync(
        long sourceId,
        MediaSourceDefinition source,
        IProgress<DirectoryScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        var entries = await _webDavDirectory.EnumerateAsync(
            source.Location,
            _credentials.Get(sourceId),
            progress,
            cancellationToken).ConfigureAwait(false);
        return await _directoryIndex.Value.ScanAsync(
            sourceId,
            source.Location,
            entries,
            progress,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<DirectoryScanResult> ScanSmbDirectoryAsync(
        long sourceId,
        MediaSourceDefinition source,
        IProgress<DirectoryScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        var normalized = SmbPath.NormalizeRoot(source.Location);
        await Task.Run(
            () => _smbConnections.EnsureConnected(normalized, _credentials.Get(sourceId)),
            cancellationToken).ConfigureAwait(false);
        return await _directoryIndex.Value.ScanFileSystemAsync(
            sourceId,
            SmbPath.ToUncPath(normalized),
            normalized,
            progress,
            cancellationToken).ConfigureAwait(false);
    }

    public MediaSourceCredential? GetCredential(long sourceId) => _credentials.Get(sourceId);

    public async Task<IReadOnlyList<string>> DiscoverWebDavSidecarsAsync(
        long sourceId,
        string mediaPath,
        CancellationToken cancellationToken = default)
    {
        var source = Get(sourceId);
        if (source?.Type != "WEBDAV") return [];
        if (!Uri.TryCreate(mediaPath, UriKind.Absolute, out var mediaUri)) return [];
        try
        {
            var siblings = await _webDavDirectory.ListSiblingFilesAsync(
                source.Location,
                mediaUri,
                _credentials.Get(sourceId),
                cancellationToken).ConfigureAwait(false);
            return ExternalAudioPathMatcher.Find(
                mediaUri.AbsoluteUri,
                siblings.Select(uri => uri.AbsoluteUri));
        }
        catch (Exception error) when (error is IOException or HttpRequestException or InvalidDataException or UnauthorizedAccessException or OperationCanceledException or TimeoutException)
        {
            return [];
        }
    }

    public async Task<IReadOnlyList<string>> DiscoverSmbSidecarsAsync(
        long sourceId,
        string mediaPath,
        CancellationToken cancellationToken = default)
    {
        var source = Get(sourceId);
        if (source?.Type != "SMB") return [];
        string root;
        try
        {
            root = SmbPath.ToUncPath(source.Location);
        }
        catch (Exception error) when (error is ArgumentException or InvalidDataException)
        {
            return [];
        }
        try
        {
            return await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                _smbConnections.EnsureConnected(source.Location, _credentials.Get(sourceId));
                var directory = Path.GetDirectoryName(mediaPath);
                if (string.IsNullOrEmpty(directory)) return (IReadOnlyList<string>)[];
                var candidates = Directory.EnumerateFiles(directory)
                    .Where(DirectoryLibraryIndex.IsExternalAudioFile)
                    .Take(1_024)
                    .ToList();
                return ExternalAudioPathMatcher.MatchUncSiblings(root, mediaPath, candidates);
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException or OperationCanceledException or TimeoutException)
        {
            return [];
        }
    }

    internal WebDavPlaybackProxy CreatePlaybackProxy(long sourceId, LibraryEpisode episode)
    {
        var source = Get(sourceId) ?? throw new KeyNotFoundException(AppStrings.Get("reg.srcMissing"));
        if (source.Type != "WEBDAV") throw new NotSupportedException(AppStrings.Get("reg.proxyOnly"));
        return new WebDavPlaybackProxy(_webDav, source.Location, _credentials.Get(sourceId), episode);
    }

    public Task<string> CacheArtworkAsync(long sourceId, string artworkUrl, CancellationToken cancellationToken = default)
    {
        var source = Get(sourceId) ?? throw new KeyNotFoundException(AppStrings.Get("reg.srcMissing"));
        if (source.Type != "WEBDAV") throw new NotSupportedException(AppStrings.Get("reg.posterOnly"));
        return _webDav.DownloadArtworkAsync(
            source.Location,
            artworkUrl,
            _credentials.Get(sourceId),
            cancellationToken);
    }

    private LibraryCatalog LoadSmbCatalog(MediaSourceDefinition source)
    {
        _smbConnections.EnsureConnected(source.Location, _credentials.Get(source.Id));
        return MlipLibraryReader.LoadSmb(source.Location);
    }

    public void Dispose()
    {
        _smbConnections.Dispose();
        _webDav.Dispose();
        _webDavDirectory.Dispose();
        _scanLock.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task<ValidatedSource> ValidateAsync(
        MediaSourceRequest request,
        bool replaceSmbCredentials = false)
    {
        ValidateCommon(request);
        return request.Type.Trim().ToUpperInvariant() switch
        {
            "LOCAL" => await ValidateLocalRequestAsync(request).ConfigureAwait(false),
            "WEBDAV" => await ValidateWebDavRequestAsync(request).ConfigureAwait(false),
            "SMB" => await ValidateSmbRequestAsync(request, replaceSmbCredentials).ConfigureAwait(false),
            "RSS" => await ValidateRssRequestAsync(request).ConfigureAwait(false),
            _ => throw new NotSupportedException(AppStrings.Get("reg.unknownType", request.Type)),
        };
    }

    private static void ValidateCommon(MediaSourceRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Name)) throw new ArgumentException(AppStrings.Get("reg.nameRequired"), nameof(request));
        if (string.IsNullOrWhiteSpace(request.Type)) throw new ArgumentException(AppStrings.Get("reg.typeRequired"), nameof(request));
        if (string.IsNullOrWhiteSpace(request.Location)) throw new ArgumentException(AppStrings.Get("reg.locationRequired"), nameof(request));
        if (request.ContentMode.Trim().ToUpperInvariant() is not ("ANIME" or "DRAMA"))
        {
            throw new NotSupportedException(AppStrings.Get("reg.modeAnimeDrama"));
        }
        if (request.Type.Trim().Equals("RSS", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(request.Username) || !string.IsNullOrEmpty(request.Password) || !string.IsNullOrWhiteSpace(request.Domain))
                throw new NotSupportedException(AppStrings.Get("reg.rssNoCred"));
            return;
        }
        if (request.RecognitionMode.Trim().ToUpperInvariant() is not ("MLIP" or "DIRECTORY"))
        {
            throw new NotSupportedException(AppStrings.Get("reg.recogMode"));
        }
        if (request.RecognitionMode.Trim().Equals("MLIP", StringComparison.OrdinalIgnoreCase) &&
            !request.MlipMetadataMode.Trim().Equals("LIBRARY_DB_LOCAL_PRIORITY", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException(AppStrings.Get("reg.mlipMode"));
        }
    }

    private async Task<ValidatedSource> ValidateRssRequestAsync(MediaSourceRequest request)
    {
        var catalog = await _rssMediaCatalogReader.LoadAsync(request.Location).ConfigureAwait(false);
        return new ValidatedSource(
            catalog.RootPath,
            "RSS",
            "RSS",
            catalog.SchemaVersion,
            catalog.Series.Count,
            catalog.Series.Sum(series => series.Episodes.Count),
            null);
    }

    private static async Task<ValidatedSource> ValidateLocalRequestAsync(MediaSourceRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.Username) ||
            !string.IsNullOrWhiteSpace(request.Password) ||
            !string.IsNullOrWhiteSpace(request.Domain))
        {
            throw new NotSupportedException(AppStrings.Get("reg.localNoCred"));
        }
        if (request.RecognitionMode.Trim().Equals("DIRECTORY", StringComparison.OrdinalIgnoreCase))
            return await ValidateLocalDirectoryAsync(request.Location).ConfigureAwait(false);
        return await ValidateLocalAsync(request.Location).ConfigureAwait(false);
    }

    private async Task<ValidatedSource> ValidateWebDavRequestAsync(MediaSourceRequest request)
    {
        var credential = new MediaSourceCredential(request.Username?.Trim() ?? "", request.Password ?? "");
        if (request.RecognitionMode.Trim().Equals("DIRECTORY", StringComparison.OrdinalIgnoreCase))
        {
            var root = WebDavMlipClient.NormalizeRoot(request.Location);
            await _webDavDirectory.ValidateAsync(root.AbsoluteUri, credential).ConfigureAwait(false);
            return new ValidatedSource(root.AbsoluteUri.TrimEnd('/'), "WEBDAV", "DIRECTORY", 1, 0, 0, credential);
        }
        return await ValidateWebDavAsync(request.Location, credential).ConfigureAwait(false);
    }

    private async Task<ValidatedSource> ValidateSmbRequestAsync(
        MediaSourceRequest request,
        bool replaceSmbCredentials)
    {
        var credential = CredentialFromRequest(request);
        if (request.RecognitionMode.Trim().Equals("DIRECTORY", StringComparison.OrdinalIgnoreCase))
        {
            var normalized = SmbPath.NormalizeRoot(request.Location);
            await Task.Run(
                () => _smbConnections.EnsureConnected(normalized, credential, replaceSmbCredentials)).ConfigureAwait(false);
            return new ValidatedSource(normalized, "SMB", "DIRECTORY", 1, 0, 0, credential);
        }
        return await ValidateSmbAsync(request.Location, credential, replaceSmbCredentials).ConfigureAwait(false);
    }

    private static MediaSourceCredential CredentialFromRequest(MediaSourceRequest request) => new(
        request.Username?.Trim() ?? "",
        request.Password ?? "",
        request.Domain?.Trim());

    private static Task<ValidatedSource> ValidateLocalDirectoryAsync(string location)
    {
        var fullPath = DirectoryLibraryIndex.NormalizeLocalRoot(location);
        return Task.FromResult(new ValidatedSource(fullPath, "LOCAL", "DIRECTORY", 1, 0, 0, null));
    }

    private static async Task<ValidatedSource> ValidateLocalAsync(string location)
    {
        var fullPath = Path.GetFullPath(location.Trim());
        try
        {
            var catalog = await Task.Run(() => MlipLibraryReader.Load(fullPath)).ConfigureAwait(false);
            return new ValidatedSource(
                catalog.RootPath,
                "LOCAL",
                "MLIP",
                catalog.SchemaVersion,
                catalog.Series.Count,
                catalog.Series.Sum(series => series.Episodes.Count),
                null);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or SqliteException or UnauthorizedAccessException)
        {
            throw new InvalidDataException(AppStrings.Get("reg.readMlip", error.Message), error);
        }
    }

    private async Task<ValidatedSource> ValidateWebDavAsync(string location, MediaSourceCredential? credential)
    {
        try
        {
            var root = WebDavMlipClient.NormalizeRoot(location);
            var snapshot = await _webDav.DownloadAndValidateAsync(root.AbsoluteUri, credential).ConfigureAwait(false);
            return new ValidatedSource(
                root.AbsoluteUri.TrimEnd('/'),
                "WEBDAV",
                "MLIP",
                snapshot.SchemaVersion,
                snapshot.SeriesCount,
                snapshot.EpisodeCount,
                credential);
        }
        catch (Exception error) when (error is HttpRequestException or IOException or InvalidDataException or SqliteException or UnauthorizedAccessException or TaskCanceledException)
        {
            throw new InvalidDataException(AppStrings.Get("reg.readDavMlip", error.Message), error);
        }
    }

    private async Task<ValidatedSource> ValidateSmbAsync(
        string location,
        MediaSourceCredential? credential,
        bool replaceSmbCredentials = false)
    {
        try
        {
            var normalized = SmbPath.NormalizeRoot(location);
            var catalog = await Task.Run(() =>
            {
                _smbConnections.EnsureConnected(normalized, credential, replaceSmbCredentials);
                return MlipLibraryReader.LoadSmb(normalized);
            }).ConfigureAwait(false);
            return new ValidatedSource(
                normalized,
                "SMB",
                "MLIP",
                catalog.SchemaVersion,
                catalog.Series.Count,
                catalog.Series.Sum(series => series.Episodes.Count),
                credential);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or SqliteException or UnauthorizedAccessException)
        {
            throw new InvalidDataException(AppStrings.Get("reg.readSmbMlip", error.Message), error);
        }
    }

    private MediaSourceRequest WithFallbackCredential(long sourceId, MediaSourceRequest request)
    {
        if (!request.Type.Trim().Equals("WEBDAV", StringComparison.OrdinalIgnoreCase) &&
            !request.Type.Trim().Equals("SMB", StringComparison.OrdinalIgnoreCase)) return request;
        var source = Get(sourceId);
        var existing = _credentials.Get(sourceId);
        if (source is null || existing is null || !HasSameCredentialScope(source, request)) return request;
        return request with
        {
            Username = string.IsNullOrWhiteSpace(request.Username) ? existing.Username : request.Username,
            Password = string.IsNullOrEmpty(request.Password) ? existing.Password : request.Password,
            Domain = string.IsNullOrWhiteSpace(request.Domain) ? existing.Domain : request.Domain,
        };
    }

    private static bool HasSameCredentialScope(MediaSourceDefinition source, MediaSourceRequest request)
    {
        var requestedType = request.Type.Trim().ToUpperInvariant();
        if (source.Type != requestedType) return false;
        if (requestedType == "SMB")
        {
            return SmbPath.ShareRoot(source.Location).Equals(
                SmbPath.ShareRoot(request.Location),
                StringComparison.OrdinalIgnoreCase);
        }
        if (requestedType != "WEBDAV") return false;
        var current = WebDavMlipClient.NormalizeRoot(source.Location);
        var requested = WebDavMlipClient.NormalizeRoot(request.Location);
        return Uri.Compare(
            current,
            requested,
            UriComponents.SchemeAndServer,
            UriFormat.SafeUnescaped,
            StringComparison.OrdinalIgnoreCase) == 0;
    }

    private void SaveCredential(long sourceId, string type, MediaSourceCredential? credential)
    {
        if ((type == "WEBDAV" || type == "SMB") && credential is not null) _credentials.Save(sourceId, credential);
        else _credentials.Delete(sourceId);
    }

    private void SetConnectionState(long sourceId, bool connected, long lastScanned)
    {
        lock (_sync)
        {
            _updateSettings(settings =>
            {
                var sources = Sources(settings).ToList();
                var index = sources.FindIndex(item => item.Id == sourceId);
                if (index < 0) return settings;
                sources[index] = sources[index] with { IsConnected = connected, LastScanned = lastScanned };
                return settings with { MediaSources = sources, MediaSourceSchemaVersion = 1 };
            });
        }
    }

    private static MediaSourceDefinition ToDefinition(
        MediaSourceRequest request,
        long id,
        ValidatedSource validated) => new(
        id,
        request.Name.Trim(),
        validated.Type,
        validated.Location,
        request.ContentMode.Trim().ToUpperInvariant(),
        validated.RecognitionMode,
        "LIBRARY_DB_LOCAL_PRIORITY",
        request.DisableOnlineMetadata,
        true,
        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    private static MediaSourceInfoDto ToDto(MediaSourceDefinition source)
    {
        var connectionInfo = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["url"] = source.Location,
            ["recognitionMode"] = source.RecognitionMode,
            ["mlipMetadataMode"] = source.MlipMetadataMode,
        };
        if (source.Type == "LOCAL") connectionInfo["path"] = source.Location;
        if (source.Type == "SMB") connectionInfo["uncPath"] = SmbPath.ToUncPath(source.Location);
        return new MediaSourceInfoDto(
            source.Id,
            source.Name,
            source.Type,
            source.ContentMode,
            connectionInfo,
            source.IsConnected,
            source.LastScanned);
    }

    private static IReadOnlyList<MediaSourceDefinition> Sources(AppSettings settings) =>
        settings.MediaSources ?? [];

    private static bool SameLocation(string? first, string? second)
    {
        if (first is null || second is null) return false;
        if (first.StartsWith("smb://", StringComparison.OrdinalIgnoreCase) &&
            second.StartsWith("smb://", StringComparison.OrdinalIgnoreCase))
        {
            return SmbPath.NormalizeRoot(first).Equals(SmbPath.NormalizeRoot(second), StringComparison.OrdinalIgnoreCase);
        }
        if (Uri.TryCreate(first, UriKind.Absolute, out var firstUri) && firstUri.Scheme is "http" or "https" &&
            Uri.TryCreate(second, UriKind.Absolute, out var secondUri) && secondUri.Scheme is "http" or "https")
        {
            if (!string.IsNullOrEmpty(firstUri.Query) || !string.IsNullOrEmpty(secondUri.Query))
                return firstUri.Equals(secondUri);
            return WebDavMlipClient.NormalizeRoot(firstUri.AbsoluteUri).Equals(
                WebDavMlipClient.NormalizeRoot(secondUri.AbsoluteUri));
        }
        return Path.GetFullPath(first).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Equals(
                Path.GetFullPath(second).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSourceFailure(Exception error) =>
        error is HttpRequestException or IOException or InvalidDataException or SqliteException or
            ArgumentException or NotSupportedException or UnauthorizedAccessException or TaskCanceledException;

    private sealed record ValidatedSource(
        string Location,
        string Type,
        string RecognitionMode,
        int SchemaVersion,
        int SeriesCount,
        int EpisodeCount,
        MediaSourceCredential? Credential);
}
