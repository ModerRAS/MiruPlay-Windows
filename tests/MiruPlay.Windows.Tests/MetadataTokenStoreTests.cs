using System.Text;
using MiruPlay.Windows.Services;

namespace MiruPlay.Windows.Tests;

public sealed class MetadataTokenStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"miruplay-metadata-{Guid.NewGuid():N}.bin");

    [Fact]
    public async Task TmdbTokenIsDpapiProtectedAndCanBeCleared()
    {
        var store = new MetadataTokenStore(_path);

        store.SaveTmdb("tmdb-secret-token");
        store.SaveBangumi("bangumi-secret-token");

        Assert.Equal("tmdb-secret-token", store.Load().Tmdb);
        Assert.Equal("bangumi-secret-token", store.Load().Bangumi);
        var encrypted = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(_path));
        Assert.DoesNotContain("tmdb-secret-token", encrypted, StringComparison.Ordinal);
        Assert.DoesNotContain("bangumi-secret-token", encrypted, StringComparison.Ordinal);
        store.ClearTmdb();
        Assert.Null(store.Load().Tmdb);
        Assert.NotNull(store.Load().Bangumi);
        store.ClearBangumi();
        Assert.False(File.Exists(_path));
    }

    [Fact]
    public async Task OAuthSessionRoundTripsRefreshAndExpiryEncrypted()
    {
        var store = new MetadataTokenStore(_path);

        store.SaveBangumiSession("access-token-value", "refresh-token-value", 123_456_789L);

        var loaded = store.Load();
        Assert.Equal("access-token-value", loaded.Bangumi);
        Assert.Equal("refresh-token-value", loaded.BangumiRefreshToken);
        Assert.Equal(123_456_789L, loaded.BangumiExpiresAtMillis);
        var encrypted = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(_path));
        Assert.DoesNotContain("refresh-token-value", encrypted, StringComparison.Ordinal);
        Assert.DoesNotContain("access-token-value", encrypted, StringComparison.Ordinal);

        store.SaveBangumi("manual-token");
        Assert.Null(store.Load().BangumiRefreshToken);
        Assert.Equal(0, store.Load().BangumiExpiresAtMillis);
        store.ClearBangumi();
        Assert.Null(store.Load().BangumiRefreshToken);
    }

    [Fact]
    public void EmptyTmdbTokenIsRejected() =>
        Assert.Throws<ArgumentException>(() => new MetadataTokenStore(_path).SaveTmdb(" "));

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }
}
