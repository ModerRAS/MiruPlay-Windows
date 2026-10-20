using System.Net;
using System.Text;
using System.Text.Json;
using MiruPlay.Windows.Services;

namespace MiruPlay.Windows.Tests;

public sealed class OperationsServicesTests : IDisposable
{
    private static readonly JsonSerializerOptions WebJsonOptions = new(JsonSerializerDefaults.Web);
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
            LogUploadStreamName: "windows",
            MergeSameAnimeEnabled: true,
            PosterWallArrangement: "RELEASE_SEASON",
            EpisodeVersionSelectionPolicy: "manual",
            AppUpdateManifestUrl: "https://updates.example.test/manifest.json"));

        var loaded = store.Load();
        Assert.True(loaded.AutoScanEnabled);
        Assert.True(loaded.MergeSameAnimeEnabled);
        Assert.Equal("RELEASE_SEASON", loaded.PosterWallArrangement);
        Assert.Equal("manual", loaded.EpisodeVersionSelectionPolicy);
        Assert.Equal("https://updates.example.test/manifest.json", loaded.AppUpdateManifestUrl);
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
    public async Task OpenObserveSchedulerTracksOptInUploadAndPersistsItsResult()
    {
        var logs = new RotatingLocalLogStore(Path.Combine(_directory, "scheduled-logs.jsonl"));
        logs.Write("INFO", "scheduled test record");
        var tokens = new OpenObserveTokenStore(Path.Combine(_directory, "scheduled-token.bin"));
        tokens.Save("scheduled-token");
        using var client = new HttpClient(new DelegateHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)));
        var upload = new OpenObserveLogService(logs, tokens, client);
        await using var tasks = new HeadlessTaskScheduler(storePath: Path.Combine(_directory, "scheduled-tasks.json"));
        var settings = new AppSettings(LogUploadEnabled: true, LogUploadEndpoint: "https://logs.example.test", LogUploadStreamName: "windows");
        await using var scheduler = new OpenObserveLogScheduler(
            () => settings,
            update => settings = update(settings),
            upload,
            tasks);

        Assert.True(await scheduler.RunIfDueAsync(10_000_000));
        Assert.Equal("SUCCEEDED", tasks.Get("openobserve-log-upload")?.State);
        Assert.True(settings.LastLogUploadAt > 0);
        Assert.Empty(logs.ReadPending());
    }

    [Fact]
    public async Task UpdaterChecksManifestAndEnforcesDeclaredDownloadSize()
    {
        var manifest = """
            {"versionName":"2.0.0.0","versionCode":null,"releaseName":"MiruPlay 2.0.0","tagName":"v2.0.0","publishedAt":"2026-01-01T00:00:00Z","releaseUrl":"https://example.test/release","assetName":"MiruPlay-2.0.0-win-x64-setup.exe","assetSizeBytes":4,"downloadUrl":"https://example.test/MiruPlay-2.0.0-win-x64-setup.exe","sha256":"770e607624d689265ca6c44884d0807d9b054d23c473c106c72be9de08b7376c"}
            """;
        using var client = new HttpClient(new DelegateHandler(request =>
        {
            if (request.RequestUri?.AbsolutePath.EndsWith("-setup.exe", StringComparison.Ordinal) == true)
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent("good"u8.ToArray()) };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(manifest, Encoding.UTF8, "application/json") };
        }));
        var updater = new WindowsAppUpdater(
            "https://example.test/manifest.json",
            client,
            Path.Combine(_directory, "updates"),
            currentVersionName: "1.0.0.0",
            currentVersionCode: 0);

        var checkedStatus = await updater.CheckAsync();
        Assert.True(checkedStatus.UpdateAvailable);
        var downloaded = await updater.DownloadAsync();
        Assert.Equal(Path.Combine(_directory, "updates", "MiruPlay-2.0.0-win-x64-setup.exe"), downloaded.StagedInstallerPath);
        Assert.Equal("good", await File.ReadAllTextAsync(downloaded.StagedInstallerPath!));
        var handoff = updater.BuildInstallerHandoffScript(Path.Combine(_directory, "MiruPlay.exe"), 12345);
        Assert.Contains("Wait-Process -Id 12345", handoff, StringComparison.Ordinal);
        Assert.Contains("/VERYSILENT", handoff, StringComparison.Ordinal);
        Assert.Contains("MiruPlay-2.0.0-win-x64-setup.exe", handoff, StringComparison.Ordinal);
        var backgroundHandoff = updater.BuildInstallerHandoffScript(Path.Combine(_directory, "MiruPlay.exe"), 12345, backgroundMode: true);
        Assert.Contains("-ArgumentList '--background'", backgroundHandoff, StringComparison.Ordinal);
        await File.WriteAllTextAsync(downloaded.StagedInstallerPath!, "evil");
        Assert.Throws<InvalidDataException>(() => updater.BuildInstallerHandoffScript(Path.Combine(_directory, "MiruPlay.exe"), 12345));
    }

    [Fact]
    public async Task UpdaterRejectsChecksumMismatchWithoutStagingTheFile()
    {
        var manifest = """{"versionName":"2.0.0","assetName":"MiruPlay.exe","assetSizeBytes":4,"downloadUrl":"https://example.test/MiruPlay.exe","sha256":"0000000000000000000000000000000000000000000000000000000000000000"}""";
        using var client = new HttpClient(new DelegateHandler(request => request.RequestUri?.AbsolutePath.EndsWith("MiruPlay.exe", StringComparison.Ordinal) == true
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent("good"u8.ToArray()) }
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(manifest, Encoding.UTF8, "application/json") }));
        var updater = new WindowsAppUpdater("https://example.test/manifest.json", client, Path.Combine(_directory, "bad-updates"), "1.0.0", 1);
        Assert.True((await updater.CheckAsync()).UpdateAvailable);
        var status = await updater.DownloadAsync();
        Assert.Contains("SHA-256", status.LastError, StringComparison.Ordinal);
        Assert.Null(status.StagedInstallerPath);
    }

    [Fact]
    public async Task InterruptedTasksAreMarkedFailedWhenThePersistentStoreIsOpened()
    {
        var path = Path.Combine(_directory, "interrupted-tasks.json");
        await File.WriteAllTextAsync(path, """[{"id":"interrupted","title":"Scan","state":"RUNNING","startedAt":1}]""");
        await using var scheduler = new HeadlessTaskScheduler(storePath: path);
        var task = scheduler.Get("interrupted");
        Assert.Equal("FAILED", task?.State);
        Assert.Equal("应用关闭时任务中断。", task?.Error);
        Assert.NotNull(task?.FinishedAt);
    }

    [Fact]
    public async Task HeadlessTasksAreExclusiveAndCancelable()
    {
        var path = Path.Combine(_directory, "tasks.json");
        await using var scheduler = new HeadlessTaskScheduler(storePath: path);
        await using var release = new AsyncManualResetEvent();
        using var running = new ManualResetEventSlim();
        var tracked = scheduler.RunTrackedAsync("scan", "Scan", async cancellationToken =>
        {
            running.Set();
            await release.WaitAsync(cancellationToken);
        });
        Assert.True(running.Wait(TimeSpan.FromSeconds(2)));
        Assert.False(scheduler.TryStart("scan", "Scan", _ => Task.CompletedTask));
        Assert.True(scheduler.Cancel("scan"));
        Assert.False(await tracked);
        Assert.Equal("CANCELED", scheduler.Get("scan")?.State);
        var persisted = JsonSerializer.Deserialize<HeadlessTaskStatus[]>(await File.ReadAllTextAsync(path), WebJsonOptions);
        Assert.Equal("CANCELED", Assert.Single(persisted!).State);
        await using var restored = new HeadlessTaskScheduler(storePath: path);
        Assert.Equal("CANCELED", restored.Get("scan")?.State);
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
