using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using MiruPlay.Windows.Services;

namespace MiruPlay.Windows.Tests;

public sealed class WebDavMlipClientTests : IDisposable
{
    private readonly string _cacheRoot = Path.Combine(Path.GetTempPath(), $"miruplay-webdav-{Guid.NewGuid():N}");

    [Fact]
    public async Task AuthenticatedArtworkIsCachedOnlyWithinSourceRoot()
    {
        var artwork = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var rootUrl = $"http://127.0.0.1:{port}/dav";
        var expectedAuthorization = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("alice:p@ss"));
        var serverTask = ServeArtworkAsync(listener, artwork, expectedAuthorization);
        using var client = new WebDavMlipClient(cacheRoot: _cacheRoot);

        var first = await client.DownloadArtworkAsync(
            rootUrl,
            $"{rootUrl}/posters/one.png",
            new MediaSourceCredential("alice", "p@ss"));
        var second = await client.DownloadArtworkAsync(
            rootUrl,
            $"{rootUrl}/posters/one.png",
            new MediaSourceCredential("alice", "p@ss"));
        await serverTask;

        Assert.Equal(first, second);
        Assert.Equal(artwork, await File.ReadAllBytesAsync(first));
        await Assert.ThrowsAsync<InvalidDataException>(() => client.DownloadArtworkAsync(
            rootUrl,
            $"http://127.0.0.1:{port}/other/poster.png",
            new MediaSourceCredential("alice", "p@ss")));

