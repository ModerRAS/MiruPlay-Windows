using System.Globalization;
using MiruPlay.Windows.Models;

namespace MiruPlay.Windows.Services;

public sealed record RewEqImportedBand(
    AudioDspBand Band,
    int LineNumber,
    string Control,
    double? BandwidthHz);

public sealed record RewEqParseError(int LineNumber, string Message);

public sealed record RewEqImportResult(
    IReadOnlyList<RewEqImportedBand> Bands,
    IReadOnlyList<RewEqParseError> Errors,
    IReadOnlyList<string> Warnings);

public static class RewEqFileParser
{
    private static readonly StringComparer HeaderComparer = StringComparer.OrdinalIgnoreCase;

    public static RewEqImportResult Parse(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var bands = new List<RewEqImportedBand>();
        var errors = new List<RewEqParseError>();
        var warnings = new List<string>();
        var section = string.Empty;
        Dictionary<string, int>? columns = null;
        var lines = content.Split(["\r\n", "\n", "\r"], StringSplitOptions.None);

        for (var index = 0; index < lines.Length; index++)
        {
            var lineNumber = index + 1;
            var line = lines[index].TrimEnd();
            var trimmed = line.Trim();
            if (trimmed.Length == 0) continue;

            if (!line.Contains('\t') && !trimmed.Contains('=') &&
                (trimmed.Equals("Generic", StringComparison.OrdinalIgnoreCase) ||
                 trimmed.Equals("Compound_filters", StringComparison.OrdinalIgnoreCase)))
            {
                section = trimmed;
                columns = null;
                continue;
            }

            if (!line.Contains('\t')) continue;
            var cells = line.Split('\t').Select(cell => cell.Trim()).ToArray();
            var candidateColumns = cells
                .Select((cell, cellIndex) => (cell, cellIndex))
                .Where(item => item.cell.Length > 0)
                .ToDictionary(item => item.cell, item => item.cellIndex, HeaderComparer);
            if (candidateColumns.ContainsKey("Type") && candidateColumns.ContainsKey("Enabled"))
            {
                columns = candidateColumns;
                if (section.Length == 0) section = "Generic";
                continue;
            }

            if (columns is null || !section.Equals("Generic", StringComparison.OrdinalIgnoreCase) &&
                !section.Equals("Compound_filters", StringComparison.OrdinalIgnoreCase))
                continue;
            ParseRow(section, columns, cells, lineNumber, bands, errors, warnings);
        }

        return new RewEqImportResult(bands, errors, warnings);
    }

    private static void ParseRow(
        string section,
        IReadOnlyDictionary<string, int> columns,
        IReadOnlyList<string> cells,
        int lineNumber,
        List<RewEqImportedBand> bands,
        List<RewEqParseError> errors,
        List<string> warnings)
    {
        var typeValue = Cell(columns, cells, "Type");
        if (string.IsNullOrWhiteSpace(typeValue) || typeValue.Equals("None", StringComparison.OrdinalIgnoreCase)) return;
        if (!bool.TryParse(Cell(columns, cells, "Enabled"), out var enabled))
        {
            errors.Add(new(lineNumber, "Enabled must be True or False"));
            return;
        }
        if (!enabled) return;
        if (!AudioDspStorage.TryParseFilterType(typeValue, out var type))
        {
            var message = $"Unsupported REW filter type '{typeValue}'";
            if (section.Equals("Compound_filters", StringComparison.OrdinalIgnoreCase)) warnings.Add($"line {lineNumber}: {message}");
            else errors.Add(new(lineNumber, message));
            return;
        }
        if (!TryReadDouble(columns, cells, "Frequency(Hz)", lineNumber, errors, out var frequency) ||
            !TryReadDouble(columns, cells, "Gain(dB)", lineNumber, errors, out var gain)) return;

        var q = 1d;
        var qValue = Cell(columns, cells, "Q");
        if (!string.IsNullOrWhiteSpace(qValue) && !double.TryParse(qValue, NumberStyles.Float, CultureInfo.InvariantCulture, out q))
        {
            errors.Add(new(lineNumber, "Q must be a decimal number"));
            return;
        }
        double? bandwidth = null;
        var bandwidthValue = Cell(columns, cells, "Bandwidth(Hz)");
        if (!string.IsNullOrWhiteSpace(bandwidthValue) && double.TryParse(bandwidthValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedBandwidth))
            bandwidth = parsedBandwidth;

        var control = Cell(columns, cells, "Control") ?? string.Empty;
        bands.Add(new(
            new AudioDspBand(type, frequency, gain, q),
            lineNumber,
            control,
            bandwidth));
    }

    private static bool TryReadDouble(
        IReadOnlyDictionary<string, int> columns,
        IReadOnlyList<string> cells,
        string name,
        int lineNumber,
        List<RewEqParseError> errors,
        out double value)
    {
        var text = Cell(columns, cells, name);
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value)) return true;
        errors.Add(new(lineNumber, $"{name} must be a finite decimal number"));
        return false;
    }

    private static string? Cell(IReadOnlyDictionary<string, int> columns, IReadOnlyList<string> cells, string name) =>
        columns.TryGetValue(name, out var index) && index < cells.Count ? cells[index] : null;
}
