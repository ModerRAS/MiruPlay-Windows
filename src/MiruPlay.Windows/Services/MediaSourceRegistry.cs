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
    public string TypeLabel => Type.ToUpperInvariant() switch
    {
        "LOCAL" => "本地 MLIP",
        "WEBDAV" => "WebDAV MLIP",
        "SMB" => "SMB MLIP",
        _ => Type,
    };

    [JsonIgnore]
    public string StatusText => IsConnected ? "已连接" : "未连接";
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
    string? Error = null);

public sealed class MediaSourceRegistry : IDisposable
{
    private readonly Func<AppSettings> _getSettings;
    private readonly Func<Func<AppSettings, AppSettings>, AppSettings> _updateSettings;
    private readonly MediaSourceCredentialStore _credentials;
    private readonly WebDavMlipClient _webDav;
    private readonly SmbConnectionManager _smbConnections;
    private readonly object _sync = new();

    public MediaSourceRegistry(
        Func<AppSettings> getSettings,
        Action<AppSettings> saveSettings,
        MediaSourceCredentialStore? credentials = null,
        WebDavMlipClient? webDav = null,
        SmbConnectionManager? smbConnections = null)
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
            smbConnections)
    {
    }

    public MediaSourceRegistry(
        Func<AppSettings> getSettings,
        Func<Func<AppSettings, AppSettings>, AppSettings> updateSettings,
        MediaSourceCredentialStore? credentials = null,
        WebDavMlipClient? webDav = null,
        SmbConnectionManager? smbConnections = null)
    {
        _getSettings = getSettings;
        _updateSettings = updateSettings;
        _credentials = credentials ?? new MediaSourceCredentialStore();
        _webDav = webDav ?? new WebDavMlipClient();
        _smbConnections = smbConnections ?? new SmbConnectionManager();
    }

    public IReadOnlyList<MediaSourceInfoDto> List() =>
        Sources(_getSettings()).Select(ToDto).ToList();

    public async Task<SourceTestResponse> TestAsync(MediaSourceRequest request)
    {
        try
        {
            var validated = await ValidateAsync(request).ConfigureAwait(false);
            return new SourceTestResponse(
                true,
                $"MLIP v{validated.SchemaVersion}，{validated.SeriesCount} 部作品");
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
                        throw new InvalidOperationException("该媒体源已经存在。");
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
        var existing = Get(sourceId) ?? throw new KeyNotFoundException("媒体源不存在。");
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
                    "SMB 凭据更新失败，且无法恢复先前连接。",
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
                if (index < 0) throw new KeyNotFoundException("媒体源不存在。");
                if (sources.Any(source => source.Id != sourceId && SameLocation(source.Location, validated.Location)))
                {
                    throw new InvalidOperationException("该媒体源已经存在。");
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
            if (existing.Type == "WEBDAV" && !SameLocation(existing.Location, updatedSource!.Location))
            {
                _webDav.DeleteCache(existing.Location);
            }
            if (existing.Type == "SMB" &&
                (updatedSource!.Type != "SMB" || !SmbPath.ShareRoot(existing.Location).Equals(
                    SmbPath.ShareRoot(updatedSource.Location),
                    StringComparison.OrdinalIgnoreCase)) &&
                !updatedSources.Any(item => item.Id != sourceId && item.Type == "SMB" &&
                    SmbPath.ShareRoot(item.Location).Equals(SmbPath.ShareRoot(existing.Location), StringComparison.OrdinalIgnoreCase)))
            {
                _smbConnections.Disconnect(existing.Location);
            }
            return ToDto(updatedSource!);
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
                    ?? throw new KeyNotFoundException("媒体源不存在。");
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
            if (removedSource!.Type == "WEBDAV") _webDav.DeleteCache(removedSource.Location);
            if (removedSource.Type == "SMB" && !remainingSources.Any(item => item.Type == "SMB" &&
                    SmbPath.ShareRoot(item.Location).Equals(SmbPath.ShareRoot(removedSource.Location), StringComparison.OrdinalIgnoreCase)))
            {
                _smbConnections.Disconnect(removedSource.Location);
            }
        }
    }

    public async Task<SourceScanResponse> ScanAsync(long sourceId)
    {
        var source = Get(sourceId) ?? throw new KeyNotFoundException("媒体源不存在。");
        try
        {
            var validated = source.Type switch
            {
                "LOCAL" => await ValidateLocalAsync(source.Location).ConfigureAwait(false),
                "WEBDAV" => await ValidateWebDavAsync(
                    source.Location,
                    _credentials.Get(sourceId)).ConfigureAwait(false),
                "SMB" => await ValidateSmbAsync(
                    source.Location,
                    _credentials.Get(sourceId)).ConfigureAwait(false),
                _ => throw new NotSupportedException($"Windows 客户端尚未实现 {source.Type} 媒体源。"),
            };
            SetConnectionState(sourceId, connected: true, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            return new SourceScanResponse(sourceId, source.Name, validated.EpisodeCount, 0, 0);
        }
        catch (Exception error) when (IsSourceFailure(error))
        {
            SetConnectionState(sourceId, connected: false, source.LastScanned);
            return new SourceScanResponse(sourceId, source.Name, 0, 0, 0, error.Message);
        }
    }

    public MediaSourceDefinition? Get(long sourceId) =>
        Sources(_getSettings()).FirstOrDefault(source => source.Id == sourceId);

    public LibraryCatalog LoadCatalog(long sourceId)
    {
        var source = Get(sourceId) ?? throw new KeyNotFoundException("媒体源不存在。");
        return source.Type switch
        {
            "LOCAL" => MlipLibraryReader.Load(source.Location),
            "WEBDAV" => _webDav.LoadCachedCatalog(source.Location),
            "SMB" => LoadSmbCatalog(source),
            _ => throw new NotSupportedException($"Windows 客户端尚未实现 {source.Type} 媒体源。"),
        };
    }

    public MediaSourceCredential? GetCredential(long sourceId) => _credentials.Get(sourceId);

    public Task<string> CacheArtworkAsync(long sourceId, string artworkUrl, CancellationToken cancellationToken = default)
    {
        var source = Get(sourceId) ?? throw new KeyNotFoundException("媒体源不存在。");
        if (source.Type != "WEBDAV") throw new NotSupportedException("只有 WebDAV 媒体源需要鉴权海报缓存。");
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
            _ => throw new NotSupportedException($"未知媒体源类型: {request.Type}"),
        };
    }

    private static void ValidateCommon(MediaSourceRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Name)) throw new ArgumentException("媒体源名称不能为空。", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Type)) throw new ArgumentException("媒体源类型不能为空。", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Location)) throw new ArgumentException("媒体源位置不能为空。", nameof(request));
        if (request.ContentMode.Trim().ToUpperInvariant() is not ("ANIME" or "DRAMA"))
        {
            throw new NotSupportedException("内容模式必须是 ANIME 或 DRAMA。");
        }
        if (!request.RecognitionMode.Trim().Equals("MLIP", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException("Windows 客户端当前仅支持 MLIP 识别模式。");
        }
        if (!request.MlipMetadataMode.Trim().Equals("LIBRARY_DB_LOCAL_PRIORITY", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException("Windows 客户端当前仅支持 LIBRARY_DB_LOCAL_PRIORITY 元数据模式。");
        }
    }

    private static async Task<ValidatedSource> ValidateLocalRequestAsync(MediaSourceRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.Username) ||
            !string.IsNullOrWhiteSpace(request.Password) ||
            !string.IsNullOrWhiteSpace(request.Domain))
        {
            throw new NotSupportedException("本地媒体源不接受域、用户名或密码。");
        }
        return await ValidateLocalAsync(request.Location).ConfigureAwait(false);
    }

    private async Task<ValidatedSource> ValidateWebDavRequestAsync(MediaSourceRequest request)
    {
        var credential = new MediaSourceCredential(request.Username?.Trim() ?? "", request.Password ?? "");
        return await ValidateWebDavAsync(request.Location, credential).ConfigureAwait(false);
    }

    private async Task<ValidatedSource> ValidateSmbRequestAsync(
        MediaSourceRequest request,
        bool replaceSmbCredentials)
    {
        var credential = CredentialFromRequest(request);
        return await ValidateSmbAsync(request.Location, credential, replaceSmbCredentials).ConfigureAwait(false);
    }

    private static MediaSourceCredential CredentialFromRequest(MediaSourceRequest request) => new(
        request.Username?.Trim() ?? "",
        request.Password ?? "",
        request.Domain?.Trim());

    private static async Task<ValidatedSource> ValidateLocalAsync(string location)
    {
        var fullPath = Path.GetFullPath(location.Trim());
        try
        {
            var catalog = await Task.Run(() => MlipLibraryReader.Load(fullPath)).ConfigureAwait(false);
            return new ValidatedSource(
                catalog.RootPath,
                "LOCAL",
                catalog.SchemaVersion,
                catalog.Series.Count,
                catalog.Series.Sum(series => series.Episodes.Count),
                null);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or SqliteException or UnauthorizedAccessException)
        {
            throw new InvalidDataException($"无法读取 MLIP 媒体源：{error.Message}", error);
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
                snapshot.SchemaVersion,
                snapshot.SeriesCount,
                snapshot.EpisodeCount,
                credential);
        }
        catch (Exception error) when (error is HttpRequestException or IOException or InvalidDataException or SqliteException or UnauthorizedAccessException or TaskCanceledException)
        {
            throw new InvalidDataException($"无法读取 WebDAV MLIP 媒体源：{error.Message}", error);
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
                catalog.SchemaVersion,
                catalog.Series.Count,
                catalog.Series.Sum(series => series.Episodes.Count),
                credential);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or SqliteException or UnauthorizedAccessException)
        {
            throw new InvalidDataException($"无法读取 SMB MLIP 媒体源：{error.Message}", error);
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
        "MLIP",
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
            NotSupportedException or UnauthorizedAccessException or TaskCanceledException;

    private sealed record ValidatedSource(
        string Location,
        string Type,
        int SchemaVersion,
        int SeriesCount,
        int EpisodeCount,
        MediaSourceCredential? Credential);
}
