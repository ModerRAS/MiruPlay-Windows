using System.Text.Json;
using System.Xml.Linq;

namespace MiruPlay.Windows.Tests;

/// <summary>
/// Guards the i18n pipeline: generated resx satellites must contain exactly
/// the keys of tools/i18n/strings.json with identical values. Run
/// tools/i18n/build.js after editing the table; this test fails otherwise.
/// </summary>
public sealed class I18nParityTests
{
    private static readonly string[] Locales = ["zh-CN", "zh-TW", "zh-HK", "en-US", "en-GB"];

    [Fact]
    public void GeneratedResxMatchesSourceTable()
    {
        var root = FindRepoRoot();
        var table = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "tools", "i18n", "strings.json")));
        var expected = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        foreach (var entry in table.RootElement.EnumerateObject())
        {
            if (entry.Name.StartsWith('_')) continue;
            expected[entry.Name] = entry.Value.EnumerateObject()
                .ToDictionary(item => item.Name, item => item.Value.GetString() ?? "");
        }
        Assert.NotEmpty(expected);

        var resources = Path.Combine(root, "src", "MiruPlay.Windows", "Resources");
        var master = ReadResx(Path.Combine(resources, "Strings.resx"));
        Assert.Equal(expected.Keys.OrderBy(key => key), master.Keys.OrderBy(key => key).ToList());
        foreach (var (key, values) in expected)
            Assert.Equal(values["zh-CN"], master[key]);
        foreach (var locale in Locales.Where(locale => locale != "zh-CN"))
        {
            var satellite = ReadResx(Path.Combine(resources, $"Strings.{locale}.resx"));
            Assert.Equal(expected.Keys.OrderBy(key => key), satellite.Keys.OrderBy(key => key).ToList());
            foreach (var (key, values) in expected)
                Assert.Equal(values[locale], satellite[key]);
        }
    }

    [Fact]
    public void AppStringsFallsBackToKeyForUnknownKeys()
    {
        Assert.Equal("i18n.missing.key", Services.AppStrings.Get("i18n.missing.key"));
    }

    private static Dictionary<string, string> ReadResx(string path)
    {
        Assert.True(File.Exists(path), $"missing {path}; run tools/i18n/build.js");
        return XDocument.Load(path).Root!.Elements("data")
            .ToDictionary(
                element => element.Attribute("name")!.Value,
                element => element.Element("value")!.Value,
                StringComparer.Ordinal);
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "tools", "i18n", "strings.json")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("repo root with tools/i18n/strings.json not found");
    }
}
