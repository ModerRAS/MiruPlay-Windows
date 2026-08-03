using System.Net;
using System.Text;
using MiruPlay.Windows.Services;

namespace MiruPlay.Windows.Tests;

public sealed class OperationsServicesTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"miruplay-operations-{Guid.NewGuid():N}");

    public OperationsServicesTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void SettingsRemainUserScopedAndPersistNonSecretLogConfiguration()
    {
        var path = Path.Combine(_directory, "settings.json");
        var store = new AppSettingsStore(path);
        store.Save(new AppSettings(
            AutoScanEnabled: true,
            AutoScanIntervalHours: 12,
            LogUploadEnabled: true,
            LogUploadEndpoint: "https://logs.example.test",
            LogUploadStreamName: "windows"));

        var loaded = store.Load();
        Assert.True(loaded.AutoScanEnabled);
        Assert.Equal(12, loaded.AutoScanIntervalHours);
        Assert.True(loaded.LogUploadEnabled);
        Assert.Equal("https://logs.example.test", loaded.LogUploadEndpoint);
        Assert.Equal("windows", loaded.LogUploadStreamName);
        Assert.Contains("logs.example.test", File.ReadAllText(path), StringComparison.Ordinal);
    }

    [Fact]
    public void LocalLogsRotateStayBoundedAndRedactCredentials()
    {
        var path = Path.Combine(_directory, "logs", "miruplay.jsonl");
        var logs = new RotatingLocalLogStore(path, maxActiveBytes: 1_024, maxRotatedBytes: 1_024);
        logs.Write("error", "prefix password = super-secret token=opaque-secret https://user:pass@example.test/private suffix");
        var redacted = Assert.Single(logs.ReadRecent(1).Records).Message;
        Assert.DoesNotContain("super-secret", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("opaque-secret", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("user:pass@", redacted, StringComparison.Ordinal);
        for (var index = 0; index < 100; index++) logs.Write("info", $"record-{index:D3}-{new string('x', 40)}");

        var exported = logs.ExportJsonLines(maxBytes: 4_096);
        Assert.DoesNotContain("super-secret", exported, StringComparison.Ordinal);
        Assert.DoesNotContain("opaque-secret", exported, StringComparison.Ordinal);
        Assert.DoesNotContain("user:pass@", exported, StringComparison.Ordinal);
        Assert.All(Directory.EnumerateFiles(Path.GetDirectoryName(path)!), file =>
            Assert.True(new FileInfo(file).Length <= 1_024));
        Assert.NotEmpty(logs.ReadRecent(1).Records);
    }

    [Fact]
    public async Task OpenObserveUploadUsesDpapiTokenAndRemovesUploadedBatch()
    {
        var logs = new RotatingLocalLogStore(Path.Combine(_directory, "logs.jsonl"));
        logs.Write("info", "hello");
        var tokens = new OpenObserveTokenStore(Path.Combine(_directory, "openobserve.bin"));
        tokens.Save("user:password-token");
        string? requestBody = null;
        string? authorization = null;
        using var client = new HttpClient(new DelegateHandler(request =>
        {
            authorization = request.Headers.Authorization?.ToString();
            requestBody = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK);
        }));
        var service = new OpenObserveLogService(logs, tokens, client);
        var result = await service.UploadAsync(new AppSettings(
            LogUploadEnabled: true,
            LogUploadEndpoint: "https://logs.example.test",
            LogUploadStreamName: "windows"));

        Assert.True(result.Succeeded);
        Assert.Equal(1, result.UploadedCount);
        Assert.StartsWith("Basic ", authorization, StringComparison.Ordinal);
        Assert.DoesNotContain("password-token", requestBody, StringComparison.Ordinal);
        Assert.Empty(logs.ReadPending());
        Assert.Throws<ArgumentException>(() => OpenObserveLogService.NormalizeEndpoint(
            "https://user:secret@logs.example.test/?token=bad", "windows"));
        Assert.Equal(
            "https://logs.example.test/api/acme/windows/_json",
            OpenObserveLogService.NormalizeEndpoint("https://logs.example.test/api/acme/v1/logs", "windows"));
        Assert.Equal(
            "https://logs.example.test/api/default/windows/_json",
            OpenObserveLogService.NormalizeEndpoint("https://logs.example.test/v1/logs", "windows"));
    }

    [Fact]
    public async Task UpdaterChecksManifestAndEnforcesDeclaredDownloadSize()
    {
        var manifest = """
            {"versionName":"2.0.0","versionCode":2,"releaseName":"v2","tagName":"v2","publishedAt":"2026-01-01","releaseUrl":"https://example.test/release","assetName":"MiruPlay.exe","assetSizeBytes":4,"downloadUrl":"https://example.test/MiruPlay.exe"}
            """;
        using var client = new HttpClient(new DelegateHandler(request =>
        {
            if (request.RequestUri?.AbsolutePath.EndsWith("MiruPlay.exe", StringComparison.Ordinal) == true)
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent("good"u8.ToArray()) };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(manifest, Encoding.UTF8, "application/json") };
        }));
        var updater = new WindowsAppUpdater(
            "https://example.test/manifest.json",
            client,
            Path.Combine(_directory, "updates"),
            currentVersionName: "1.0.0",
            currentVersionCode: 1);

        var checkedStatus = await updater.CheckAsync();
        Assert.True(checkedStatus.UpdateAvailable);
        var downloaded = await updater.DownloadAsync();
        Assert.Equal(Path.Combine(_directory, "updates", "MiruPlay.exe"), downloaded.StagedInstallerPath);
        Assert.Equal("good", await File.ReadAllTextAsync(downloaded.StagedInstallerPath!));
    }

    [Fact]
    public async Task HeadlessTasksAreExclusiveAndCancelable()
    {
        await using var scheduler = new HeadlessTaskScheduler();
        using var started = new ManualResetEventSlim();
        await using var release = new AsyncManualResetEvent();
        Assert.True(scheduler.TryStart("scan", "Scan", async cancellationToken =>
        {
            started.Set();
            await release.WaitAsync(cancellationToken);
        }));
        Assert.True(started.Wait(TimeSpan.FromSeconds(2)));
        Assert.False(scheduler.TryStart("scan", "Scan", _ => Task.CompletedTask));
        Assert.True(scheduler.Cancel("scan"));
        var status = await WaitForStatus(scheduler, "scan");
        Assert.Equal("CANCELED", status.State);
    }

    private static async Task<HeadlessTaskStatus> WaitForStatus(HeadlessTaskScheduler scheduler, string id)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var status = scheduler.Get(id);
            if (status is not null && status.State != "RUNNING") return status;
            await Task.Delay(10);
        }
        throw new TimeoutException();
    }

    private sealed class DelegateHandler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(send(request));
    }

    private sealed class AsyncManualResetEvent : IAsyncDisposable
    {
        private readonly TaskCompletionSource<bool> _source = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<bool> WaitAsync(CancellationToken cancellationToken) => _source.Task.WaitAsync(cancellationToken);
        public ValueTask DisposeAsync()
        {
            _source.TrySetResult(true);
            return ValueTask.CompletedTask;
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
