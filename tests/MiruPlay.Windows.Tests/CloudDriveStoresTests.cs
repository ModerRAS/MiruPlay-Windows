using System.Text;
using System.Text.Json;
using MiruPlay.Windows.Services;

namespace MiruPlay.Windows.Tests;

public sealed class CloudDriveStoresTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"miruplay-cloud-{Guid.NewGuid():N}");

    [Fact]
    public void ConfigNormalizesAndroidCompatibleBoundsAndPersistsEnumName()
    {
        var path = Path.Combine(_directory, "cloud.json");
        var store = new CloudDriveAutomationStore(path);

        var saved = store.Save(new CloudDriveAutomationConfig(
            " http://localhost:19798/ ",
            " user ",
            0,
            " /downloads ",
            " /library ",
            CloudDriveLibraryMode.SingleDirectory,
            3,
            false,
            RssProxyPort: 99_999));

        Assert.Equal("http://localhost:19798", saved.EndpointUrl);
        Assert.Equal("user", saved.Username);
        Assert.Null(saved.WebDavSourceId);
        Assert.Equal(15, saved.IntervalMinutes);
        Assert.Equal(65_535, saved.RssProxyPort);
        Assert.Equal(CloudDriveLibraryMode.SingleDirectory, new CloudDriveAutomationStore(path).Load().LibraryMode);
        Assert.Contains("SINGLE_DIRECTORY", File.ReadAllText(path), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ftp://localhost:19798")]
    [InlineData("http://localhost:19798/api")]
    [InlineData("http://localhost:19798/?token=secret")]
    [InlineData("http://user:secret@localhost:19798")]
    public void ConfigRejectsUnsafeEndpointShapes(string endpoint)
    {
        var store = new CloudDriveAutomationStore(Path.Combine(_directory, "cloud.json"));
        Assert.Throws<ArgumentException>(() => store.Save(new CloudDriveAutomationConfig(EndpointUrl: endpoint)));
    }

    [Fact]
    public async Task CredentialsAreDpapiEncryptedAndClearable()
    {
        var path = Path.Combine(_directory, "credentials.bin");
        var store = new CloudDriveCredentialStore(path);
        store.SavePassword("http://localhost:19798", " password-secret ");
        store.SaveToken("http://localhost:19798", "token-secret");

        Assert.Equal("http://localhost:19798", store.Load().EndpointUrl);
        Assert.Equal(" password-secret ", store.Load().Password);
        Assert.Equal("token-secret", store.Load().Token);
        Assert.Throws<InvalidOperationException>(() => store.LoadForEndpoint("https://other.example.test"));
        store.SaveToken("https://other.example.test", "other-token");
        Assert.Null(store.Load().Password);
        Assert.Equal("other-token", store.LoadForEndpoint("https://other.example.test").Token);
        var encrypted = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(path));
        Assert.DoesNotContain("password-secret", encrypted, StringComparison.Ordinal);
        Assert.DoesNotContain("token-secret", encrypted, StringComparison.Ordinal);
        Assert.DoesNotContain("password-secret", JsonSerializer.Serialize(new { configured = true }), StringComparison.Ordinal);

        store.Clear();
        Assert.False(File.Exists(path));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        GC.SuppressFinalize(this);
    }
}
