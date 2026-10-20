using Microsoft.Data.Sqlite;
using MiruPlay.Windows.Services;

namespace MiruPlay.Windows.Tests;

public sealed class DirectoryMetadataTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"miruplay-metadata-{Guid.NewGuid():N}");
    private readonly DirectoryLibraryIndex _index;

    public DirectoryMetadataTests()
    {
        Directory.CreateDirectory(_root);
        _index = new(Path.Combine(_root, "index.db"));
    }

    [Fact]
    public async Task PreviewApplyAndUndoPersistAtomicallyWithoutChangingEpisodes()
    {
        await _index.ScanAsync(1, _root, [new("Show.S01E01.mkv", 1, 1), new("Other.S01E01.mkv", 1, 1)]);
        var original = _index.LoadDirectory(1, _root).Series;
        var edits = original.Select((series, i) => new SeriesMetadataEdit(series.ApiId,
            new NfoTvShowMetadata(Title: $"Matched {i}", Plot: "Summary", Premiered: "2026-09-01", UniqueIds: [new("bangumi", (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture))]))).ToList();
        var preview = _index.PreviewMetadata(1, _root, edits);
        Assert.Equal("PREVIEW", preview.State);
        Assert.Equal(original.Select(item => item.Title), _index.LoadDirectory(1, _root).Series.Select(item => item.Title));
        Assert.Equal("APPLIED", _index.ApplyMetadata(1, _root, preview.Id).State);
        var reopened = new DirectoryLibraryIndex(Path.Combine(_root, "index.db"));
        var matched = reopened.LoadDirectory(1, _root).Series;
        Assert.Equal(["Matched 0", "Matched 1"], matched.Select(item => item.Title));
        Assert.Equal(original.SelectMany(item => item.Episodes).Select(item => item.ProgressKey).Order(),
            matched.SelectMany(item => item.Episodes).Select(item => item.ProgressKey).Order());
        Assert.All(matched, item => Assert.NotNull(item.ExternalId("Bangumi")));
        Assert.Throws<InvalidOperationException>(() => reopened.ApplyMetadata(1, _root, preview.Id));
        Assert.Equal("UNDONE", reopened.UndoMetadata(1, _root, preview.Id).State);
        Assert.Equal(original.Select(item => item.Title), reopened.LoadDirectory(1, _root).Series.Select(item => item.Title));
        Assert.Equal("UNDONE", Assert.Single(reopened.MetadataHistory(1)).State);
        Assert.Empty(reopened.MetadataHistory(2));
        Assert.Throws<KeyNotFoundException>(() => reopened.UndoMetadata(2, _root, preview.Id));
        _index.Remove(1);
        Assert.Empty(_index.MetadataHistory(1));
    }

    [Fact]
    public async Task StalePreviewAndUndoCannotOverwriteLaterEdits()
    {
        await _index.ScanAsync(1, _root, [new("Show.S01E01.mkv", 1, 1)]);
        var series = Assert.Single(_index.LoadDirectory(1, _root).Series);
        var a = _index.PreviewMetadata(1, _root, [new(series.ApiId, new(Title: "A"))]);
        var stale = _index.PreviewMetadata(1, _root, [new(series.ApiId, new(Title: "Stale"))]);
        _index.ApplyMetadata(1, _root, a.Id);
        Assert.Throws<InvalidOperationException>(() => _index.ApplyMetadata(1, _root, stale.Id));
        var b = _index.PreviewMetadata(1, _root, [new(series.ApiId, new(Title: "B"))]);
        _index.ApplyMetadata(1, _root, b.Id);
        Assert.Throws<InvalidOperationException>(() => _index.UndoMetadata(1, _root, a.Id));
        Assert.Equal("B", Assert.Single(_index.LoadDirectory(1, _root).Series).Title);
        _index.UndoMetadata(1, _root, b.Id);
        Assert.Equal("A", Assert.Single(_index.LoadDirectory(1, _root).Series).Title);
        _index.UndoMetadata(1, _root, a.Id);
        Assert.Equal(series.Title, Assert.Single(_index.LoadDirectory(1, _root).Series).Title);
    }

    [Fact]
    public async Task ScanChangesInvalidatePreviewAndDoNotReplaceManualOverrides()
    {
        await _index.ScanAsync(1, _root, [new("Show.S01E01.mkv", 1, 1)]);
        var series = Assert.Single(_index.LoadDirectory(1, _root).Series);
        var preview = _index.PreviewMetadata(1, _root, [new(series.ApiId, new(Title: "Manual"))]);
        await _index.ScanAsync(1, _root, [new("Show.S01E01.mkv", 1, 1, SeriesMetadata: new(Title: "NFO"))]);
        Assert.Throws<InvalidOperationException>(() => _index.ApplyMetadata(1, _root, preview.Id));
        preview = _index.PreviewMetadata(1, _root, [new(series.ApiId, new(Title: "Manual"))]);
        _index.ApplyMetadata(1, _root, preview.Id);
        await _index.ScanAsync(1, _root, [new("Show.S01E01.mkv", 2, 2, SeriesMetadata: new(Title: "New NFO"))]);
        Assert.Equal("Manual", Assert.Single(_index.LoadDirectory(1, _root).Series).Title);
        _index.UndoMetadata(1, _root, preview.Id);
        Assert.Equal("New NFO", Assert.Single(_index.LoadDirectory(1, _root).Series).Title);
        Assert.Throws<ArgumentException>(() => _index.PreviewMetadata(1, _root, [new(series.ApiId, new(Title: ""))]));
        Assert.Throws<ArgumentException>(() => _index.PreviewMetadata(1, _root, [new(series.ApiId, new(Title: "X", Plot: new string('x', 20_001)))]));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_root, true);
    }
}