        client.DeleteCache(rootUrl);
        Assert.False(Directory.Exists(Path.GetDirectoryName(Path.GetDirectoryName(first))));
    }

    [Fact]
    public async Task Endpoint405PreservesExistingArtworkAndDoesNotRetryIt()
    {
        var artwork = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
        var handler = new CacheThenBanHandler(artwork);
        using var client = new WebDavMlipClient(
            handler,
            _cacheRoot,
            minimumRequestInterval: TimeSpan.Zero,
            initialCircuitCooldown: TimeSpan.FromMinutes(1));
        const string root = "https://example.test/dav";
        const string poster = "https://example.test/dav/poster.png";

        var cached = await client.DownloadArtworkAsync(root, poster, null);
        await Assert.ThrowsAsync<WebDavCircuitOpenException>(() => client.DownloadAndValidateAsync(root, null));
        var reused = await client.DownloadArtworkAsync(root, poster, null);

        Assert.Equal(cached, reused);
        Assert.Equal(artwork, await File.ReadAllBytesAsync(reused));
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task V4ScanPrewarmsOnceAndDownloadsEachPackOnlyOnce()
    {
        var artwork = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
        var assetHash = Hash(artwork);
        var member = $"{assetHash}.png";
        var tar = CreateTar(member, artwork);
        var packHash = Hash(tar);
        var database = CreateV4Database(packHash, tar.Length, assetHash, member, artwork.Length);
        var handler = new V4Handler(database, tar);
        using var client = new WebDavMlipClient(
            handler,
            _cacheRoot,
            minimumRequestInterval: TimeSpan.Zero);

        await client.DownloadAndValidateAsync("https://example.test/dav", null);
        await client.DownloadAndValidateAsync("https://example.test/dav", null);
        var catalog = client.LoadCachedCatalog("https://example.test/dav");

        Assert.Equal(2, handler.LibraryRequests);
        Assert.Equal(2, handler.Prewarms);
        Assert.Equal(1, handler.PackRequests);
        Assert.True(File.Exists(Assert.Single(catalog.Series).PosterPath));
    }

    [Fact]
    public async Task RustFixtureTransitionFetchesOnlyTheNewIncrementalPack()
    {
        var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "mlip-v4");
        var baseDirectory = Path.Combine(fixture, "base");
        var incrementalDirectory = Path.Combine(fixture, "incremental");
        var basePack = Assert.Single(Directory.GetFiles(Path.Combine(baseDirectory, "MLIP-Artwork")));
        var incrementalPacks = Directory.GetFiles(Path.Combine(incrementalDirectory, "MLIP-Artwork"));
        var newPack = Assert.Single(incrementalPacks.Except([Path.Combine(incrementalDirectory, "MLIP-Artwork", Path.GetFileName(basePack))]));
        var handler = new FixtureTransitionHandler(
            File.ReadAllBytes(Path.Combine(baseDirectory, "library.db")),
            File.ReadAllBytes(Path.Combine(incrementalDirectory, "library.db")),
            incrementalPacks.ToDictionary(path => Path.GetFileName(path)!, File.ReadAllBytes, StringComparer.Ordinal));
        using var client = new WebDavMlipClient(handler, _cacheRoot, minimumRequestInterval: TimeSpan.Zero);

        await client.DownloadAndValidateAsync("https://example.test/dav", null);
        await client.DownloadAndValidateAsync("https://example.test/dav", null);

        Assert.Equal(2, handler.LibraryRequests);
        Assert.Equal(2, handler.Prewarms);
        Assert.Equal(1, handler.PackRequests[Path.GetFileName(basePack)]);
        Assert.Equal(1, handler.PackRequests[Path.GetFileName(newPack)]);
    }

    [Fact]
    public async Task BrokenV4PackKeepsMediaCatalogAndLegacyArtworkPath()
    {
        var artwork = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
        var assetHash = Hash(artwork);
        var member = $"{assetHash}.png";
        var validTar = CreateTar(member, artwork);
        var database = CreateV4Database(Hash(validTar), validTar.Length, assetHash, member, artwork.Length);
        var corruptTar = validTar.ToArray();
        corruptTar[600] ^= 0xff;
        var handler = new V4Handler(database, corruptTar);
        using var client = new WebDavMlipClient(handler, _cacheRoot, minimumRequestInterval: TimeSpan.Zero);

        var snapshot = await client.DownloadAndValidateAsync("https://example.test/dav", null);
        var series = Assert.Single(client.LoadCachedCatalog("https://example.test/dav").Series);

        Assert.Equal(4, snapshot.SchemaVersion);
        Assert.Single(series.Episodes);
        Assert.Equal("https://example.test/dav/Fixture/poster.png", series.PosterPath);
        Assert.Equal(1, handler.PackRequests);
    }

    [Theory]
    [InlineData("https://alice:secret@example.test/dav")]
    [InlineData("https://example.test/dav?token=secret")]
    [InlineData("https://example.test/dav#fragment")]
    public void RootRejectsCredentialsQueryAndFragment(string rootUrl)
    {
        Assert.Throws<ArgumentException>(() => WebDavMlipClient.NormalizeRoot(rootUrl));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_cacheRoot)) Directory.Delete(_cacheRoot, true);
    }

    private byte[] CreateV4Database(string packHash, int packLength, string assetHash, string member, int assetLength)
    {
        Directory.CreateDirectory(_cacheRoot);
        var path = Path.Combine(_cacheRoot, $"fixture-{Guid.NewGuid():N}.db");
        using (var connection = new SqliteConnection($"Data Source={path}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = $$"""
                PRAGMA user_version = 4;
                CREATE TABLE meta(key TEXT PRIMARY KEY, value TEXT NOT NULL);
                CREATE TABLE series(id INTEGER PRIMARY KEY, uuid TEXT NOT NULL, title TEXT NOT NULL, original_title TEXT, summary TEXT, year INTEGER);
                CREATE TABLE series_release_date(series_id INTEGER PRIMARY KEY, air_date TEXT NOT NULL);
                CREATE TABLE episode(id INTEGER PRIMARY KEY, uuid TEXT NOT NULL, series_id INTEGER NOT NULL, season INTEGER NOT NULL, episode REAL NOT NULL, sort_order REAL NOT NULL, title TEXT, runtime INTEGER);
                CREATE TABLE media_file(id INTEGER PRIMARY KEY, episode_id INTEGER NOT NULL, path TEXT NOT NULL);
                CREATE TABLE media_subtitle(id INTEGER PRIMARY KEY, media_file_id INTEGER NOT NULL, path TEXT NOT NULL, sort_order INTEGER NOT NULL);
                CREATE TABLE media_extra(id INTEGER PRIMARY KEY, series_id INTEGER NOT NULL, extra_kind INTEGER NOT NULL, ordinal INTEGER NOT NULL, sort_order INTEGER NOT NULL, title TEXT NOT NULL, path TEXT NOT NULL, runtime INTEGER);
                CREATE TABLE series_artwork(id INTEGER PRIMARY KEY, series_id INTEGER NOT NULL, artwork_kind INTEGER NOT NULL, path TEXT, asset_id INTEGER, source_provider INTEGER, source_subject_id TEXT, source_url TEXT, downloaded_at TEXT);
                CREATE TABLE episode_artwork(id INTEGER PRIMARY KEY, episode_id INTEGER NOT NULL, artwork_kind INTEGER NOT NULL, path TEXT, asset_id INTEGER, source_provider INTEGER, source_subject_id TEXT, source_url TEXT, downloaded_at TEXT);
                CREATE TABLE genre(id INTEGER PRIMARY KEY, name TEXT NOT NULL);
                CREATE TABLE series_genre(series_id INTEGER NOT NULL, genre_id INTEGER NOT NULL);
                CREATE TABLE series_external_id(series_id INTEGER NOT NULL, provider INTEGER NOT NULL, value TEXT NOT NULL);
                CREATE TABLE episode_external_id(episode_id INTEGER NOT NULL, provider INTEGER NOT NULL, value TEXT NOT NULL);
                CREATE TABLE capability(name TEXT PRIMARY KEY, enabled INTEGER NOT NULL);
                CREATE TABLE artwork_pack(id INTEGER PRIMARY KEY, path TEXT NOT NULL, sha256 TEXT NOT NULL, byte_length INTEGER NOT NULL, asset_count INTEGER NOT NULL);
                CREATE TABLE artwork_asset(id INTEGER PRIMARY KEY, sha256 TEXT NOT NULL, pack_id INTEGER NOT NULL, member_name TEXT NOT NULL, data_offset INTEGER NOT NULL, byte_length INTEGER NOT NULL, media_type TEXT NOT NULL, width INTEGER, height INTEGER);
                INSERT INTO meta VALUES ('protocol', 'MLIP'), ('schema', '4');
                INSERT INTO capability VALUES ('extra', 1), ('artwork_pack', 1);
                INSERT INTO series VALUES (1, 'series-1', 'Fixture', NULL, '', 2026);
                INSERT INTO episode VALUES (1, 'episode-1', 1, 1, 1, 1, 'Episode', 60);
                INSERT INTO media_file VALUES (1, 1, '/Fixture/01.mkv');
                INSERT INTO artwork_pack VALUES (1, 'MLIP-Artwork/one.tar', '{{packHash}}', {{packLength}}, 1);
                INSERT INTO artwork_asset VALUES (1, '{{assetHash}}', 1, '{{member}}', 512, {{assetLength}}, 'image/png', 1, 1);
                INSERT INTO series_artwork VALUES (1, 1, 1, '/Fixture/poster.png', 1, 1, '1', 'https://example.test/poster.png', '2026-01-01T00:00:00Z');
                """;
            command.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();
        var bytes = File.ReadAllBytes(path);
        File.Delete(path);
        return bytes;
    }

    private static byte[] CreateTar(string name, byte[] content)
    {
        using var output = new MemoryStream();
        var header = new byte[512];
        Encoding.ASCII.GetBytes(name).CopyTo(header, 0);
        WriteOctal(header, 100, 8, 0x1A4);
        WriteOctal(header, 108, 8, 0);
        WriteOctal(header, 116, 8, 0);
        WriteOctal(header, 124, 12, content.Length);
        WriteOctal(header, 136, 12, 0);
        Array.Fill(header, (byte)' ', 148, 8);
        header[156] = (byte)'0';
        Encoding.ASCII.GetBytes("ustar\0").CopyTo(header, 257);
        Encoding.ASCII.GetBytes("00").CopyTo(header, 263);
        WriteOctal(header, 148, 8, header.Sum(value => (int)value));
        output.Write(header);
        output.Write(content);
        output.Write(new byte[(512 - content.Length % 512) % 512]);
        output.Write(new byte[1024]);
        return output.ToArray();
    }

    private static void WriteOctal(byte[] target, int offset, int length, long value)
    {
        Encoding.ASCII.GetBytes(Convert.ToString(value, 8)!.PadLeft(length - 2, '0')).CopyTo(target, offset);
        target[offset + length - 2] = 0;
        target[offset + length - 1] = (byte)' ';
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static async Task ServeArtworkAsync(
        TcpListener listener,
        byte[] artwork,
        string expectedAuthorization)
    {
        using var connection = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await using var stream = connection.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
        var requestLine = await reader.ReadLineAsync();
        string? line;
        string? authorization = null;
        while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync()))
        {
            if (line.StartsWith("Authorization:", StringComparison.OrdinalIgnoreCase))
            {
                authorization = line["Authorization:".Length..].Trim();
            }
        }
        Assert.Equal("GET /dav/posters/one.png HTTP/1.1", requestLine);
        Assert.Equal(expectedAuthorization, authorization);
        var header = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 200 OK\r\nContent-Type: image/png\r\nContent-Length: {artwork.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(header);
        await stream.WriteAsync(artwork);
    }

    private sealed class CacheThenBanHandler(byte[] artwork) : HttpMessageHandler
    {
        private int _requestCount;
        public int RequestCount => Volatile.Read(ref _requestCount);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var index = Interlocked.Increment(ref _requestCount);
            return Task.FromResult(index == 1
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(artwork) }
                : new HttpResponseMessage(HttpStatusCode.MethodNotAllowed));
        }
    }

    private sealed class FixtureTransitionHandler(
        byte[] baseDatabase,
        byte[] incrementalDatabase,
        IReadOnlyDictionary<string, byte[]> packs) : HttpMessageHandler
    {
        private int _libraryRequests;
        private int _prewarms;
        public ConcurrentDictionary<string, int> PackRequests { get; } = new(StringComparer.Ordinal);
        public int LibraryRequests => Volatile.Read(ref _libraryRequests);
        public int Prewarms => Volatile.Read(ref _prewarms);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Method.Method == "PROPFIND")
            {
                Interlocked.Increment(ref _prewarms);
                return Task.FromResult(new HttpResponseMessage((HttpStatusCode)207));
            }
            var name = Path.GetFileName(request.RequestUri!.AbsolutePath);
            if (name.Equals("library.db", StringComparison.OrdinalIgnoreCase))
            {
                var database = Interlocked.Increment(ref _libraryRequests) == 1
                    ? baseDatabase
                    : incrementalDatabase;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(database),
                });
            }
            if (packs.TryGetValue(name, out var pack))
            {
                PackRequests.AddOrUpdate(name, 1, (_, count) => count + 1);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(pack),
                });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class V4Handler(byte[] database, byte[] pack) : HttpMessageHandler
    {
        private int _libraryRequests;
        private int _prewarms;
        private int _packRequests;

        public int LibraryRequests => Volatile.Read(ref _libraryRequests);
        public int Prewarms => Volatile.Read(ref _prewarms);
        public int PackRequests => Volatile.Read(ref _packRequests);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method.Method == "PROPFIND")
            {
                Interlocked.Increment(ref _prewarms);
                return Task.FromResult(new HttpResponseMessage((HttpStatusCode)207));
            }
            if (path.EndsWith("/library.db", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _libraryRequests);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(database),
                });
            }
            if (path.EndsWith("/MLIP-Artwork/one.tar", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _packRequests);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(pack),
                });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
