using System.Globalization;
using System.Xml;

namespace MiruPlay.Windows.Services;

public sealed record NfoUniqueId(string Type, string Value, bool IsDefault = false);

public sealed record NfoEpisodeMetadata(
    string Title = "",
    string? ShowTitle = null,
    int Season = 1,
    int Episode = 1,
    string Plot = "",
    string? Premiered = null,
    double Rating = 0,
    int PlayCount = 0,
    string? LastPlayed = null,
    long ResumePositionMs = 0,
    IReadOnlyList<NfoUniqueId>? UniqueIds = null);

public sealed record NfoActor(string Name, string Role = "");

public sealed record NfoTvShowMetadata(
    string Title = "",
    string OriginalTitle = "",
    string? SortTitle = null,
    string Plot = "",
    IReadOnlyList<string>? Genres = null,
    string? Premiered = null,
    string? Studio = null,
    double Rating = 0,
    IReadOnlyList<NfoUniqueId>? UniqueIds = null,
    IReadOnlyList<NfoActor>? Actors = null);

public enum NfoType
{
    Unknown,
    Episode,
    TvShow,
    Movie,
    MusicVideo,
}

public sealed record NfoWriteOptions(bool CreateBackup = true, string BackupSuffix = ".bak");

public sealed class NfoDocumentService
{
    private const long MaximumNfoBytes = 4 * 1024 * 1024;
    private readonly string _root;
    private readonly NfoWriteOptions _options;

    public NfoDocumentService(string localRoot, NfoWriteOptions? options = null)
    {
        _root = Path.GetFullPath(localRoot.Trim());
        if (!Directory.Exists(_root)) throw new DirectoryNotFoundException($"媒体目录不存在：{_root}");
        EnsureNoReparsePoints(_root);
        _options = options ?? new NfoWriteOptions();
        if (string.IsNullOrWhiteSpace(_options.BackupSuffix) ||
            Path.IsPathRooted(_options.BackupSuffix) ||
            _options.BackupSuffix.Contains(Path.DirectorySeparatorChar) ||
            _options.BackupSuffix.Contains(Path.AltDirectorySeparatorChar))
            throw new ArgumentException("NFO 备份后缀无效。", nameof(options));
    }

    public string EpisodePath(string mediaPath) =>
        ResolveBounded(Path.ChangeExtension(Path.GetFullPath(mediaPath.Trim()), ".nfo"));

    public string TvShowPath(string showDirectory) =>
        ResolveBounded(Path.Combine(Path.GetFullPath(showDirectory.Trim()), "tvshow.nfo"));

    public void WriteEpisode(string mediaPath, NfoEpisodeMetadata metadata) =>
        WriteAtomic(EpisodePath(mediaPath), BuildEpisodeXml(metadata));

    public void WriteTvShow(string showDirectory, NfoTvShowMetadata metadata) =>
        WriteAtomic(TvShowPath(showDirectory), BuildTvShowXml(metadata));

