using System.Diagnostics;

namespace MiruPlay.Windows.Tv;

/// <summary>Runtime status of the gamepad backends, surfaced by the TV settings page.</summary>
public sealed record TvGamepadStatus(
    bool Demo,
    bool Sdl3DllMissing,
    bool Sdl3InitFailed,
    string? Sdl3Error,
    bool Connected,
    string ActiveBackend,
    string? DeviceName);

/// <summary>
/// Non-blocking diagnostics for the optional SDL3 backend. SDL3 failures fall
/// back to XInput silently in the UI path, so failures are recorded once to
/// Trace and the per-user log folder and surfaced on the settings page.
/// </summary>
public static class TvGamepadDiagnostics
{
    private static readonly object Sync = new();

    public static bool Sdl3DllMissing { get; private set; }

    public static string? Sdl3InitError { get; private set; }

    public static string? LastWarning { get; private set; }

    public static void ReportSdl3DllMissing()
    {
        lock (Sync)
        {
            if (Sdl3DllMissing) return;
            Sdl3DllMissing = true;
            LastWarning = "SDL3.dll not found; falling back to XInput.";
        }
        Write("WARN", LastWarning!);
    }

    public static void ReportSdl3InitFailure(string? error)
    {
        lock (Sync)
        {
            var message = string.IsNullOrWhiteSpace(error) ? "unknown SDL3 error" : error.Trim();
            if (Sdl3InitError == message) return;
            Sdl3InitError = message;
            LastWarning = $"SDL3 initialization failed; falling back to XInput: {message}";
        }
        Write("WARN", LastWarning!);
    }

    public static void Write(string level, string message)
    {
        try
        {
            Trace.TraceWarning($"MiruPlay TV gamepad: {message}");
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MiruPlay",
                "logs");
            Directory.CreateDirectory(directory);
            File.AppendAllText(
                Path.Combine(directory, "tv-gamepad.log"),
                $"{DateTimeOffset.Now:O} [{level}] {message}{Environment.NewLine}");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Diagnostics must never break the TV session.
        }
    }
}
