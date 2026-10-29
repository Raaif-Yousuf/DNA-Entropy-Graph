using System.Globalization;

namespace DnaEntropyGraph.Core.Tracks;

/// <summary>One gene of <c>&lt;name&gt;.genes.tsv</c> (worker writers/gene_summary.py). Entropy values are bits, read as the worker wrote them (issue #552).</summary>
public sealed record GeneRecord(
    string Contig,
    string GeneId,
    long Begin,
    long End,
    string Strand,
    long LengthNt,
    bool Partial,
    bool WrapsOrigin,
    double MeanEntropy,
    double MinEntropy,
    double MaxEntropy,
    double? MeanSurprisal,
    double FractionLowEntropy);

/// <summary>A gene table as read: the genes, and how many data rows were left out because they could not be read.</summary>
public sealed record GeneTableContent(IReadOnlyList<GeneRecord> Rows, int SkippedRows);

/// <summary>
/// Reads the worker's gene summary table by column name, so a reordered file or an added column still reads. A row whose gene,
/// position or mean entropy cannot be read is left out and counted, never turned into a guess; only a file that has no such columns,
/// or cannot be opened, is unreadable (null).
/// </summary>
public static class GeneTableReader
{
    private static readonly string[] Required = ["contig", "gene_id", "begin", "end", "mean_entropy_bits"];

    public static GeneTableContent? Read(string path)
    {
        string[] lines;
        try
        {
            lines = File.ReadAllLines(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        if (lines.Length == 0)
        {
            return null;
        }

        var header = lines[0].TrimEnd('\r').Split('\t');
        var at = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < header.Length; i++)
        {
            at.TryAdd(header[i].Trim(), i);
        }

        if (Required.Any(c => !at.ContainsKey(c)))
        {
            return null;
        }

        var rows = new List<GeneRecord>(lines.Length - 1);
        var skipped = 0;
        foreach (var line in lines.Skip(1).Where(l => l.Trim().Length > 0))
        {
            var c = line.TrimEnd('\r').Split('\t');
            string Cell(string name) => at.TryGetValue(name, out var i) && i < c.Length ? c[i] : string.Empty;

            if (!long.TryParse(Cell("begin"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var begin)
                || !long.TryParse(Cell("end"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var end)
                || !Number(Cell("mean_entropy_bits"), out var mean)
                || Cell("gene_id").Length == 0)
            {
                skipped++;
                continue;
            }

            long.TryParse(Cell("length_nt"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var length);
            var min = Number(Cell("min_entropy_bits"), out var lo) ? lo : mean;
            var max = Number(Cell("max_entropy_bits"), out var hi) ? hi : mean;
            Number(Cell("fraction_low_entropy"), out var low);
            double? surprisal = Number(Cell("mean_surprisal_bits"), out var s) ? s : null;
            rows.Add(new GeneRecord(Cell("contig"), Cell("gene_id"), begin, end, Cell("strand"), length, Cell("partial") == "true", Cell("wraps_origin") == "true", mean, min, max, surprisal, low));
        }

        return new GeneTableContent(rows, skipped);
    }

    private static bool Number(string text, out double value)
        => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);
}

/// <summary>One <c>*.genes.tsv</c> of a run: its path under the run folder and what was read from it (null when the file is unreadable).</summary>
public sealed record GeneTableFile(string RelativePath, GeneTableContent? Content);

/// <summary>Finds a run's gene tables for the Results page (issue #552). Touches the disk: callers run it off the UI thread.</summary>
public interface IGeneTableSource
{
    IReadOnlyList<GeneTableFile> Load(string folder);
}
/// <summary>The real source: every <c>*.genes.tsv</c> in the run folder and the one level of per-input folders below it; a link, hidden or system entry is never entered (Hard Rule 14).</summary>
public sealed class GeneTableSource : IGeneTableSource
{
    private const string Suffix = ".genes.tsv";

    public IReadOnlyList<GeneTableFile> Load(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            return [];
        }

        try
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                MaxRecursionDepth = 1,
                AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.Hidden | FileAttributes.System,
            };
            return [.. Directory.EnumerateFiles(folder, "*" + Suffix, options)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .Select(p => new GeneTableFile(Path.GetRelativePath(folder, p), GeneTableReader.Read(p)))];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
