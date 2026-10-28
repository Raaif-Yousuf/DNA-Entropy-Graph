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

/// <summary>Reads the worker's gene summary table. Never throws for a bad file: null means "could not be read", an empty list means "no genes".</summary>
public static class GeneTableReader
{
    private static readonly string[] Columns =
    [
        "contig", "gene_id", "begin", "end", "strand", "length_nt", "partial", "wraps_origin", "segments",
        "mean_entropy_bits", "min_entropy_bits", "max_entropy_bits", "mean_surprisal_bits", "fraction_low_entropy",
    ];

    public static IReadOnlyList<GeneRecord>? Read(string path)
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

        if (lines.Length == 0 || !lines[0].TrimEnd('\r').Split('\t').SequenceEqual(Columns))
        {
            return null;
        }

        var rows = new List<GeneRecord>(lines.Length - 1);
        foreach (var line in lines.Skip(1).Where(l => l.Length > 0))
        {
            var c = line.Split('\t');
            if (c.Length != Columns.Length
                || !long.TryParse(c[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var begin)
                || !long.TryParse(c[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var end)
                || !long.TryParse(c[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out var length)
                || !Number(c[9], out var mean)
                || !Number(c[10], out var min)
                || !Number(c[11], out var max)
                || !Number(c[13], out var low))
            {
                return null;
            }

            double? surprisal = Number(c[12], out var s) ? s : null;
            rows.Add(new GeneRecord(c[0], c[1], begin, end, c[4], length, c[6] == "true", c[7] == "true", mean, min, max, surprisal, low));
        }

        return rows;
    }

    private static bool Number(string text, out double value)
        => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);
}

/// <summary>One <c>*.genes.tsv</c> of a run: its path under the run folder and its rows (null when unreadable).</summary>
public sealed record GeneTableFile(string RelativePath, IReadOnlyList<GeneRecord>? Rows);

/// <summary>Finds a run's gene tables for the Results page (issue #552). Touches the disk: callers run it off the UI thread.</summary>
public interface IGeneTableSource
{
    IReadOnlyList<GeneTableFile> Load(string folder);
}

/// <summary>The real source: every <c>*.genes.tsv</c> in the run folder and one level of per-input folders; a link, hidden or system entry is never entered (Hard Rule 14).</summary>
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
                MaxRecursionDepth = 4,
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
