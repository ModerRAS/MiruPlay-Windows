using System.Net;
using System.Text;
using MiruPlay.Windows.Models;
using MiruPlay.Windows.Services;

namespace MiruPlay.Windows.Tests;

public sealed class AirRadarScheduleTests
{
    private const string ScheduleJson = """
    {
      "schemaVersion": 1,
      "generatedAt": "2026-11-19T12:00:00Z",
      "season": "2026-fall",
      "seasonStart": "2026-10-01",
      "seasonEnd": "2026-12-31",
      "timezone": "Asia/Tokyo",
      "itemCount": 4,
      "items": [
        {"subjectId":101,"name":"Monday Show","nameCn":"周一新番","airDate":"2026-11-16","image":"https://img.example/101.jpg","weekday":1,"time":"23:00","timezone":"Asia/Tokyo","confidence":"high","sources":[]},
        {"subjectId":102,"name":"Cross Day","nameCn":null,"airDate":"2026-11-17","image":null,"weekday":2,"time":"00:30","timezone":"Asia/Tokyo","confidence":"medium","sources":[]},
        {"subjectId":103,"name":"No Time","nameCn":null,"airDate":"2026-11-18","image":null,"weekday":3,"time":null,"timezone":"Asia/Tokyo","confidence":"unknown","sources":[]},
        {"subjectId":104,"name":"Early Monday","nameCn":null,"airDate":"2026-11-16","image":null,"weekday":1,"time":"00:30","timezone":"Asia/Tokyo","confidence":"low","sources":[]}
      ]
    }
    """;

    [Fact]
    public void ParsesApiContractAndIgnoresUnknownFields()
    {
        var data = AirRadarScheduleService.Parse(ScheduleJson);
        Assert.Equal(1, data.SchemaVersion);
        Assert.Equal("2026-fall", data.Season);
        Assert.Equal("Asia/Tokyo", data.Timezone);
        Assert.Equal(4, data.Items.Count);
        Assert.Equal("周一新番", data.Items[0].DisplayName());
        Assert.Equal(102, data.Items[1].SubjectId);
        Assert.Equal(1, data.Items[0].Weekday);
        Assert.Equal("23:00", data.Items[0].Time);
    }

    [Fact]
    public void RejectsUnsupportedSchema()
    {
        const string json = """{"schemaVersion":2,"items":[]}""";
        Assert.Throws<InvalidDataException>(() => AirRadarScheduleService.Parse(json));
    }

    [Fact]
    public void ConvertsJstToLocalAndHandlesCrossDay()
    {
        var data = AirRadarScheduleService.Parse(ScheduleJson);
        var utcMinus8 = TimeZoneInfo.CreateCustomTimeZone("TestUTC-8", TimeSpan.FromHours(-8), "Test UTC-8", "Test UTC-8");
        var days = AirRadarScheduleService.BuildWeek(data, utcMinus8);
        var monday = days[0];
        Assert.Equal(DayOfWeek.Monday, monday.Weekday);
        // JST 23:00 Monday -> local Monday 06:00; JST 00:30 Tuesday -> local Monday 07:30.
        Assert.Equal([101, 102], monday.Entries.Take(2).Select(entry => entry.Item.SubjectId).ToArray());
        Assert.Equal("06:00", monday.Entries[0].TimeText);
        Assert.Equal("07:30", monday.Entries[1].TimeText);
        // JST Monday 00:30 -> local Sunday 07:30 (cross-day).
        var sunday = days[6];
        Assert.Contains(sunday.Entries, entry => entry.Item.SubjectId == 104);
        Assert.DoesNotContain(monday.Entries, entry => entry.Item.SubjectId == 104);
        // Entries without a time are shown without a clock and sort after timed entries in their local day.
        var noTime = days.SelectMany(day => day.Entries).Single(entry => entry.Item.SubjectId == 103);
        Assert.Equal(string.Empty, noTime.TimeText);
        Assert.Same(noTime, days.SelectMany(day => day.Entries).Last(entry => entry.Item.SubjectId == 103));
    }

    [Fact]
    public void MatchesLocalLibraryByBangumiSubjectId()
    {
        var item = AirRadarScheduleService.Parse(ScheduleJson).Items[0];
        var matched = new LibrarySeries(1, "u", "t", null, "", 2026, null, [], null, [], [])
        {
            ExternalIds = [new ExternalMetadataId("Bangumi", "101")],
        };
        var other = new LibrarySeries(2, "u2", "t2", null, "", 2026, null, [], null, [], []);
        Assert.Same(matched, AirRadarScheduleService.MatchLibrary([other, matched], item));
        Assert.Null(AirRadarScheduleService.MatchLibrary([other], item));
    }

    [Fact]
    public async Task FreshCacheSkipsNetworkAndStaleCacheFallsBack()
    {
        var directory = Path.Combine(Path.GetTempPath(), "miruplay-airradar-" + Guid.NewGuid().ToString("N"));
        var now = new DateTimeOffset(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);
        try
        {
            var handler = new StubHandler(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ScheduleJson, Encoding.UTF8, "application/json"),
            });
            using (var service = new AirRadarScheduleService(() => "https://schedule.example", handler, directory, () => now))
            {
                var first = await service.LoadAsync();
                Assert.NotNull(first.Data);
                Assert.False(first.Stale);
                Assert.Equal(1, handler.Calls);
            }
            var failing = new StubHandler(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            using (var service = new AirRadarScheduleService(() => "https://schedule.example", failing, directory, () => now.AddMinutes(50)))
            {
                var cached = await service.LoadAsync();
                Assert.NotNull(cached.Data);
                Assert.False(cached.Stale);
                Assert.Equal(0, failing.Calls);
            }
            using (var service = new AirRadarScheduleService(() => "https://schedule.example", failing, directory, () => now.AddHours(2)))
            {
                var stale = await service.LoadAsync();
                Assert.NotNull(stale.Data);
                Assert.True(stale.Stale);
                Assert.Equal(1, failing.Calls);
            }
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task NotFoundReportsNoScheduleDataWithoutCache()
    {
        var directory = Path.Combine(Path.GetTempPath(), "miruplay-airradar-" + Guid.NewGuid().ToString("N"));
        try
        {
            var handler = new StubHandler(new HttpResponseMessage(HttpStatusCode.NotFound));
            using var service = new AirRadarScheduleService(() => "https://schedule.example", handler, directory, () => DateTimeOffset.UtcNow);
            var result = await service.LoadAsync();
            Assert.Null(result.Data);
            Assert.Equal("no_schedule_data", result.Error);
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task EmptyUrlDoesNotCallNetwork()
    {
        var handler = new StubHandler(new HttpResponseMessage(HttpStatusCode.OK));
        using var service = new AirRadarScheduleService(() => "  ", handler, Path.GetTempPath(), () => DateTimeOffset.UtcNow);
        var result = await service.LoadAsync();
        Assert.Null(result.Data);
        Assert.Equal("not_configured", result.Error);
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData(" https://schedule.example ", "https://schedule.example")]
    [InlineData("http://schedule.example/base", "http://schedule.example/base")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData("ftp://schedule.example", "")]
    [InlineData("schedule.example", "")]
    [InlineData("javascript:alert(1)", "")]
    [InlineData("https://schedule.example/?a=1", "")]
    [InlineData("https://user:pass@schedule.example", "")]
    public void SettingsUrlNormalizationTrimsAndValidates(string input, string expected) =>
        Assert.Equal(expected, AppSettingsStore.NormalizeScheduleDataSourceUrl(input));

    private sealed class StubHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(response);
        }
    }
}