    public NfoEpisodeMetadata ReadEpisode(string nfoPath)
    {
        var document = LoadXml(ResolveBounded(nfoPath));
        var root = document.DocumentElement ?? throw new InvalidDataException("NFO 缺少根节点。");
        if (!root.Name.Equals("episodedetails", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("NFO 根节点不是 episodedetails。");
        return new NfoEpisodeMetadata(
            Text(root, "title") ?? "",
            Text(root, "showtitle"),
            ParseInt(Text(root, "season"), 1),
            ParseInt(Text(root, "episode"), 1),
            Text(root, "plot") ?? "",
            Text(root, "premiered"),
            ParseDouble(Text(root, "rating")),
            ParseInt(Text(root, "playcount"), 0),
            Text(root, "lastplayed"),
            (long)(ParseDouble(Text(root, "resume")) * 60_000),
            ReadIds(root));
    }

    public NfoTvShowMetadata ReadTvShow(string nfoPath)
    {
        var document = LoadXml(ResolveBounded(nfoPath));
        var root = document.DocumentElement ?? throw new InvalidDataException("NFO 缺少根节点。");
        if (!root.Name.Equals("tvshow", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("NFO 根节点不是 tvshow。");
        return new NfoTvShowMetadata(
            Text(root, "title") ?? "",
            Text(root, "originaltitle") ?? Text(root, "title") ?? "",
            Text(root, "sorttitle"),
            Text(root, "plot") ?? "",
            root.SelectNodes("genre")?.OfType<XmlNode>().Select(node => node.InnerText.Trim()).Where(value => value.Length > 0).ToList() ?? [],
            Text(root, "premiered"),
            Text(root, "studio"),
            ParseDouble(Text(root, "rating")),
            ReadIds(root),
            root.SelectNodes("actor")?.OfType<XmlNode>().Select(actor => new NfoActor(
                Text(actor, "name") ?? "",
                Text(actor, "role") ?? "")).Where(actor => actor.Name.Length > 0).ToList() ?? []);
    }

    public NfoType Detect(string nfoPath)
    {
        var document = LoadXml(ResolveBounded(nfoPath));
        return document.DocumentElement?.Name.ToLowerInvariant() switch
        {
            "episodedetails" => NfoType.Episode,
            "tvshow" => NfoType.TvShow,
            "movie" => NfoType.Movie,
            "musicvideo" => NfoType.MusicVideo,
            _ => NfoType.Unknown,
        };
    }

    public void UpdateWatchProgress(string mediaPath, long positionMs, DateTimeOffset lastWatched)
    {
        var path = EpisodePath(mediaPath);
        var metadata = ReadEpisode(path) with
        {
            ResumePositionMs = Math.Max(0, positionMs),
            LastPlayed = lastWatched.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        };
        WriteAtomic(path, BuildEpisodeXml(metadata));
    }

    private string ResolveBounded(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var relative = Path.GetRelativePath(_root, fullPath);
        if (relative is "." or ".." || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            throw new InvalidDataException("NFO 路径超出媒体来源根目录。");
        EnsureNoReparsePoints(fullPath);
        return fullPath;
    }

    private void WriteAtomic(string path, string contents)
    {
        var directory = Path.GetDirectoryName(path) ?? throw new InvalidDataException("NFO 目录无效。");
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporary, contents, new System.Text.UTF8Encoding(false));
            if (_options.CreateBackup && File.Exists(path))
                File.Copy(path, ResolveBounded(path + _options.BackupSuffix), true);
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static XmlDocument LoadXml(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException("NFO 文件不存在。", path);
        if (info.Length > MaximumNfoBytes) throw new InvalidDataException("NFO 文件过大。");
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaximumNfoBytes,
            MaxCharactersFromEntities = 0,
        };
        var document = new XmlDocument { XmlResolver = null };
        using var reader = XmlReader.Create(path, settings);
        document.Load(reader);
        return document;
    }

    private static string? Text(XmlNode node, string name) =>
        node.SelectSingleNode(name)?.InnerText.Trim();

    private static List<NfoUniqueId> ReadIds(XmlNode root) =>
        root.SelectNodes("id")?.OfType<XmlElement>().Select(element => new NfoUniqueId(
            element.GetAttribute("type"), element.InnerText.Trim(),
            element.GetAttribute("default").Equals("true", StringComparison.OrdinalIgnoreCase)))
            .Where(id => id.Value.Length > 0).ToList() ?? [];

    private static int ParseInt(string? value, int fallback) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ? result : fallback;

    private static double ParseDouble(string? value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result) ? result : 0;

    private static string BuildEpisodeXml(NfoEpisodeMetadata metadata)
    {
        var writer = CreateWriter(out var text);
        writer.WriteStartElement("episodedetails");
        Element(writer, "title", metadata.Title);
        Element(writer, "showtitle", metadata.ShowTitle);
        Element(writer, "season", metadata.Season.ToString(CultureInfo.InvariantCulture));
        Element(writer, "episode", metadata.Episode.ToString(CultureInfo.InvariantCulture));
        Element(writer, "plot", metadata.Plot, omitWhenEmpty: true);
        Element(writer, "premiered", metadata.Premiered);
        if (metadata.Rating > 0) Element(writer, "rating", metadata.Rating.ToString(CultureInfo.InvariantCulture));
        Element(writer, "playcount", metadata.PlayCount.ToString(CultureInfo.InvariantCulture));
        Element(writer, "lastplayed", metadata.LastPlayed);
        if (metadata.ResumePositionMs > 0)
            Element(writer, "resume", (metadata.ResumePositionMs / 60_000d).ToString("0.######", CultureInfo.InvariantCulture));
        WriteIds(writer, metadata.UniqueIds);
        writer.WriteEndElement();
        writer.Flush();
        return "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" + text.ToString();
    }

    private static string BuildTvShowXml(NfoTvShowMetadata metadata)
    {
        var writer = CreateWriter(out var text);
        writer.WriteStartElement("tvshow");
        Element(writer, "title", metadata.Title);
        Element(writer, "originaltitle", metadata.OriginalTitle, omitWhenEmpty: true);
        Element(writer, "sorttitle", metadata.SortTitle);
        Element(writer, "plot", metadata.Plot, omitWhenEmpty: true);
        foreach (var genre in metadata.Genres ?? []) Element(writer, "genre", genre, omitWhenEmpty: true);
        Element(writer, "premiered", metadata.Premiered);
        Element(writer, "studio", metadata.Studio);
        if (metadata.Rating > 0) Element(writer, "rating", metadata.Rating.ToString(CultureInfo.InvariantCulture));
        WriteIds(writer, metadata.UniqueIds);
        foreach (var actor in metadata.Actors ?? [])
        {
            writer.WriteStartElement("actor");
            Element(writer, "name", actor.Name);
            Element(writer, "role", actor.Role, omitWhenEmpty: true);
            writer.WriteEndElement();
        }
        writer.WriteEndElement();
        writer.Flush();
        return "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" + text.ToString();
    }

    private static XmlWriter CreateWriter(out System.Text.StringBuilder text)
    {
        text = new System.Text.StringBuilder();
        return XmlWriter.Create(text, new XmlWriterSettings
        {
            OmitXmlDeclaration = true,
            Encoding = new System.Text.UTF8Encoding(false),
            Indent = true,
            NewLineChars = "\n",
        });
    }

    private static void Element(XmlWriter writer, string name, string? value, bool omitWhenEmpty = false)
    {
        if (value is null || omitWhenEmpty && value.Length == 0) return;
        writer.WriteElementString(name, value);
    }

    private static void WriteIds(XmlWriter writer, IReadOnlyList<NfoUniqueId>? ids)
    {
        foreach (var id in ids ?? [])
        {
            writer.WriteStartElement("id");
            writer.WriteAttributeString("type", id.Type);
            writer.WriteAttributeString("default", id.IsDefault ? "true" : "false");
            writer.WriteString(id.Value);
            writer.WriteEndElement();
        }
    }

    private static void EnsureNoReparsePoints(string path)
    {
        var current = Path.GetFullPath(path);
        while (true)
        {
            if (File.Exists(current) || Directory.Exists(current))
            {
                var attributes = File.GetAttributes(current);
                if (attributes.HasFlag(FileAttributes.ReparsePoint))
                    throw new InvalidDataException("NFO 路径不能经过符号链接或联接点。");
            }
            var parent = Directory.GetParent(current)?.FullName;
            if (parent is null || parent.Equals(current, StringComparison.OrdinalIgnoreCase)) break;
            current = parent;
        }
    }
}
