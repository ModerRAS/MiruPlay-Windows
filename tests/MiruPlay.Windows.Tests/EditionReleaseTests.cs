using System.Net;
using System.Text;
using MiruPlay.Windows.Services;

namespace MiruPlay.Windows.Tests;

public sealed class EditionReleaseTests
{
    private const string Sha = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Theory]
    [InlineData("full", "full")]
    [InlineData("FULL", "full")]
    [InlineData("lite", "lite")]
    [InlineData("", "lite")]
    [InlineData(null, "lite")]
    [InlineData("bogus", "lite")]
    public void EditionNormalization(string? value, string expected) =>
        Assert.Equal(expected, EditionInfo.Normalize(value));

    [Fact]
    public void CurrentEditionIsKnown() => Assert.Contains(EditionInfo.Current, EditionInfo.Editions);

    [Fact]
    public async Task UpdateManifestForAnotherEditionIsRejected()
    {
        var updater = UpdaterWith(Manifest("full", "2.0.0"));
        var status = await updater.CheckAsync();
        Assert.False(status.UpdateAvailable);
        Assert.Null(status.Latest);
        Assert.NotNull(status.LastError);
    }

    [Fact]
    public async Task UpdateManifestWithoutEditionIsTreatedAsLite()
    {
        var updater = UpdaterWith(Manifest(null, "2.0.0"));
        var status = await updater.CheckAsync();
        Assert.True(status.UpdateAvailable);
    }

    private static WindowsAppUpdater UpdaterWith(string manifestJson) => new(
        manifestUrl: "https://example.test/windows-update.json",
        httpClient: new HttpClient(new JsonHandler(manifestJson)),
        downloadDirectory: Path.Combine(Path.GetTempPath(), "miruplay-edition-tests-" + Guid.NewGuid().ToString("N")),
        currentVersionName: "0.1.0",
        currentVersionCode: 1);

    private static string Manifest(string? edition, string version)
    {
        var editionField = edition is null ? string.Empty : $",\"edition\":\"{edition}\"";
        return $"{{\"versionName\":\"{version}\",\"versionCode\":null,\"releaseName\":\"MiruPlay {version}\",\"tagName\":\"v{version}\",\"publishedAt\":\"2026-01-01T00:00:00Z\",\"releaseUrl\":\"https://example.test/release\",\"assetName\":\"MiruPlay-{version}-win-x64-setup.exe\",\"assetSizeBytes\":10,\"downloadUrl\":\"https://example.test/MiruPlay-{version}-win-x64-setup.exe\",\"sha256\":\"{Sha}\"{editionField}}}";
    }

    private sealed class JsonHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
    }
}
