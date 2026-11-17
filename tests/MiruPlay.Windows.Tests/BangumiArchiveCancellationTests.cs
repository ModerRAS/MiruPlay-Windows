using System.IO.Compression;
using System.Net;
using System.Text;
using Microsoft.Data.Sqlite;
using MiruPlay.Windows.Services;

namespace MiruPlay.Windows.Tests;

public sealed class BangumiArchiveCancellationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "miruplay-archive-cancel-" + Guid.NewGuid().ToString("N"));

    public BangumiArchiveCancellationTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task DownloadCancellationRemovesStagingFiles()
    {
        using var cancellation = new CancellationTokenSource();
        using var http = new HttpClient(new LatestPlusStreamingArchiveHandler(cancellation, totalBytes: 64 * 1024));
        using var client = new BangumiArchiveClient(http);
        var store = new BangumiArchiveStore(Path.Combine(_root, "store"), client);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.DownloadLatestAsync(cancellationToken: cancellation.Token));

        AssertNoStagingFiles(Path.Combine(_root, "store"));
        Assert.False(File.Exists(store.SubjectFile));
    }

    [Fact]
    public async Task ImportCancellationRemovesStagingFiles()
    {
        var payload = Encoding.UTF8.GetBytes(string.Join('\n', Enumerable.Range(1, 200).Select(index => $"{{\"id\":{index},\"type\":2,\"name\":\"Subject {index}\"}}")) + "\n");
        using var cancellation = new CancellationTokenSource();
        var store = new BangumiArchiveStore(Path.Combine(_root, "store"));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.ImportAsync(
            new CancelsAfterReadStream(payload, cancellation), "subjects.jsonlines", payload.Length, cancellationToken: cancellation.Token));

        AssertNoStagingFiles(Path.Combine(_root, "store"));
        Assert.False(File.Exists(store.SubjectFile));
    }

    [Fact]
    public async Task PreCancelledDownloadDoesNotTouchDisk()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var store = new BangumiArchiveStore(Path.Combine(_root, "store"));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.DownloadLatestAsync(cancellationToken: cancellation.Token));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_root, "store"), "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task ImportStillReplacesArchiveWhenNotCancelled()
    {
        var payload = Encoding.UTF8.GetBytes("{\"id\":1,\"type\":2,\"name\":\"Sousou no Frieren\"}\n");
        var store = new BangumiArchiveStore(Path.Combine(_root, "store"));

        var snapshot = await store.ImportAsync(new MemoryStream(payload), "subjects.jsonlines", payload.Length);

        Assert.True(snapshot.HasSubjectData);
        Assert.Single(store.Search("Sousou no Frieren"));
    }

    private static void AssertNoStagingFiles(string directory)
    {
        if (!Directory.Exists(directory)) return;
        Assert.Empty(Directory.EnumerateFiles(directory, "*.download"));
        Assert.Empty(Directory.EnumerateFiles(directory, "*.upload"));
        Assert.Empty(Directory.EnumerateFiles(directory, "*.raw-upload"));
    }

    /// <summary>Serves latest.json with a valid-looking archive descriptor, then a stream that
    /// hands out a few chunks and cancels the test token from inside the read loop.</summary>
    private sealed class LatestPlusStreamingArchiveHandler(CancellationTokenSource cancellation, int totalBytes) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("latest.json", StringComparison.Ordinal))
            {
                var json = $$"""
                    {"browser_download_url":"https://example.test/archive.zip","name":"archive.zip","size":{{totalBytes}}}
                    """;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new CancellingReadStream(cancellation, totalBytes)) });
        }
    }

    private sealed class CancellingReadStream(CancellationTokenSource cancellation, int totalBytes) : Stream
    {
        private int _sent;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => totalBytes;
        public override long Position { get => _sent; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            if (_sent >= totalBytes) return 0;
            var count = Math.Min(buffer.Length, 16 * 1024);
            buffer.Span[..count].Fill((byte)'x');
            _sent += count;
            cancellation.Cancel();
            return count;
        }
    }

    /// <summary>Stream that cancels the token right after its final chunk is consumed so the
    /// cancel lands deterministically after the bounded upload copy.</summary>
    private sealed class CancelsAfterReadStream(byte[] payload, CancellationTokenSource cancellation) : MemoryStream(payload)
    {
        private bool _cancelled;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await base.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (Position >= Length && !_cancelled)
            {
                _cancelled = true;
                cancellation.Cancel();
            }
            return read;
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}

public sealed class ScanArchiveIsolationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "miruplay-scan-archive-" + Guid.NewGuid().ToString("N"));

    public ScanArchiveIsolationTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task AutomaticScanCompletesWhileOfflineArchiveIsLocked()
    {
        var libraryRoot = Path.Combine(_root, "library");
        var showDirectory = Path.Combine(libraryRoot, "Frieren");
        Directory.CreateDirectory(showDirectory);
        await File.WriteAllBytesAsync(Path.Combine(showDirectory, "Frieren S01E01.mkv"), [0]);
        var archiveDirectory = Path.Combine(_root, "archive");
        Directory.CreateDirectory(archiveDirectory);
        var subjectFile = Path.Combine(archiveDirectory, BangumiArchiveStore.SubjectFileName);
        await File.WriteAllTextAsync(subjectFile,
            "{\"id\":431767,\"type\":2,\"name\":\"Sousou no Frieren\",\"name_cn\":\"葬送的芙莉莲\"}\n");

        var settings = new AppSettings(
            LibraryRoot: libraryRoot,
            MediaSourceSchemaVersion: 1,
            MediaSources: [new MediaSourceDefinition(1, "Local", "LOCAL", libraryRoot, "ANIME", "DIRECTORY", IsConnected: true)],
            ActiveSourceId: 1,
            AutoScanEnabled: true,
            AutoScanIntervalHours: 6);
        var index = new DirectoryLibraryIndex(
            Path.Combine(_root, "library-cache.db"),
            new AnimeVideoClassifier(new EmptyFilenameParser()));
        using var registry = new MediaSourceRegistry(() => settings, updated => settings = updated, directoryIndex: index);
        using var archiveLock = new FileStream(subjectFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var scheduler = new MediaSourceAutoScanScheduler(() => settings, registry.List, registry.ScanAsync);

        var attempted = await scheduler.RunIfDueAsync(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        Assert.Equal(1, attempted);
        var result = await registry.ScanAsync(1);
        Assert.Null(result.Error);
        Assert.Equal(1, result.EpisodesFound);
        var loaded = registry.LoadCatalog(1);
        Assert.Equal("Frieren", Assert.Single(loaded.Series).Title);
    }

    private sealed class EmptyFilenameParser : IAnimeFilenameParser
    {
        public FilenameMetadata Parse(string filename, int maxLength = 128) => new();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
