using System.Reflection;

namespace MiruPlay.Windows.Services;

/// <summary>Deployment edition embedded at publish time (lite/full).</summary>
public static class EditionInfo
{
    public const string Lite = "lite";
    public const string Full = "full";
    public static readonly string[] Editions = [Lite, Full];
    public static string Current { get; } = ResolveCurrent();

    public static string Normalize(string? value) =>
        string.Equals(value?.Trim(), Full, StringComparison.OrdinalIgnoreCase) ? Full : Lite;

    private static string ResolveCurrent()
    {
        // Publishing writes edition.txt next to the executable; it is authoritative
        // for portable/installer copies. Assembly metadata is the development fallback.
        try
        {
            var marker = Path.Combine(AppContext.BaseDirectory, "edition.txt");
            if (File.Exists(marker)) return Normalize(File.ReadAllText(marker));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // fall through to assembly metadata / lite
        }
        try
        {
            var metadata = Assembly.GetEntryAssembly()?.GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(attribute => attribute.Key.Equals("Edition", StringComparison.OrdinalIgnoreCase))
                ?.Value;
            if (!string.IsNullOrWhiteSpace(metadata)) return Normalize(metadata);
        }
        catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException))
        {
            // fall through to lite
        }
        return Lite;
    }
}