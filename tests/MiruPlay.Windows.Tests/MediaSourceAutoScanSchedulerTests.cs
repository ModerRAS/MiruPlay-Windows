using MiruPlay.Windows.Services;

namespace MiruPlay.Windows.Tests;

public sealed class MediaSourceAutoScanSchedulerTests
{
    [Fact]
    public async Task ScansEachSourceOnlyWhenItsIntervalIsDue()
    {
        var settings = new AppSettings(AutoScanEnabled: true, AutoScanIntervalHours: 1);
        IReadOnlyList<MediaSourceInfoDto> sources =
        [
            Source(1, lastScanned: 1_000),
            Source(2, lastScanned: 0),
        ];
        var calls = new List<long>();
        await using var scheduler = new MediaSourceAutoScanScheduler(
            () => settings,
            () => sources,
            (id, _) =>
            {
                calls.Add(id);
                return Task.FromResult(new SourceScanResponse(id, $"Source {id}", 1, 0, 0));
            },
            TimeProvider.System,
            TimeSpan.FromMinutes(1));

        Assert.Equal(1, await scheduler.RunIfDueAsync(3_600_999));
        Assert.Equal([2L], calls);
        Assert.Equal(1, await scheduler.RunIfDueAsync(3_601_000));
        Assert.Equal([2L, 1L], calls);
        Assert.Equal(0, await scheduler.RunIfDueAsync(3_601_001));

        settings = settings with { AutoScanEnabled = false };
        Assert.Equal(0, await scheduler.RunIfDueAsync(7_201_000));
    }

    [Fact]
    public async Task FailedAttemptIsThrottledUntilTheNextInterval()
    {
        var calls = 0;
        await using var scheduler = new MediaSourceAutoScanScheduler(
            () => new AppSettings(AutoScanEnabled: true, AutoScanIntervalHours: 1),
            () => [Source(1, lastScanned: 0)],
            (_, _) =>
            {
                calls++;
                throw new IOException("offline");
            },
            TimeProvider.System,
            TimeSpan.FromMinutes(1));

        Assert.Equal(1, await scheduler.RunIfDueAsync(1_000));
        Assert.Equal(0, await scheduler.RunIfDueAsync(1_001));
        Assert.Equal(1, await scheduler.RunIfDueAsync(3_601_000));
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(6, 6)]
    [InlineData(12, 12)]
    [InlineData(24, 24)]
    [InlineData(0, 6)]
    [InlineData(2, 6)]
    public void IntervalOptionsMatchAndroidAndInvalidValuesUseDefault(int value, int expected) =>
        Assert.Equal(expected, MediaSourceAutoScanScheduler.NormalizeIntervalHours(value));

    private static MediaSourceInfoDto Source(long id, long lastScanned) => new(
        id,
        $"Source {id}",
        "LOCAL",
        "ANIME",
        new Dictionary<string, string>(),
        true,
        lastScanned);
}
