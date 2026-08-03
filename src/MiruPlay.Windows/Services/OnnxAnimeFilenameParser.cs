using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace MiruPlay.Windows.Services;

public sealed class OnnxAnimeFilenameParser : IAnimeFilenameParser, IDisposable
{
    private readonly InferenceSession _session;
    private readonly Dictionary<string, int> _vocabulary;
    private readonly Dictionary<int, string> _labels;
    private readonly int _graphLength;
    private readonly int _padTokenId;
    private readonly int _unknownTokenId;
    private readonly int _classTokenId;
    private readonly int _separatorTokenId;

    public OnnxAnimeFilenameParser(string modelPath, string vocabularyPath, string configPath)
    {
        if (!File.Exists(modelPath)) throw new FileNotFoundException("AniFileBERT ONNX 模型不存在。", modelPath);
        _vocabulary = ReadVocabulary(vocabularyPath);
        (_labels, _graphLength) = ReadConfig(configPath);
        _padTokenId = _vocabulary.GetValueOrDefault("[PAD]", 0);
        _unknownTokenId = _vocabulary.GetValueOrDefault("[UNK]", 1);
        _classTokenId = _vocabulary.GetValueOrDefault("[CLS]", 2);
        _separatorTokenId = _vocabulary.GetValueOrDefault("[SEP]", 3);
        if (_graphLength is < 4 or > 512) throw new InvalidDataException("AniFileBERT 序列长度无效。");
        _session = new InferenceSession(modelPath);
    }

    public static IAnimeFilenameParser CreateDefaultLazy() => new LazyDefaultParser();

