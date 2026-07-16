using MiruPlay.Windows.Services;

namespace MiruPlay.Windows.Tests;

public sealed class CloudDriveRssSchedulerTests
{
    [Fact]
    public async Task RunsOnlyWhenEnabledAndIntervalIsDue()
    {
        var calls = 0;
        var config = new CloudDriveAutomationConfig(Enabled: true, IntervalMinutes: 15, LastRunAt: 1_000);
        await using var scheduler = new CloudDriveRssScheduler(
            () => config,
            _ => { calls++; return Task.CompletedTask; },
            () => false,
            TimeProvider.System,
            TimeSpan.FromMinutes(1));

        Assert.False(await scheduler.RunIfDueAsync(900_999));
        Assert.True(await scheduler.RunIfDueAsync(901_000));
        Assert.False(await scheduler.RunIfDueAsync(901_001));
        Assert.True(await scheduler.RunIfDueAsync(1_801_000));
        Assert.Equal(2, calls);

        config = config with { Enabled = false };
        Assert.False(await scheduler.RunIfDueAsync(2_701_000));
    }

    [Fact]
    public async Task FailedAttemptStillObservesRetryInterval()
    {
        var calls = 0;
        await using var scheduler = new CloudDriveRssScheduler(
            () => new CloudDriveAutomationConfig(Enabled: true, IntervalMinutes: 15),
            _ => { calls++; throw new HttpRequestException("offline"); },
            () => false,
            TimeProvider.System,
            TimeSpan.FromMinutes(1));

        Assert.True(await scheduler.RunIfDueAsync(1_000));
        Assert.False(await scheduler.RunIfDueAsync(1_001));
        Assert.True(await scheduler.RunIfDueAsync(901_000));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task DoesNotCompeteWithActiveManualRun()
    {
        await using var scheduler = new CloudDriveRssScheduler(
            () => new CloudDriveAutomationConfig(Enabled: true, IntervalMinutes: 15),
            _ => throw new InvalidOperationException("must not run"),
            () => true,
            TimeProvider.System,
            TimeSpan.FromMinutes(1));

        Assert.False(await scheduler.RunIfDueAsync(1_000));
    }
}
