using System.Diagnostics;
using System.Globalization;
using MiruPlay.Windows.Models;

namespace MiruPlay.Windows.Services;

public static class MpvPlayerLauncher
{
    internal static async Task<MpvPlaybackSession?> PlayAsync(
        LibraryEpisode episode,
        AppSettings settings,
        PlaybackProgressStore progressStore,
        long? startPositionMs = null,
        bool headless = false,
        WebDavPlaybackProxy? playbackProxy = null)
    {
        var isRemote = IsRemoteUri(episode.MediaPath);
        try
        {
            if (isRemote && playbackProxy is null)
                throw new InvalidOperationException("WebDAV playback must use the shared endpoint consumer.");
            if (!isRemote && !File.Exists(episode.MediaPath))
                throw new FileNotFoundException("找不到视频文件。", episode.MediaPath);

            var mpvPath = FindMpv(settings.PlayerPath);
            if (mpvPath is null)
            {
                if (isRemote) throw new NotSupportedException("播放 WebDAV 媒体需要 mpv。");
                Process.Start(new ProcessStartInfo(episode.MediaPath) { UseShellExecute = true });
                return null;
            }

            var pipeName = $"miruplay-{Guid.NewGuid():N}";
            var progress = progressStore.Get(episode.ProgressKey);
            if (startPositionMs is not null)
            {
                var durationMs = progress?.DurationMs ?? Convert.ToInt64(episode.Duration.TotalMilliseconds);
                progress = new PlaybackProgress(
                    episode.ProgressKey,
                    Math.Clamp(startPositionMs.Value, 0, durationMs > 0 ? durationMs : long.MaxValue),
                    durationMs,
                    progress?.LastWatchedEpochMs ?? 0,
                    progress?.PlayCount ?? 0);
            }
            var launchEpisode = playbackProxy?.Episode ?? episode;
            var startInfo = CreateStartInfo(mpvPath, pipeName, launchEpisode, settings, progress, headless);
            var process = Process.Start(startInfo) ?? throw new InvalidOperationException("无法启动 mpv。 ");
            var session = await MpvPlaybackSession.AttachAsync(
                process,
                pipeName,
                episode,
                progressStore,
                transportLease: playbackProxy).ConfigureAwait(false);
            playbackProxy = null;
            return session;
        }
        finally
        {
            if (playbackProxy is not null) await playbackProxy.DisposeAsync().ConfigureAwait(false);
        }
    }

    internal static ProcessStartInfo CreateStartInfo(
        string mpvPath,
        string pipeName,
        LibraryEpisode episode,
        AppSettings settings,
        PlaybackProgress? progress,
        bool headless = false)
    {
        var startInfo = new ProcessStartInfo(mpvPath)
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            WorkingDirectory = IsRemoteUri(episode.MediaPath) || episode.MediaPath.StartsWith("\\\\", StringComparison.Ordinal)
                ? AppContext.BaseDirectory
                : Path.GetDirectoryName(episode.MediaPath)!,
        };
        startInfo.ArgumentList.Add(headless ? "--force-window=no" : "--force-window=yes");
        if (headless)
        {
            startInfo.ArgumentList.Add("--vo=null");
            startInfo.ArgumentList.Add("--ao=null");
        }
        startInfo.ArgumentList.Add("--resume-playback=no");
        startInfo.ArgumentList.Add("--keep-open=yes");
        startInfo.ArgumentList.Add($"--input-ipc-server=\\\\.\\pipe\\{pipeName}");
        if (progress is { IsCompleted: false, PositionMs: > 0 })
        {
            startInfo.ArgumentList.Add($"--start={(progress.PositionMs / 1_000d).ToString(CultureInfo.InvariantCulture)}");
        }
        AddSubtitlePreference(startInfo, settings.PreferredSubtitleLanguage);
        foreach (var subtitlePath in PrioritizeSubtitlePaths(
            episode.SubtitlePaths.Where(path => File.Exists(path) || IsRemoteUri(path)),
            settings.PreferredSubtitleLanguage))
        {
            startInfo.ArgumentList.Add($"--sub-file={subtitlePath}");
        }
        startInfo.ArgumentList.Add(episode.MediaPath);
        return startInfo;
    }

    public static string? FindMpv(string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath)) return configuredPath;

        var packagedPath = Path.Combine(AppContext.BaseDirectory, "runtime", "mpv", "mpv.exe");
        if (File.Exists(packagedPath)) return packagedPath;

        var pathDirectories = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator) ?? [];
        return pathDirectories
            .Select(directory => Path.Combine(directory.Trim('"'), "mpv.exe"))
            .FirstOrDefault(File.Exists);
    }

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

    private static void AddSubtitlePreference(ProcessStartInfo startInfo, string preference)
    {
        var languages = preference switch
        {
            "zh_hans" => "zh-Hans,zh-CN,chs,sc,chi,zho",
            "zh_hant" => "zh-Hant,zh-TW,cht,tc,chi,zho",
            "zh" => "zh-Hans,zh-Hant,zh-CN,zh-TW,chi,zho",
            "en" => "eng,en",
            "ja" => "jpn,ja",
            _ => null,
        };
        if (languages is not null) startInfo.ArgumentList.Add($"--slang={languages}");
    }
}