    public static OnnxAnimeFilenameParser? TryCreateDefault()
    {
        var roots = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Assets", "anime_parser"),
            Path.Combine(AppContext.BaseDirectory, "anime_parser"),
        };
        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var model = Path.Combine(root, "anime_filename_parser.onnx");
            var vocab = Path.Combine(root, "vocab.json");
            var config = Path.Combine(root, "config.json");
            if (File.Exists(model) && File.Exists(vocab) && File.Exists(config))
            {
                try { return new OnnxAnimeFilenameParser(model, vocab, config); }
                catch (Exception) { return null; }
            }
        }
        return null;
    }

    public FilenameMetadata Parse(string filename, int maxLength = 128)
    {
        var tokens = filename.EnumerateRunes().Select(rune => rune.ToString()).ToArray();
        var sequenceLength = Math.Min(Math.Max(maxLength, 1), _graphLength);
        var available = Math.Min(tokens.Length, sequenceLength - 2);
        if (available <= 0) return new FilenameMetadata();

        var inputIds = new long[_graphLength];
        var attentionMask = new long[_graphLength];
        Array.Fill(inputIds, _padTokenId);
        inputIds[0] = _classTokenId;
        attentionMask[0] = 1;
        for (var index = 0; index < available; index++)
        {
            inputIds[index + 1] = _vocabulary.GetValueOrDefault(tokens[index], _unknownTokenId);
            attentionMask[index + 1] = 1;
        }
        inputIds[available + 1] = _separatorTokenId;
        attentionMask[available + 1] = 1;

        var ids = new DenseTensor<long>(inputIds, new[] { 1, _graphLength });
        var mask = new DenseTensor<long>(attentionMask, new[] { 1, _graphLength });
        using var results = _session.Run(new[]
        {
            NamedOnnxValue.CreateFromTensor("input_ids", ids),
            NamedOnnxValue.CreateFromTensor("attention_mask", mask),
        });
        var logits = results[0].AsTensor<float>();
        var emissions = new float[available][];
        for (var index = 0; index < available; index++)
        {
            emissions[index] = new float[logits.Dimensions[^1]];
            for (var label = 0; label < emissions[index].Length; label++)
                emissions[index][label] = logits[0, index + 1, label];
        }
        return Postprocess(tokens.Take(available).ToArray(), ConstrainedBioDecode(emissions));
    }

    public void Dispose()
    {
        _session.Dispose();
        GC.SuppressFinalize(this);
    }

    private FilenameMetadata Postprocess(string[] tokens, int[] labelIds)
    {
        var entities = new List<(string Type, string Text)>();
        string? currentType = null;
        var currentText = new List<string>();
        void Flush()
        {
            if (currentType is null) return;
            entities.Add((currentType, Normalize(string.Concat(currentText))));
            currentType = null;
            currentText.Clear();
        }

        for (var index = 0; index < Math.Min(tokens.Length, labelIds.Length); index++)
        {
            var label = _labels.GetValueOrDefault(labelIds[index], "O");
            if (label.StartsWith("B-", StringComparison.Ordinal))
            {
                Flush();
                currentType = label[2..];
                currentText.Add(tokens[index]);
            }
            else if (label.StartsWith("I-", StringComparison.Ordinal) && currentType == label[2..])
            {
                currentText.Add(tokens[index]);
            }
            else if (label.StartsWith("I-", StringComparison.Ordinal))
            {
                Flush();
                currentType = label[2..];
                currentText.Add(tokens[index]);
            }
            else
            {
                Flush();
            }
        }
        Flush();

        var title = entities
            .Where(entity => entity.Type.StartsWith("TITLE", StringComparison.Ordinal))
            .Select(entity => entity.Text)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        var season = entities.FirstOrDefault(entity => entity.Type is "SEASON" or "PATH_SEASON").Text;
        var episode = entities.FirstOrDefault(entity => entity.Type == "EPISODE").Text;
        return new FilenameMetadata(title, ClassifierText.ExtractNumber(season), ClassifierText.ExtractNumber(episode));
    }

    private int[] ConstrainedBioDecode(float[][] emissions)
    {
        if (emissions.Length == 0) return [];
        var labelCount = emissions.Max(values => values.Length);
        var backpointers = new int[emissions.Length, labelCount];
        var scores = Enumerable.Repeat(float.NegativeInfinity, labelCount).ToArray();
        for (var labelId = 0; labelId < labelCount; labelId++)
        {
            if (!_labels.GetValueOrDefault(labelId, "O").StartsWith("I-", StringComparison.Ordinal))
                scores[labelId] = emissions[0].ElementAtOrDefault(labelId);
        }
        for (var index = 1; index < emissions.Length; index++)
        {
            var next = Enumerable.Repeat(float.NegativeInfinity, labelCount).ToArray();
            for (var labelId = 0; labelId < labelCount; labelId++)
            {
                var label = _labels.GetValueOrDefault(labelId, "O");
                var bestPrevious = 0;
                var bestScore = float.NegativeInfinity;
                for (var previousId = 0; previousId < labelCount; previousId++)
                {
                    if (!AllowedTransition(_labels.GetValueOrDefault(previousId, "O"), label) || scores[previousId] <= bestScore) continue;
                    bestScore = scores[previousId];
                    bestPrevious = previousId;
                }
                next[labelId] = bestScore + emissions[index].ElementAtOrDefault(labelId);
                backpointers[index, labelId] = bestPrevious;
            }
            scores = next;
        }
        var decoded = new int[emissions.Length];
        decoded[^1] = Array.IndexOf(scores, scores.Max());
        for (var index = decoded.Length - 1; index > 0; index--)
            decoded[index - 1] = backpointers[index, decoded[index]];
        return decoded;
    }

    private static bool AllowedTransition(string previous, string current) =>
        !current.StartsWith("I-", StringComparison.Ordinal) ||
        previous == $"B-{current[2..]}" || previous == current;

    private static string Normalize(string value) =>
        value.Trim().Trim('[', ']', '(', ')', '【', '】', '《', '》', '（', '）');

    private sealed class LazyDefaultParser : IAnimeFilenameParser
    {
        private readonly Lazy<OnnxAnimeFilenameParser?> _parser = new(TryCreateDefault);

        public FilenameMetadata Parse(string filename, int maxLength = 128) =>
            _parser.Value?.Parse(filename, maxLength) ?? new FilenameMetadata();
    }

    private static Dictionary<string, int> ReadVocabulary(string path) =>
        JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(path))
        ?? throw new InvalidDataException("AniFileBERT 词表为空。");

    private static (Dictionary<int, string> Labels, int Length) ReadConfig(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        var labels = root.GetProperty("id2label").EnumerateObject()
            .ToDictionary(property => int.Parse(property.Name, System.Globalization.CultureInfo.InvariantCulture), property => property.Value.GetString() ?? "O");
        var length = root.TryGetProperty("max_seq_length", out var configured)
            ? configured.GetInt32()
            : root.GetProperty("max_position_embeddings").GetInt32();
        return (labels, length);
    }
}
