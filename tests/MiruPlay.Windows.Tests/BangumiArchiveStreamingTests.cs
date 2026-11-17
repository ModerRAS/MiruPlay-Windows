using System.Globalization;
using System.Text;
using MiruPlay.Windows.Services;

namespace MiruPlay.Windows.Tests;

public sealed class BangumiArchiveStreamingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "miruplay-archive-stream-" + Guid.NewGuid().ToString("N"));

    public BangumiArchiveStreamingTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task SearchIsCancellableWithoutTouchingTheArchive()
    {
        var store = new BangumiArchiveStore(_root);
        var payload = Encoding.UTF8.GetBytes("{\"id\":1,\"type\":2,\"name\":\"Sousou no Frieren\"}\n");
        await store.ImportAsync(new MemoryStream(payload), "subjects.jsonlines", payload.Length);
        var before = await File.ReadAllBytesAsync(store.SubjectFile);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Task.Run(
            () => store.Search("Frieren", cancellationToken: cancellation.Token),
            cancellation.Token));

        Assert.Equal(before, await File.ReadAllBytesAsync(store.SubjectFile));
    }

    [Fact]
    public async Task SearchStreamsBoundedTopResultsInRankOrder()
    {
        var lines = new StringBuilder();
        for (var index = 1; index <= 200; index++)
            lines.Append(CultureInfo.InvariantCulture, $"{{\"id\":{index},\"type\":2,\"name\":\"Show {index}\",\"rank\":{index},\"score\":5.0}}\n");
        lines.Append("{\"id\":999,\"type\":1,\"name\":\"Show 999\"}\n");
        var payload = Encoding.UTF8.GetBytes(lines.ToString());
        var store = new BangumiArchiveStore(_root);
        await store.ImportAsync(new MemoryStream(payload), "subjects.jsonlines", payload.Length);

        var hits = store.Search("Show", limit: 3);

        Assert.Equal(3, hits.Count);
        Assert.Equal(["1", "2", "3"], hits.Select(hit => hit.AnimeId).ToArray());
        Assert.True(hits[0].Confidence >= hits[1].Confidence && hits[1].Confidence >= hits[2].Confidence);
    }

    [Fact]
    public async Task SearchBySubjectIdShortCircuitsAndReturnsExactSubject()
    {
        var payload = Encoding.UTF8.GetBytes(
            "{\"id\":1,\"type\":2,\"name\":\"A\"}\n" +
            "{\"id\":471578,\"type\":2,\"name\":\"Dr.STONE SCIENCE FUTURE\",\"name_cn\":\"石纪元 科学与未来\"}\n");
        var store = new BangumiArchiveStore(_root);
        await store.ImportAsync(new MemoryStream(payload), "subjects.jsonlines", payload.Length);

        var hit = Assert.Single(store.Search("471578"));

        Assert.Equal("471578", hit.AnimeId);
        Assert.Equal("石纪元 科学与未来", hit.TitleCn);
        Assert.Equal(1, hit.Confidence);
    }

    [Fact]
    public async Task SearchReturnsEmptyWhenQueryBlankOrArchiveMissing()
    {
        var store = new BangumiArchiveStore(_root);
        Assert.Empty(store.Search("   "));
        var payload = Encoding.UTF8.GetBytes("{\"id\":1,\"type\":2,\"name\":\"A\"}\n");
        await store.ImportAsync(new MemoryStream(payload), "subjects.jsonlines", payload.Length);
        File.Delete(store.SubjectFile);
        Assert.Empty(store.Search("A"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
