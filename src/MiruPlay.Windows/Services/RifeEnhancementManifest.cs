using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MiruPlay.Windows.Services;

public sealed record RifeEnhancementArchive(
    string Url,
    string Sha256,
    long Size,
    string Format = "zip",
    long UnpackedSize = 0,
    int FileCount = 0);

public sealed record RifeEnhancementFile(string Path, string Sha256, long Size);

public sealed record RifeEnhancementModel(string Id, int Index, bool Default, string Path);

public sealed record RifeEnhancementLicense(string Component, string License, string File);

/// <summary>Pinned source manifest for the optional RIFE enhancement pack. It is embedded in
/// the application from tools/enhance/rife-enhance-manifest.json; the same file is the release
/// pin consumed by tools/Get-EnhancementPack.ps1 (tvmode's full-edition packaging).</summary>
public sealed record RifeEnhancementManifest(
    int SchemaVersion,
    string Id,
    string Version,
    string MinAppVersion,
    RifeEnhancementArchive SourceArchive,
    IReadOnlyList<RifeEnhancementFile> Files,
    IReadOnlyList<RifeEnhancementModel>? Models = null,
    IReadOnlyList<RifeEnhancementLicense>? Licenses = null)
{
    public const string EmbeddedResourceName = "MiruPlay.Windows.rife-enhance-manifest.json";
    public const string PackId = "rife-vulkan";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private static readonly Regex Sha256Pattern = new("^[0-9a-f]{64}$", RegexOptions.Compiled);

    public static RifeEnhancementManifest LoadEmbedded()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(EmbeddedResourceName)
            ?? throw new InvalidOperationException("Embedded RIFE enhancement manifest is missing.");
        using var reader = new StreamReader(stream);
        var manifest = JsonSerializer.Deserialize<RifeEnhancementManifest>(reader.ReadToEnd(), JsonOptions)
            ?? throw new InvalidDataException("Embedded RIFE enhancement manifest is empty.");
        manifest.Validate();
        return manifest;
    }

    public void Validate()
    {
        if (SchemaVersion != 1) throw new InvalidDataException("RIFE enhancement manifest schemaVersion must be 1.");
        if (!string.Equals(Id, PackId, StringComparison.Ordinal))
            throw new InvalidDataException($"Unexpected RIFE enhancement manifest id: {Id}");
        if (string.IsNullOrWhiteSpace(Version)) throw new InvalidDataException("RIFE enhancement manifest version is missing.");
        if (SourceArchive is null) throw new InvalidDataException("RIFE enhancement manifest sourceArchive is missing.");
        if (!SourceArchive.Url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("RIFE enhancement archive URL must use HTTPS.");
        if (!Sha256Pattern.IsMatch(SourceArchive.Sha256 ?? string.Empty))
            throw new InvalidDataException("RIFE enhancement archive SHA-256 is invalid.");
        if (SourceArchive.Size <= 0) throw new InvalidDataException("RIFE enhancement archive size is invalid.");
        if (Files is null || Files.Count == 0) throw new InvalidDataException("RIFE enhancement manifest does not list files.");
        if (SourceArchive.FileCount > 0 && SourceArchive.FileCount != Files.Count)
            throw new InvalidDataException("RIFE enhancement manifest fileCount does not match files.");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var file in Files)
        {
            if (!IsSafeRelativePath(file.Path))
                throw new InvalidDataException($"Unsafe RIFE enhancement file path: {file.Path}");
            if (!Sha256Pattern.IsMatch(file.Sha256 ?? string.Empty))
                throw new InvalidDataException($"Invalid RIFE enhancement SHA-256 for {file.Path}");
            if (file.Size < 0) throw new InvalidDataException($"Invalid RIFE enhancement size for {file.Path}");
            if (!seen.Add(file.Path)) throw new InvalidDataException($"Duplicate RIFE enhancement file: {file.Path}");
            total += file.Size;
        }
        if (SourceArchive.UnpackedSize > 0 && SourceArchive.UnpackedSize != total)
            throw new InvalidDataException("RIFE enhancement manifest unpackedSize does not match files.");
    }

    public static bool IsSafeRelativePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var normalized = path.Replace('\\', '/');
        if (normalized.StartsWith('/') || normalized.Contains(':')) return false;
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length > 0 && !segments.Any(segment => segment is "." or "..");
    }
}
