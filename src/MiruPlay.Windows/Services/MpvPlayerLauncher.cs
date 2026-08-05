using System.Diagnostics;
using MiruPlay.Windows.Models;

namespace MiruPlay.Windows.Services;

public static class MpvPlayerLauncher
{
    public static IReadOnlyList<string> SystemPlayerFallbackDegradations { get; } =
        ["无 IPC 进度同步", "无轨道选择", "无倍速与精确跳转", "无自动连播"];

    internal static async Task<IPlaybackSession?> PlayAsync(
        LibraryEpisode episode,
        AppSettings settings,
        PlaybackProgressStore progressStore,
        long? startPositionMs = null,
        bool headless = false,
        WebDavPlaybackProxy? playbackProxy = null,
        IntPtr? windowHandle = null,
        MpvWindowsVideoOptions? videoOptions = null) =>
        (await PlayDetailedAsync(
            episode,
            settings,
            progressStore,
            startPositionMs,
            headless,
            playbackProxy,
            windowHandle,
            videoOptions).ConfigureAwait(false)).Session;

    internal static async Task<MpvPlaybackLaunchResult> PlayDetailedAsync(
        LibraryEpisode episode,
        AppSettings settings,
        PlaybackProgressStore progressStore,
        long? startPositionMs = null,
        bool headless = false,
        WebDavPlaybackProxy? playbackProxy = null,
        IntPtr? windowHandle = null,
        MpvWindowsVideoOptions? videoOptions = null)
    {
        var isRemote = IsRemoteUri(episode.MediaPath);
        try
        {
            if (isRemote && playbackProxy is null)
                throw new InvalidOperationException("WebDAV playback must use the shared endpoint consumer.");
            if (!isRemote && !File.Exists(episode.MediaPath))
                throw new FileNotFoundException("找不到视频文件。", episode.MediaPath);

            var launchEpisode = playbackProxy?.Episode ?? episode;
            var libMpvPath = FindLibMpv(settings.LibMpvPath);
            if (libMpvPath is not null)
            {
                try
                {
                    var embeddedSession = await LibMpvPlaybackSession.StartAsync(
                        libMpvPath,
                        launchEpisode,
                        settings,
                        progressStore,
                        headless,
                        windowHandle,
                        playbackProxy,
                        videoOptions).ConfigureAwait(false);
                    playbackProxy = null;
                    return new MpvPlaybackLaunchResult(
                        embeddedSession,
                        MpvFallbackMode.LibMpvEmbedded,
                        []);
                }
                catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or InvalidOperationException or IOException or TimeoutException)
                {
                    // A missing or incompatible native runtime falls through to the local system-player degradation.
                }
            }

            if (isRemote) throw new NotSupportedException("播放 WebDAV 媒体需要 libmpv。");
            Process.Start(new ProcessStartInfo(episode.MediaPath) { UseShellExecute = true });
            return new MpvPlaybackLaunchResult(
                null,
                MpvFallbackMode.SystemPlayerDegraded,
                SystemPlayerFallbackDegradations);
        }
        finally
        {
            if (playbackProxy is not null) await playbackProxy.DisposeAsync().ConfigureAwait(false);
        }
    }

    public static string? FindLibMpv(string? configuredPath) =>
        LibMpvRuntime.FindLibraryPath(configuredPath, []);

    private static bool IsRemoteUri(string path) =>
        Uri.TryCreate(path, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";

    internal static IReadOnlyList<string> PrioritizeSubtitlePaths(IEnumerable<string> paths, string preference)
    {
        var ordered = paths.ToList();
        if (preference == "auto") return ordered;
        var best = ordered
            .Select((path, index) => new { index, score = SubtitlePathScore(path, preference) })
            .Where(item => item.score > 0)
            .OrderByDescending(item => item.score)
            .ThenBy(item => item.index)
            .FirstOrDefault();
        if (best is null) return ordered;
        return [ordered[best.index], .. ordered.Where((_, index) => index != best.index)];
    }

    private static int SubtitlePathScore(string path, string preference)
    {
        var language = DetectSubtitleLanguage(SubtitleFileName(path));
        return preference switch
        {
            "zh_hans" when language == DetectedSubtitleLanguage.ChineseSimplified => 3,
            "zh_hans" when language == DetectedSubtitleLanguage.Chinese => 2,
            "zh_hans" when language == DetectedSubtitleLanguage.ChineseTraditional => 1,
            "zh_hant" when language == DetectedSubtitleLanguage.ChineseTraditional => 3,
            "zh_hant" when language == DetectedSubtitleLanguage.Chinese => 2,
            "zh_hant" when language == DetectedSubtitleLanguage.ChineseSimplified => 1,
            "zh" when language is DetectedSubtitleLanguage.ChineseSimplified or
                DetectedSubtitleLanguage.ChineseTraditional or DetectedSubtitleLanguage.Chinese => 3,
            "en" when language == DetectedSubtitleLanguage.English => 3,
            "ja" when language == DetectedSubtitleLanguage.Japanese => 3,
            _ => 0,
        };
    }

    private static DetectedSubtitleLanguage? DetectSubtitleLanguage(string fileName)
    {
        var value = Path.GetFileNameWithoutExtension(fileName).ToLowerInvariant().Replace('_', '-');
        var tokens = value.Split(['.', ' ', '-', '[', ']'], StringSplitOptions.RemoveEmptyEntries);
        var aliases = tokens.Concat(tokens.Zip(tokens.Skip(1), (first, second) => $"{first}-{second}")).ToHashSet(StringComparer.Ordinal);
        if (ContainsAny(value, "简中", "简体", "簡中") || aliases.Overlaps(["zh-hans", "zh-cn", "zh-sg", "chs", "sc", "gb", "gb2312"]))
            return DetectedSubtitleLanguage.ChineseSimplified;
        if (ContainsAny(value, "繁中", "繁体", "繁體", "正體") || aliases.Overlaps(["zh-hant", "zh-tw", "zh-hk", "zh-mo", "cht", "tc", "big5"]))
            return DetectedSubtitleLanguage.ChineseTraditional;
        if (ContainsAny(value, "中文", "汉语", "漢語") || aliases.Overlaps(["zh", "chi", "zho", "chinese"]))
            return DetectedSubtitleLanguage.Chinese;
        if (ContainsAny(value, "英文", "英语", "英語") || aliases.Overlaps(["en", "eng", "english"]))
            return DetectedSubtitleLanguage.English;
        if (ContainsAny(value, "日文", "日语", "日語") || aliases.Overlaps(["ja", "jpn", "jp", "japanese"]))
            return DetectedSubtitleLanguage.Japanese;
        return null;
    }

    private static string SubtitleFileName(string path)
    {
        var withoutSuffix = path.Contains("://", StringComparison.Ordinal)
            ? path.Split(['?', '#'], 2)[0]
            : path;
        var encodedName = withoutSuffix[(Math.Max(withoutSuffix.LastIndexOf('/'), withoutSuffix.LastIndexOf('\\')) + 1)..];
        try
        {
            return Uri.UnescapeDataString(encodedName);
        }
        catch (UriFormatException)
        {
            return encodedName;
        }
    }

    private static bool ContainsAny(string value, params string[] candidates) =>
        candidates.Any(candidate => value.Contains(candidate, StringComparison.Ordinal));

    private enum DetectedSubtitleLanguage
    {
        ChineseSimplified,
        ChineseTraditional,
        Chinese,
        English,
        Japanese,
    }
}
