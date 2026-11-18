using System.Net;
using System.Text;
using MiruPlay.Windows.Models;
using MiruPlay.Windows.Tv;

namespace MiruPlay.Windows.Tests;

public sealed class TvBangumiCalendarTests
{
    private const string CalendarJson = """
    [
      {"weekday":{"en":"Mon","cn":"星期一","ja":"月曜日","id":1},"items":[
        {"id":101,"name":"Monday Show","name_cn":"周一新番","summary":"s","air_date":"2026-01-05","air_weekday":1,"images":{"large":"https://img.example/101.jpg"},"rating":{"score":8.1}},
        {"id":102,"name":"Future Show","name_cn":null,"summary":null,"air_date":"2026-01-20","air_weekday":1,"images":null,"rating":{"score":0}}
      ]},
      {"weekday":{"en":"Tue","cn":"星期二","ja":"火曜日","id":2},"items":[
        {"id":201,"name":"Tuesday Show","name_cn":"周二新番","summary":null,"air_date":"2026-01-06","air_weekday":2,"images":null,"rating":{"score":7.0}}
      ]},
      {"weekday":{"en":"Wed","cn":"星期三","ja":"水曜日","id":3},"items":[]},
      {"weekday":{"en":"Thu","cn":"星期四","ja":"木曜日","id":4},"items":[]},
      {"weekday":{"en":"Fri","cn":"星期五","ja":"金曜日","id":5},"items":[]},
      {"weekday":{"en":"Sat","cn":"星期六","ja":"土曜日","id":6},"items":[]},
      {"weekday":{"en":"Sun","cn":"星期日","ja":"日曜日","id":7},"items":[]}
    ]
    """;

    private static TvBangumiCalendarData Data => TvBangumiCalendarService.ParseCalendar(CalendarJson);

    [Fact]
    public void ParsesCalendarDaysItemsAndFallbackNames()
    {
        var data = Data;
        Assert.Equal(7, data.Days.Count);
        Assert.Equal(TvAirWeekday.Monday, data.Days[0].Weekday);
        Assert.Equal(TvAirWeekday.Sunday, data.Days[6].Weekday);
        Assert.Equal("周一新番", data.Days[0].Items[0].DisplayName);
        Assert.Equal("Future Show", data.Days[0].Items[1].DisplayName);
        Assert.Equal("https://img.example/101.jpg", data.Days[0].Items[0].ImageUrl);
        Assert.Equal(8.1, data.Days[0].Items[0].Rating, 3);
    }

    [Theory]
    [InlineData("2026-01-07", "2026-01-05")]
    [InlineData("2026-01-05", "2026-01-05")]
    [InlineData("2026-01-11", "2026-01-05")]
    [InlineData("2026-01-01", "2025-12-29")]
    public void WeekStartIsMonday(string today, string expected) =>
        Assert.Equal(
            DateOnly.Parse(expected, System.Globalization.CultureInfo.InvariantCulture),
            TvBangumiCalendarService.WeekStartFor(DateOnly.Parse(today, System.Globalization.CultureInfo.InvariantCulture)));

    [Fact]
    public void WeekOffsetsCrossYearBoundary()
    {
        var today = new DateOnly(2026, 1, 1); // Thursday
        var lastWeek = TvBangumiCalendarService.BuildWeek(Data, today, -1);
        var thisWeek = TvBangumiCalendarService.BuildWeek(Data, today, 0);
        var nextWeek = TvBangumiCalendarService.BuildWeek(Data, today, 1);
        Assert.Equal(new DateOnly(2025, 12, 22), lastWeek[0].Date);
        Assert.Equal(new DateOnly(2025, 12, 29), thisWeek[0].Date);
        Assert.Equal(new DateOnly(2026, 1, 5), nextWeek[0].Date);
        Assert.Equal(7, thisWeek.Count);
    }

    [Fact]
    public void FiltersItemsThatHaveNotAiredByWeekEnd()
    {
        var today = new DateOnly(2026, 1, 7);
        var thisWeek = TvBangumiCalendarService.BuildWeek(Data, today, 0);
        var monday = thisWeek[0];
        Assert.Single(monday.Entries);
        Assert.Equal(101, monday.Entries[0].Item.Id);

        var nextWeek = TvBangumiCalendarService.BuildWeek(Data, today, 1);
        var nextMonday = nextWeek[0].Entries;
        Assert.Single(nextMonday); // already-airing title remains
        Assert.Equal(101, nextMonday[0].Item.Id);
        Assert.DoesNotContain(nextMonday, entry => entry.Item.Id == 102); // 2026-01-20 is still beyond next week's end
    }

    [Fact]
    public void MarksTodayOnlyInCurrentWeek()
    {
        var today = new DateOnly(2026, 1, 7); // Wednesday
        var week = TvBangumiCalendarService.BuildWeek(Data, today, 0);
        Assert.True(week[2].IsToday);
        Assert.All(week.Where((_, index) => index != 2), section => Assert.False(section.IsToday));
        Assert.All(TvBangumiCalendarService.BuildWeek(Data, today, -1), section => Assert.False(section.IsToday));
    }

    [Fact]
    public void MatchesLocalSeriesByBangumiId()
    {
        var item = Data.Days[0].Items[0];
        var matched = new LibrarySeries(1, "u", "t", null, "", 2026, null, [], null, [], [])
        {
            ExternalIds = [new ExternalMetadataId("Bangumi", "101")],
        };
        var other = new LibrarySeries(2, "u2", "t2", null, "", 2026, null, [], null, [], []);
        Assert.Same(matched, TvBangumiCalendarService.MatchLibrary([other, matched], item));
        Assert.Null(TvBangumiCalendarService.MatchLibrary([other], item));
    }

    [Fact]
    public async Task FreshCacheSkipsNetworkAndStaleCacheFallsBack()
    {
        var directory = Path.Combine(Path.GetTempPath(), "miruplay-calendar-" + Guid.NewGuid().ToString("N"));
        var cachePath = Path.Combine(directory, "calendar.json");
        var now = new DateTimeOffset(2026, 1, 7, 12, 0, 0, TimeSpan.Zero);
        try
        {
            var handler = new StubHandler(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(CalendarJson, Encoding.UTF8, "application/json"),
            });
            using (var service = new TvBangumiCalendarService(handler, cachePath, () => now))
            {
                var first = await service.LoadAsync();
                Assert.NotNull(first.Data);
                Assert.False(first.Stale);
                Assert.Equal(1, handler.Calls);
            }

            var failing = new StubHandler(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            using (var service = new TvBangumiCalendarService(failing, cachePath, () => now.AddMinutes(30)))
            {
                var cached = await service.LoadAsync();
                Assert.NotNull(cached.Data);
                Assert.False(cached.Stale);
                Assert.Equal(0, failing.Calls);
            }

            using (var service = new TvBangumiCalendarService(failing, cachePath, () => now.AddHours(2)))
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
    public async Task NetworkFailureWithoutCacheReportsError()
    {
        var directory = Path.Combine(Path.GetTempPath(), "miruplay-calendar-" + Guid.NewGuid().ToString("N"));
        var cachePath = Path.Combine(directory, "calendar.json");
        try
        {
            var handler = new StubHandler(new HttpResponseMessage(HttpStatusCode.BadGateway));
            using var service = new TvBangumiCalendarService(handler, cachePath, () => DateTimeOffset.UtcNow);
            var result = await service.LoadAsync();
            Assert.Null(result.Data);
            Assert.NotNull(result.Error);
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
        }
    }

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