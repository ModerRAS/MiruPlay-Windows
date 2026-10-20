namespace MiruPlay.Windows.Services;

/// <summary>
/// Minimal CUE sheet support for whole-disc music playback: parses FILE / TRACK /
/// TITLE / PERFORMER / INDEX lines and expands virtual tracks with start/end
/// offsets. Malformed lines are skipped; callers fall back to whole-file playback
/// when no usable tracks emerge. Input is bounded to 1 MiB / 10k lines.
/// </summary>
public static class MusicCueSheet
{
    private const int MaxCharacters = 1 * 1024 * 1024;
    private const int MaxLines = 10_000;

    public sealed record CueTrack(int Number, string Title, string Performer, long StartMs);
    public sealed record CueSheet(string File, string Title, string Performer, IReadOnlyList<CueTrack> Tracks);
    public sealed record VirtualTrack(
        string AudioPath,
        int Number,
        string Title,
        string Performer,
        string Album,
        long StartMs,
        long EndMs);

    public static CueSheet Parse(string content, string albumFallback = "")
    {
        if (content.Length > MaxCharacters)
            throw new InvalidDataException(AppStrings.Get("music.cueTooLarge"));
        string? file = null;
        var title = "";
        var performer = "";
        var tracks = new List<CueTrack>();
        int? currentNumber = null;
        var currentTitle = "";
        var currentPerformer = "";
        long? currentStartMs = null;
        var lines = 0;
        foreach (var raw in content.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (++lines > MaxLines)
                throw new InvalidDataException(AppStrings.Get("music.cueTooManyLines"));
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("REM", StringComparison.OrdinalIgnoreCase)) continue;
            var firstSpace = line.IndexOf(' ');
            var keyword = (firstSpace < 0 ? line : line[..firstSpace]).ToUpperInvariant();
            var rest = firstSpace < 0 ? "" : line[(firstSpace + 1)..].Trim();
            switch (keyword)
            {
                case "FILE" when file is null:
                    file = Unquote(rest.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "");
                    break;
                case "TITLE":
                    var titleValue = Unquote(rest);
                    if (currentNumber is null) title = titleValue;
                    else currentTitle = titleValue;
                    break;
                case "PERFORMER":
                    var performerValue = Unquote(rest);
                    if (currentNumber is null) performer = performerValue;
                    else currentPerformer = performerValue;
                    break;
                case "TRACK" when int.TryParse(
                    rest.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(),
                    out var number) && number is >= 1 and <= 99:
                    if (currentNumber is { } pending && currentStartMs is { } pendingStart)
                        tracks.Add(new CueTrack(pending, currentTitle, currentPerformer, pendingStart));
                    currentNumber = number;
                    currentTitle = "";
                    currentPerformer = "";
                    currentStartMs = null;
                    break;
                case "INDEX" when currentNumber is not null:
                    var parts = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    // Only INDEX 01 marks a track start; pregap INDEX 00 is ignored.
                    if (parts.Length >= 2 && parts[0] == "01" && TryParseTime(parts[1], out var startMs))
                        currentStartMs = startMs;
                    break;
            }
        }
        if (currentNumber is { } last && currentStartMs is { } lastStart)
            tracks.Add(new CueTrack(last, currentTitle, currentPerformer, lastStart));
        return new CueSheet(
            file ?? "",
            string.IsNullOrWhiteSpace(title) ? albumFallback : title,
            performer,
            tracks.OrderBy(track => track.StartMs).ThenBy(track => track.Number).ToList());
    }

    /// <summary>Expand virtual tracks; ends come from the next start, the file duration, or -1 (file end).</summary>
    public static IReadOnlyList<VirtualTrack> Expand(
        string audioPath,
        CueSheet sheet,
        long fileDurationMs,
        string albumFallback = "")
    {
        var album = string.IsNullOrWhiteSpace(sheet.Title) ? albumFallback : sheet.Title;
        var result = new List<VirtualTrack>();
        for (var i = 0; i < sheet.Tracks.Count; i++)
        {
            var track = sheet.Tracks[i];
            var end = i + 1 < sheet.Tracks.Count ? sheet.Tracks[i + 1].StartMs : fileDurationMs > 0 ? Math.Max(fileDurationMs, 0) : -1;
            if (end >= 0 && end <= track.StartMs) continue;
            var title = string.IsNullOrWhiteSpace(track.Title) ? $"Track {track.Number:00}" : track.Title;
            var performer = string.IsNullOrWhiteSpace(track.Performer) ? sheet.Performer : track.Performer;
            result.Add(new VirtualTrack(audioPath, track.Number, title, performer, album, track.StartMs, end));
        }
        return result;
    }

    private static string Unquote(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length >= 2 && trimmed.StartsWith('"') && trimmed.EndsWith('"')
            ? trimmed[1..^1]
            : trimmed;
    }

    private static bool TryParseTime(string value, out long milliseconds)
    {
        milliseconds = 0;
        var parts = value.Split(':');
        if (parts.Length != 3 ||
            !int.TryParse(parts[0], out var minutes) || minutes < 0 || minutes > 99 ||
            !int.TryParse(parts[1], out var seconds) || seconds is < 0 or >= 60 ||
            !int.TryParse(parts[2], out var frames) || frames is < 0 or >= 75)
            return false;
        milliseconds = ((long)minutes * 60 + seconds) * 1_000 + frames * 1_000 / 75;
        return true;
    }
}
