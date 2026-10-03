using System.Globalization;

namespace DnaEntropyGraph.Core.Runs;

/// <summary>One contig's numbers exactly as the worker wrote them in its summary file. <see cref="Direction"/> and <see cref="Seam"/> are text, as written ("both-combined", "n/a").</summary>
public sealed record ContigSummary(string Name, long Length, double Mean, double Min, double Max, string? Direction, string? Seam);

/// <summary>The headline block and the per-contig blocks of one summary file. Entropy is in bits.</summary>
public sealed record RunSummary(string Name, int Records, long TotalLength, double MeanAll, double MinAll, double MaxAll, IReadOnlyList<ContigSummary> Contigs);

/// <summary>
/// Reads the worker's summary file (<c>stats.txt</c> for GenBank input, <c>&lt;name&gt;.summary.txt</c> otherwise,
/// <c>worker/src/dna_entropy/writers/summary.py</c> <c>write_multi</c>). The numbers are the worker's own; the app never
/// recomputes entropy (issue #102). Strict on purpose: a block with no mean is "not a summary we understand", never a zero.
/// The shape is pinned by <c>tests/contract-fixtures/run_summary_two_contigs.txt</c>, which the worker's own test regenerates.
/// </summary>
public static class RunSummaryReader
{
    private const string Header = "DNA-Entropy summary";

    /// <summary>Throws <see cref="InvalidDataException"/> for anything that is not a summary this app understands.</summary>
    public static RunSummary Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        if (lines.Count == 0 || lines[0] != Header)
        {
            throw new InvalidDataException("The summary does not start with the worker's header.");
        }

        var overall = new Dictionary<string, string>(StringComparer.Ordinal);
        var contigs = new List<ContigSummary>();
        string? contigName = null;
        var block = new Dictionary<string, string>(StringComparer.Ordinal);

        void Close()
        {
            if (contigName is not null)
            {
                contigs.Add(ToContig(contigName, block));
            }

            block = new Dictionary<string, string>(StringComparer.Ordinal);
        }

        foreach (var line in lines.Skip(1))
        {
            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                Close();
                contigName = line[1..^1];
                continue;
            }

            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0)
            {
                continue;
            }

            var key = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();

            // The first value of a key wins: "entropy mean:" is the track, and later lines never shadow it.
            (contigName is null ? overall : block).TryAdd(key, value);
        }

        Close();

        return new RunSummary(
            overall.GetValueOrDefault("name") ?? throw Missing("name"),
            checked((int)Whole(overall, "records")),
            Whole(overall, "total length"),
            Number(overall, "entropy mean (all)"),
            Number(overall, "entropy min (all)"),
            Number(overall, "entropy max (all)"),
            contigs);
    }

    private static ContigSummary ToContig(string name, Dictionary<string, string> block)
        => new(
            name,
            Whole(block, "length"),
            Number(block, "entropy mean"),
            Number(block, "entropy min"),
            Number(block, "entropy max"),
            block.GetValueOrDefault("direction"),
            block.GetValueOrDefault("seam"));

    private static InvalidDataException Missing(string key) => new($"The summary has no '{key}' line.");

    // "1.6898 bits", "0.8985 bits (position 14)": the first token is the number.
    private static double Number(Dictionary<string, string> values, string key)
    {
        var token = FirstToken(values, key);
        return double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number)
            ? number
            : throw new InvalidDataException($"The summary's '{key}' is not a number.");
    }

    // "64 nt".
    private static long Whole(Dictionary<string, string> values, string key)
        => long.TryParse(FirstToken(values, key), NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            ? number
            : throw new InvalidDataException($"The summary's '{key}' is not a whole number.");

    private static string FirstToken(Dictionary<string, string> values, string key)
        => values.TryGetValue(key, out var value) ? value.Split(' ', 2)[0] : throw Missing(key);
}
