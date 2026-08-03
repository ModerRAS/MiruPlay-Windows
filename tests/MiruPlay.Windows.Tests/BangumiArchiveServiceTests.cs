using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text;
using MiruPlay.Windows.Services;

namespace MiruPlay.Windows.Tests;

public sealed class BangumiArchiveServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "miruplay-archive-" + Guid.NewGuid().ToString("N"));

    public BangumiArchiveServiceTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task ImportValidatesAndSearchesAnimeRowsWithAliasesAndSeason()
    {
        var subjectLines = """
            {"id":266794,"type":2,"name":"Dr.STONE","name_cn":"石纪元","meta_tags":["Dr Stone"]}
            {"id":471578,"type":2,"name":"Dr.STONE SCIENCE FUTURE","name_cn":"石纪元 科学与未来","meta_tags":["Dr Stone 新石纪 第四季"],"eps":12}
            {"id":99,"type":1,"name":"Not anime"}
            """;
        var store = new BangumiArchiveStore(_root);

        var snapshot = await store.ImportAsync(
            new MemoryStream(Encoding.UTF8.GetBytes(subjectLines)),
            "subjects.jsonlines",
            Encoding.UTF8.GetByteCount(subjectLines));
        var result = store.Search("Dr Stone 新石纪 第四季");

        Assert.True(snapshot.HasSubjectData);
        var hit = Assert.Single(result);
        Assert.Equal("471578", hit.AnimeId);
        Assert.True(hit.Confidence >= .62f);
        Assert.Equal(12, hit.EpisodeCount);
    }

    [Fact]
    public async Task ImportZipExtractsOnlySubjectJsonLinesAndCleansStagingFiles()
    {
        var zipPath = Path.Combine(_root, "archive.zip");
        using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("archive/subject.jsonlines");
            using (var writer = new StreamWriter(entry.Open(), Encoding.UTF8))
                writer.Write("{\"id\":1,\"type\":2,\"name\":\"Test\"}\n");
            var ignored = archive.CreateEntry("episode.jsonlines");
            using (var ignoredWriter = new StreamWriter(ignored.Open(), Encoding.UTF8))
                ignoredWriter.Write("ignored");
        }
        var bytes = await File.ReadAllBytesAsync(zipPath);
        var store = new BangumiArchiveStore(_root);

        await store.ImportAsync(new MemoryStream(bytes), "archive.zip", bytes.Length);

        Assert.Contains("\"name\":\"Test\"", await File.ReadAllTextAsync(store.SubjectFile), StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFiles(_root, "*.upload"));
        Assert.Single(store.Search("Test"));
    }

    [Fact]
    public async Task DownloadRejectsDigestMismatchAfterBoundedTransfer()
    {
        var payload = Encoding.UTF8.GetBytes("archive");
        using var http = new HttpClient(new FixedResponseHandler(payload));
        using var client = new BangumiArchiveClient(http);
        var destination = Path.Combine(_root, "archive.zip");
        var latest = new BangumiArchiveLatest(
            "https://example.test/archive.zip",
            Digest: "sha256:0000000000000000000000000000000000000000000000000000000000000000",
            Name: "archive.zip",
            Size: payload.Length);

        await Assert.ThrowsAsync<InvalidDataException>(() => client.DownloadAsync(latest, destination, maxBytes: 1024));
        Assert.True(File.Exists(destination));
    }

    [Fact]
    public async Task OversizedImportIsRejectedBeforeReadingInput()
    {
        var store = new BangumiArchiveStore(_root);
        var input = new ThrowingStream();

        await Assert.ThrowsAsync<InvalidDataException>(() => store.ImportAsync(
            input,
            "too-large.zip",
            BangumiArchiveStore.MaxArchiveBytes + 1));
        Assert.Equal(0, input.ReadCount);
    }

    private sealed class FixedResponseHandler(byte[] payload) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload),
            });
    }

    private sealed class ThrowingStream : MemoryStream
    {
        public int ReadCount { get; private set; }
        public override int Read(byte[] buffer, int offset, int count)
        {
            ReadCount++;
            throw new InvalidOperationException("input should not be read");
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ReadCount++;
            throw new InvalidOperationException("input should not be read");
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
