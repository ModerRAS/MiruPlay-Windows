using MiruPlay.Windows.Services;

namespace MiruPlay.Windows.Tests;

public sealed class AutoUpdateSchedulerTests
{
    private sealed class FakeChecker : IUpdateChecker
    {
        public bool IsSupported { get; init; } = true;
        public WindowsUpdateStatus Status { get; private set; } = new(true, "1.0.0", 1);
        public int CheckCount { get; private set; }
        public int DownloadCount { get; private set; }
        public bool UpdateAvailable { get; init; }
        public bool FailCheck { get; init; }

        public Task<WindowsUpdateStatus> CheckAsync(CancellationToken cancellationToken = default)
        {
            CheckCount++;
            if (FailCheck) throw new HttpRequestException("offline");
            Status = Status with
            {
                Latest = UpdateAvailable ? new WindowsUpdateInfo("1.1.0", 2, "1.1.0", "v1.1.0", "", "", "MiruPlay-1.1.0-setup.exe", 10, "https://example/setup.exe", new string('a', 64)) : null,
                UpdateAvailable = UpdateAvailable,
                LastCheckedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            };
            return Task.FromResult(Status);
        }

        public Task<WindowsUpdateStatus> DownloadAsync(CancellationToken cancellationToken = default)
        {
            DownloadCount++;
            Status = Status with { StagedInstallerPath = Path.Combine(Path.GetTempPath(), "MiruPlay-1.1.0-setup.exe") };
            return Task.FromResult(Status);
        }
    }

    private static AppSettings Settings(string mode = "download", int hours = 6) =>
        new(AutoUpdateMode: mode, AutoUpdateIntervalHours: hours);

    [Theory]
    [InlineData(null, "download")]
    [InlineData("", "download")]
    [InlineData("OFF", "off")]
    [InlineData("check", "check")]
    [InlineData("download", "download")]
    [InlineData("install", "install")]
    [InlineData("bogus", "download")]
    public void NormalizesMode(string? input, string expected) =>
        Assert.Equal(expected, AutoUpdateScheduler.NormalizeMode(input));

    [Theory]
    [InlineData("off", false)]
    [InlineData("check", true)]
    [InlineData("download", true)]
    [InlineData("install", true)]
    public void EnabledExceptOff(string mode, bool expected) =>
        Assert.Equal(expected, AutoUpdateScheduler.IsEnabled(mode));

    [Theory]
    [InlineData("off", 0)]
    [InlineData("check", 1)]
    [InlineData("download", 2)]
    [InlineData("install", 3)]
    public void ModeRankOrdering(string mode, int expected) =>
        Assert.Equal(expected, AutoUpdateScheduler.ModeRank(mode));

    [Fact]
    public async Task OffModeNeverChecks()
    {
        var checker = new FakeChecker();
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        using var scheduler = new AutoUpdateScheduler(() => Settings("off"), checker, clock: () => now);
        await scheduler.TickAsync();
        now = now.AddHours(2);
        await scheduler.TickAsync();
        Assert.Equal(0, checker.CheckCount);
    }

    [Fact]
    public async Task WaitsForStartupDelayThenChecks()
    {
        var checker = new FakeChecker();
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        using var scheduler = new AutoUpdateScheduler(() => Settings("check"), checker, clock: () => now, startupDelay: TimeSpan.FromSeconds(90));
        await scheduler.TickAsync();
        Assert.Equal(0, checker.CheckCount);
        now = now.AddSeconds(89);
        await scheduler.TickAsync();
        Assert.Equal(0, checker.CheckCount);
        now = now.AddSeconds(2);
        await scheduler.TickAsync();
        Assert.Equal(1, checker.CheckCount);
    }

    [Fact]
    public async Task CheckModeDoesNotDownload()
    {
        var checker = new FakeChecker { UpdateAvailable = true };
        var now = DateTimeOffset.UtcNow;
        using var scheduler = new AutoUpdateScheduler(() => Settings("check"), checker, clock: () => now, startupDelay: TimeSpan.Zero);
        await scheduler.TickAsync();
        Assert.Equal(1, checker.CheckCount);
        Assert.Equal(0, checker.DownloadCount);
        Assert.Equal(AutoUpdateStateKind.Available, scheduler.State.Kind);
    }

    [Fact]
    public async Task DownloadModeDownloadsOnceAndBecomesReady()
    {
        var checker = new FakeChecker { UpdateAvailable = true };
        var now = DateTimeOffset.UtcNow;
        using var scheduler = new AutoUpdateScheduler(() => Settings("download"), checker, clock: () => now, startupDelay: TimeSpan.Zero);
        await scheduler.TickAsync();
        Assert.Equal(1, checker.CheckCount);
        Assert.Equal(1, checker.DownloadCount);
        Assert.Equal(AutoUpdateStateKind.Ready, scheduler.State.Kind);
        now = now.AddMinutes(1);
        await scheduler.TickAsync();
        Assert.Equal(1, checker.CheckCount);
        Assert.Equal(1, checker.DownloadCount);
    }

    [Fact]
    public async Task ChecksAgainAfterInterval()
    {
        var checker = new FakeChecker();
        var now = DateTimeOffset.UtcNow;
        using var scheduler = new AutoUpdateScheduler(() => Settings("check", hours: 6), checker, clock: () => now, startupDelay: TimeSpan.Zero);
        await scheduler.TickAsync();
        Assert.Equal(1, checker.CheckCount);
        now = now.AddHours(5);
        await scheduler.TickAsync();
        Assert.Equal(1, checker.CheckCount);
        now = now.AddHours(1);
        await scheduler.TickAsync();
        Assert.Equal(2, checker.CheckCount);
    }

    [Fact]
    public async Task FailedCheckIsReportedSilently()
    {
        var checker = new FakeChecker { FailCheck = true };
        var now = DateTimeOffset.UtcNow;
        using var scheduler = new AutoUpdateScheduler(() => Settings("download"), checker, clock: () => now, startupDelay: TimeSpan.Zero);
        await scheduler.TickAsync();
        Assert.Equal(AutoUpdateStateKind.Failed, scheduler.State.Kind);
        Assert.NotNull(scheduler.State.Error);
    }

    [Fact]
    public async Task ManualCheckWorksWhileModeIsOff()
    {
        var checker = new FakeChecker { UpdateAvailable = true };
        using var scheduler = new AutoUpdateScheduler(() => Settings("off"), checker, clock: () => DateTimeOffset.UtcNow);
        await scheduler.CheckNowAsync();
        Assert.Equal(1, checker.CheckCount);
        Assert.Equal(0, checker.DownloadCount);
    }
}
