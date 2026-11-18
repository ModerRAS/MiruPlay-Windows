using System.Text.Json;

namespace MiruPlay.Windows.Services;

public sealed record RifeSelfTestResult(
    bool Ok,
    double? OutputFps480p,
    string? Gpu,
    string? ErrorKey,
    string? Detail,
    long EvaluatedAtUtc,
    string? PackVersion,
    string? RecommendedHeight);

public sealed record RifeEnhancementState(
    string? InstalledVersion = null,
    long InstalledAtUtc = 0,
    RifeSelfTestResult? SelfTest = null);

public sealed class RifeEnhancementStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _path;

    public RifeEnhancementStateStore(string? path = null) => _path = path ?? RifeEnhancementPaths.StatePath;

    public string Path => _path;

    public RifeEnhancementState Load()
    {
        try
        {
            if (!File.Exists(_path)) return new RifeEnhancementState();
            return JsonSerializer.Deserialize<RifeEnhancementState>(File.ReadAllText(_path), JsonOptions)
                ?? new RifeEnhancementState();
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        {
            return new RifeEnhancementState();
        }
    }

    public void Save(RifeEnhancementState state)
    {
        try
        {
            var directory = System.IO.Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            var temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(state, JsonOptions));
            File.Move(temp, _path, true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Selftest cache persistence is best-effort; playback must not fail on state IO.
        }
    }
}
