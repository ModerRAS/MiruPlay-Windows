using Microsoft.Data.Sqlite;
using MiruPlay.Windows.Services;

namespace MiruPlay.Windows.Tests;

public sealed class MediaSourceRegistryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"miruplay-source-{Guid.NewGuid():N}");

    public MediaSourceRegistryTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Anime"));
        File.WriteAllText(Path.Combine(_root, "Anime", "01.mkv"), "video");
        CreateDatabase();
        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task LocalMlipSourceSupportsTestAddUpdateScanAndRemove()
    {
        var settings = new AppSettings(MediaSourceSchemaVersion: 1, MediaSources: []);
        using var registry = new MediaSourceRegistry(() => settings, updated => settings = updated);
        var request = new MediaSourceRequest(
            "Anime",
            "LOCAL",
            _root,
            ContentMode: "ANIME");
        var databasePath = Path.Combine(_root, "library.db");
        var databaseWriteTime = File.GetLastWriteTimeUtc(databasePath);

        var test = await registry.TestAsync(request);
        var added = await registry.AddAsync(request);
        var scanned = await registry.ScanAsync(added.Id);
        var updated = await registry.UpdateAsync(added.Id, request with { Name = "Anime Updated" });

        Assert.True(test.Connected);
        Assert.Equal(1, added.Id);
        Assert.Equal(_root, settings.LibraryRoot);
        Assert.Single(registry.List());
        Assert.Equal(1, scanned.EpisodesFound);
        Assert.Null(scanned.Error);
        Assert.Equal("Anime Updated", updated.Name);
        Assert.True(registry.Get(added.Id)?.IsConnected);
        Assert.Equal(databaseWriteTime, File.GetLastWriteTimeUtc(databasePath));

        registry.Remove(added.Id);

        Assert.Empty(registry.List());
        Assert.Null(settings.LibraryRoot);
    }

    [Fact]
    public async Task DramaMlipSourceIsPersistedAsCurrentMode()
    {
        var settings = new AppSettings(MediaSourceSchemaVersion: 1, MediaSources: []);
        using var registry = new MediaSourceRegistry(() => settings, updated => settings = updated);

        var added = await registry.AddAsync(new MediaSourceRequest(
            "Drama",
            "LOCAL",
            _root,
            ContentMode: "DRAMA",
            RecognitionMode: "MLIP"));

        Assert.Equal("DRAMA", added.ContentMode);
        Assert.Equal("DRAMA", registry.Get(added.Id)?.ContentMode);
        Assert.Equal("drama", settings.CurrentAppMode);
        Assert.Equal(added.Id, settings.ActiveSourceId);
    }

    [Fact]
    public async Task UnsupportedSourceIsReportedWithoutBeingPersisted()
    {
        var settings = new AppSettings(MediaSourceSchemaVersion: 1, MediaSources: []);
        using var registry = new MediaSourceRegistry(() => settings, updated => settings = updated);
        var request = new MediaSourceRequest(
            "Remote",
            "FTP",
            "ftp://example.invalid/share",
            RecognitionMode: "MLIP");

        var test = await registry.TestAsync(request);

        Assert.False(test.Connected);
        await Assert.ThrowsAsync<NotSupportedException>(() => registry.AddAsync(request));
        Assert.Empty(registry.List());
    }

    [Fact]
    public async Task WebDavSourceUsesDpapiCredentialsAndValidatesRemoteMlip()
    {
        var credentialsDirectory = Path.Combine(_root, "credentials");
        var cacheDirectory = Path.Combine(_root, "cache");
        var credentialStore = new MediaSourceCredentialStore(credentialsDirectory);
        var databaseBytes = await File.ReadAllBytesAsync(Path.Combine(_root, "library.db"));
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        var expectedAuthorization = "Basic " + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("alice:p@ss"));
        var serverTask = ServeDatabaseAsync(listener, databaseBytes, expectedAuthorization, requestCount: 3);
        var settings = new AppSettings(MediaSourceSchemaVersion: 1, MediaSources: []);
        using var registry = new MediaSourceRegistry(
            () => settings,
            updated => settings = updated,
            credentialStore,
            new WebDavMlipClient(cacheRoot: cacheDirectory));
        var request = new MediaSourceRequest(
            "Remote Anime",
            "WEBDAV",
            $"http://127.0.0.1:{port}/dav",
            Username: "alice",
            Password: "p@ss",
            ContentMode: "ANIME",
            RecognitionMode: "MLIP");

        var added = await registry.AddAsync(request);
        var scanned = await registry.ScanAsync(added.Id);
        var updated = await registry.UpdateAsync(added.Id, request with
        {
            Name = "Remote Updated",
            Username = null,
            Password = null,
        });
        await serverTask;

        Assert.Equal("WEBDAV", added.Type);
        Assert.Null(settings.LibraryRoot);
        Assert.Equal(1, scanned.EpisodesFound);
        Assert.Null(scanned.Error);
        Assert.Equal("Remote Updated", updated.Name);
        Assert.Equal(added.Id, settings.ActiveSourceId);
        var remoteCatalog = registry.LoadCatalog(added.Id);
        var remoteEpisode = Assert.Single(Assert.Single(remoteCatalog.Series).Episodes);
        Assert.Equal($"http://127.0.0.1:{port}/dav/Anime/01.mkv", remoteEpisode.MediaPath);
        Assert.Equal(new MediaSourceCredential("alice", "p@ss"), registry.GetCredential(added.Id));
        Assert.DoesNotContain("password", System.Text.Json.JsonSerializer.Serialize(settings), StringComparison.OrdinalIgnoreCase);
        var encrypted = await File.ReadAllBytesAsync(Path.Combine(credentialsDirectory, "source-1.bin"));
        Assert.DoesNotContain("p@ss", System.Text.Encoding.UTF8.GetString(encrypted), StringComparison.Ordinal);
        Assert.True(Directory.EnumerateFiles(cacheDirectory, "library.db", SearchOption.AllDirectories).Any());

        registry.Remove(added.Id);
        Assert.Null(registry.GetCredential(added.Id));
        Assert.Empty(registry.List());
        Assert.Null(settings.ActiveSourceId);
    }

    [Fact]
    public async Task WebDavUpdateDoesNotSendStoredCredentialToDifferentAuthority()
    {
        var credentialStore = new MediaSourceCredentialStore(Path.Combine(_root, "scoped-credentials"));
        var databaseBytes = await File.ReadAllBytesAsync(Path.Combine(_root, "library.db"));
        var firstListener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        var secondListener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        firstListener.Start();
        secondListener.Start();
        var firstPort = ((System.Net.IPEndPoint)firstListener.LocalEndpoint).Port;
        var secondPort = ((System.Net.IPEndPoint)secondListener.LocalEndpoint).Port;
        var expectedAuthorization = "Basic " + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("alice:p@ss"));
        var firstServer = ServeDatabaseAsync(firstListener, databaseBytes, expectedAuthorization, requestCount: 1);
        var secondServer = ServeDatabaseAsync(secondListener, databaseBytes, expectedAuthorization: null, requestCount: 1);
        var settings = new AppSettings(MediaSourceSchemaVersion: 1, MediaSources: []);
        using var registry = new MediaSourceRegistry(
            () => settings,
            updated => settings = updated,
            credentialStore,
            new WebDavMlipClient(cacheRoot: Path.Combine(_root, "scoped-cache")));
        var added = await registry.AddAsync(new MediaSourceRequest(
            "First",
            "WEBDAV",
            $"http://127.0.0.1:{firstPort}/dav",
            Username: "alice",
            Password: "p@ss"));

        await registry.UpdateAsync(added.Id, new MediaSourceRequest(
            "Second",
            "WEBDAV",
            $"http://127.0.0.1:{secondPort}/dav"));
        await Task.WhenAll(firstServer, secondServer);

        Assert.Null(registry.GetCredential(added.Id));
    }

    [Fact]
    public void WebDavRequiresHttpsOutsideLoopback()
    {
        Assert.Throws<ArgumentException>(() => WebDavMlipClient.NormalizeRoot("http://example.com/dav"));
        Assert.Equal("https://example.com/dav/", WebDavMlipClient.NormalizeRoot("https://example.com/dav").AbsoluteUri);
        Assert.Equal("http://127.0.0.1:9978/dav/", WebDavMlipClient.NormalizeRoot("http://127.0.0.1:9978/dav").AbsoluteUri);
    }

    [Fact]
    public void LegacyLibraryRootMigratesToOneLocalMlipSource()
    {
        var settingsPath = Path.Combine(_root, "settings.json");
        File.WriteAllText(settingsPath, $$"""
            {
              "LibraryRoot": {{System.Text.Json.JsonSerializer.Serialize(_root)}},
              "PreferredSubtitleLanguage": "auto"
            }
            """);
        var store = new AppSettingsStore(settingsPath);

        var migrated = store.Load();
        var reloaded = store.Load();

        Assert.Equal(1, migrated.MediaSourceSchemaVersion);
        var source = Assert.Single(migrated.MediaSources ?? []);
        Assert.Equal("LOCAL", source.Type);
        Assert.Equal("MLIP", source.RecognitionMode);
        Assert.True(source.IsConnected);
        Assert.Single(reloaded.MediaSources ?? []);
        Assert.DoesNotContain("TypeLabel", File.ReadAllText(settingsPath), StringComparison.Ordinal);
        Assert.DoesNotContain("StatusText", File.ReadAllText(settingsPath), StringComparison.Ordinal);
    }

    private static async Task ServeDatabaseAsync(
        System.Net.Sockets.TcpListener listener,
        byte[] databaseBytes,
        string? expectedAuthorization,
        int requestCount)
    {
        try
        {
            for (var index = 0; index < requestCount; index++)
            {
                using var client = await listener.AcceptTcpClientAsync();
                await using var stream = client.GetStream();
                using var reader = new StreamReader(stream, System.Text.Encoding.ASCII, leaveOpen: true);
                string? line;
                string? authorization = null;
                while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync()))
                {
                    if (line.StartsWith("Authorization:", StringComparison.OrdinalIgnoreCase))
                    {
                        authorization = line["Authorization:".Length..].Trim();
                    }
                }
                Assert.Equal(expectedAuthorization, authorization);
                var header = System.Text.Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 200 OK\r\nContent-Type: application/octet-stream\r\nContent-Length: {databaseBytes.Length}\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(header);
                await stream.WriteAsync(databaseBytes);
            }
        }
        finally
        {
            listener.Stop();
        }
    }

    private void CreateDatabase()
    {
        using var connection = new SqliteConnection($"Data Source={Path.Combine(_root, "library.db")}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA user_version = 3;
            CREATE TABLE meta(key TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE series(id INTEGER PRIMARY KEY, uuid TEXT UNIQUE NOT NULL, title TEXT NOT NULL, original_title TEXT, sort_title TEXT, summary TEXT, year INTEGER, series_type INTEGER NOT NULL DEFAULT 1);
            CREATE TABLE series_release_date(series_id INTEGER PRIMARY KEY, air_date TEXT NOT NULL);
            CREATE TABLE episode(id INTEGER PRIMARY KEY, uuid TEXT UNIQUE NOT NULL, series_id INTEGER NOT NULL, season INTEGER NOT NULL, episode REAL NOT NULL, sort_order REAL NOT NULL, title TEXT, summary TEXT, runtime INTEGER);
            CREATE TABLE media_file(id INTEGER PRIMARY KEY, episode_id INTEGER NOT NULL, path TEXT NOT NULL UNIQUE, size INTEGER, modified_time INTEGER);
            CREATE TABLE media_subtitle(id INTEGER PRIMARY KEY, media_file_id INTEGER NOT NULL, path TEXT NOT NULL, language TEXT, title TEXT, sort_order INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE media_extra(id INTEGER PRIMARY KEY, uuid TEXT UNIQUE NOT NULL, series_id INTEGER NOT NULL, extra_kind INTEGER NOT NULL, ordinal INTEGER NOT NULL, sort_order INTEGER NOT NULL, title TEXT NOT NULL, path TEXT NOT NULL UNIQUE, size INTEGER, modified_time INTEGER, runtime INTEGER);
            CREATE TABLE series_artwork(id INTEGER PRIMARY KEY, series_id INTEGER NOT NULL, artwork_kind INTEGER NOT NULL, path TEXT NOT NULL);
            CREATE TABLE episode_artwork(id INTEGER PRIMARY KEY, episode_id INTEGER NOT NULL, artwork_kind INTEGER NOT NULL, path TEXT NOT NULL);
            CREATE TABLE genre(id INTEGER PRIMARY KEY, name TEXT UNIQUE NOT NULL);
            CREATE TABLE series_genre(series_id INTEGER NOT NULL, genre_id INTEGER NOT NULL, PRIMARY KEY(series_id, genre_id));
            CREATE TABLE series_external_id(series_id INTEGER NOT NULL, provider INTEGER NOT NULL, value TEXT NOT NULL, PRIMARY KEY(series_id, provider, value));
            CREATE TABLE episode_external_id(episode_id INTEGER NOT NULL, provider INTEGER NOT NULL, value TEXT NOT NULL, PRIMARY KEY(episode_id, provider, value));
            CREATE TABLE capability(name TEXT PRIMARY KEY, enabled INTEGER NOT NULL);
            INSERT INTO meta VALUES ('protocol', 'MLIP'), ('schema', '3');
            INSERT INTO capability VALUES ('extra', 1), ('subtitle', 1);
            INSERT INTO series VALUES (1, 'series-1', 'Anime', NULL, NULL, '', 2024, 1);
            INSERT INTO episode VALUES (1, 'episode-1', 1, 1, 1, 1, 'Episode', NULL, 1200);
            INSERT INTO media_file VALUES (1, 1, '/Anime/01.mkv', 5, 1);
            """;
        command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
