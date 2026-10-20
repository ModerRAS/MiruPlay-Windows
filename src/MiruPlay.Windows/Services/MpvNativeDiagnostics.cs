namespace MiruPlay.Windows.Services;

public sealed record MpvNativePropertySample(string Name, string? Value);

public sealed record MpvNativeLogMessage(long ObservedAtElapsedRealtimeMs, string Prefix, int Level, string Text);

public sealed record MpvNativeDiagnostics(
    long CollectedAtElapsedRealtimeMs,
    bool SurfaceAttached,
    IReadOnlyList<MpvNativePropertySample> Properties,
    IReadOnlyList<MpvNativeLogMessage> RecentLogs);
