using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using MiruPlay.Windows.Services;

namespace MiruPlay.Windows.Tests;

public sealed class LocaleApiTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"miruplay-locale-api-{Guid.NewGuid():N}");

    public LocaleApiTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void SupportedLocalesNormalizeAndResolve()
    {
        Assert.Equal("system", AppLocale.Normalize(null));
        Assert.Equal("system", AppLocale.Normalize("xx-YY"));
        Assert.Equal("zh-TW", AppLocale.Normalize("zh-tw"));
        Assert.Equal(["system", "zh-CN", "zh-TW", "zh-HK", "en-US", "en-GB"], AppLocale.Supported);
        Assert.Null(AppLocale.ResolveCulture("system"));
        Assert.Equal("zh-CN", AppLocale.ResolveCulture("zh-CN")?.Name);
        Assert.Equal("en-GB", AppLocale.ResolveCulture("en-GB")?.Name);
    }

    [Fact]
    public async Task LocaleApiRoundTripsThroughSettings()
    {
        try
        {
        var port = ReservePort();
        var tokens = new WebControlTokenStore(Path.Combine(_directory, "token.bin"));
        var settings = new AppSettings(WebControlPort: port);
        await using var server = new WebControlServer(
            port,
            tokens,
            () => [],
            () => new PlaybackRuntimeStatus(),
            (_, _) => Task.FromResult(false),
            command => Task.FromResult(new PlaybackRuntimeStatus()),
            () => settings,
            update =>
            {
                settings = update(settings);
                return Task.FromResult(settings);
            },
            new MediaSourceActions(
                () => [],
                _ => Task.FromResult(new SourceTestResponse(true, "ok")),
                _ => throw new NotSupportedException(),
                (_, _) => throw new NotSupportedException(),
                _ => Task.CompletedTask,
                (_, _) => throw new NotSupportedException()),
            listenOnAnyIp: false);
        await server.StartAsync();

        using var client = new HttpClient { BaseAddress = new Uri($"http://localhost:{port}") };
        client.DefaultRequestHeaders.Add("X-MiruPlay-Token", tokens.AccessToken);

        var initial = await ReadData(await client.GetAsync("/api/settings/locale"));
        Assert.Equal("system", initial.GetProperty("language").GetString());
        Assert.Equal(6, initial.GetProperty("supported").GetArrayLength());

        var updated = await client.PutAsJsonAsync("/api/settings/locale", new { language = "en-US" });
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        Assert.Equal("en-US", (await ReadData(updated)).GetProperty("language").GetString());
        Assert.Equal("en-US", settings.AppLanguage);

        var normalized = await client.PutAsJsonAsync("/api/settings/locale", new { language = "xx-YY" });
        Assert.Equal(HttpStatusCode.OK, normalized.StatusCode);
        Assert.Equal("system", (await ReadData(normalized)).GetProperty("language").GetString());
        }
        finally
        {
            var culture = System.Globalization.CultureInfo.GetCultureInfo("zh-CN");
            System.Globalization.CultureInfo.DefaultThreadCurrentCulture = culture;
            System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = culture;
        }
    }

    private static async Task<JsonElement> ReadData(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        return document.RootElement.GetProperty("data").Clone();
    }

    private static int ReservePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, true); } catch (IOException) { }
    }
}
