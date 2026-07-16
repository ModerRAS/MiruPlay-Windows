using System.Text;
using MiruPlay.Windows.Services;

namespace MiruPlay.Windows.Tests;

public sealed class MediaSourceCredentialStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"miruplay-credentials-{Guid.NewGuid():N}");

    [Fact]
    public async Task SmbDomainUsernameAndPasswordAreDpapiProtected()
    {
        var store = new MediaSourceCredentialStore(_directory);
        var credential = new MediaSourceCredential("alice", "s3cret", "WORKGROUP");

        store.Save(7, credential);

        Assert.Equal(credential, store.Get(7));
        var encrypted = await File.ReadAllBytesAsync(Path.Combine(_directory, "source-7.bin"));
        var text = Encoding.UTF8.GetString(encrypted);
        Assert.DoesNotContain("alice", text, StringComparison.Ordinal);
        Assert.DoesNotContain("s3cret", text, StringComparison.Ordinal);
        Assert.DoesNotContain("WORKGROUP", text, StringComparison.Ordinal);

        store.Delete(7);
        Assert.Null(store.Get(7));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
