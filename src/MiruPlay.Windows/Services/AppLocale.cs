using System.Globalization;

namespace MiruPlay.Windows.Services;

/// <summary>
/// Application language plumbing. The five supported interface locales plus
/// system follow Android's AppLanguage set. This slice persists the choice,
/// shares it across WPF/WebAPI/WebUI and applies the corresponding culture
/// for number/date formatting; full translated string tables are still open.
/// </summary>
public static class AppLocale
{
    public static IReadOnlyList<string> Supported { get; } =
        ["system", "zh-CN", "zh-TW", "zh-HK", "en-US", "en-GB"];

    public static string Normalize(string? value)
    {
        var candidate = value?.Trim();
        return candidate is not null && Supported.Contains(candidate, StringComparer.OrdinalIgnoreCase)
            ? Supported.First(item => item.Equals(candidate, StringComparison.OrdinalIgnoreCase))
            : "system";
    }

    /// <summary>Null means follow the system (leave process cultures untouched).</summary>
    public static CultureInfo? ResolveCulture(string? value)
    {
        var normalized = Normalize(value);
        return normalized == "system" ? null : CultureInfo.GetCultureInfo(normalized);
    }

    public static void Apply(string? value)
    {
        if (ResolveCulture(value) is not { } culture) return;
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        Thread.CurrentThread.CurrentCulture = culture;
        Thread.CurrentThread.CurrentUICulture = culture;
    }
}
