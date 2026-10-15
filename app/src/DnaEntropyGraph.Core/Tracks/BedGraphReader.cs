using System.Globalization;

namespace DnaEntropyGraph.Core.Tracks;

/// <summary>
/// One contig of an entropy track, kept as the mean of every <see cref="BinSize"/> bases (the overview chart and the sparklines
/// never need base resolution, and a 100 Mb contig as floats would be 400 MB). <see cref="Means"/> is NaN for a bin with no
/// rows. <see cref="Seam"/> is the 0-based base where the forward and reverse passes were joined, from the run's provenance file,
/// or null when there is none (a short or circular contig).
/// </summary>
public sealed record EntropyContig(string Name, int Length, int BinSize, float[] Means, int? Seam = null);

/// <summary>The entropy track of a run: one <see cref="EntropyContig"/> per contig, in file order.</summary>
public sealed record EntropyOverview(IReadOnlyList<EntropyContig> Contigs);

/// <summary>
/// Reads a bedGraph (<c>chrom start end value</c>, 0-based half-open, docs/science_and_formats.md section 5) into binned means
/// (issue #118). Read-only (Hard Rule 14). A file that cannot be read, has no data row, or has any row that is not four fields
/// with numeric start, end and value gives null: a half-read chart would show wrong numbers, which is worse than none.
/// </summary>
public static class BedGraphReader
{
    /// <summary>Bases per bin when the caller does not choose: a 100 Mb contig is about 6 million floats.</summary>
    public const int DefaultBinSize = 16;

    public static EntropyOverview? Read(string path, int binSize = DefaultBinSize)
    {
        if (binSize < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(binSize));
        }

        try
        {
            using var reader = new StreamReader(path);
            return Parse(reader, binSize);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private sealed class Accumulator
    {
        public readonly List<double> Sums = [];
        public readonly List<int> Counts = [];
        public int Length;
    }

    private static EntropyOverview? Parse(TextReader reader, int binSize)
    {
        var contigs = new Dictionary<string, Accumulator>(StringComparer.Ordinal);
        var order = new List<string>();
        Accumulator? current = null;
        string? currentName = null;

        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var span = line.AsSpan().Trim();
            if (span.IsEmpty || span[0] == '#' || span.StartsWith("track", StringComparison.Ordinal) || span.StartsWith("browser", StringComparison.Ordinal))
            {
                continue;
            }

            if (!TryRow(span, out var chrom, out var start, out var end, out var value))
            {
                return null;
            }

            if (currentName is null || !chrom.SequenceEqual(currentName))
            {
                var name = chrom.ToString();
                if (!contigs.TryGetValue(name, out current))
                {
                    current = new Accumulator();
                    contigs[name] = current;
                    order.Add(name);
                }

                currentName = name;
            }

            Add(current!, start, end, value, binSize);
        }

        if (order.Count == 0)
        {
            return null;
        }

        return new EntropyOverview([.. order.Select(name => ToContig(name, contigs[name], binSize))]);
    }

    private static bool TryRow(ReadOnlySpan<char> span, out ReadOnlySpan<char> chrom, out int start, out int end, out double value)
    {
        chrom = default;
        start = end = 0;
        value = 0;
        Span<Range> fields = stackalloc Range[5];
        var count = span.SplitAny(fields, " \t", StringSplitOptions.RemoveEmptyEntries);
        if (count < 4)
        {
            return false;
        }

        chrom = span[fields[0]];
        return int.TryParse(span[fields[1]], NumberStyles.None, CultureInfo.InvariantCulture, out start)
            && int.TryParse(span[fields[2]], NumberStyles.None, CultureInfo.InvariantCulture, out end)
            && end >= start
            && double.TryParse(span[fields[3]], NumberStyles.Float, CultureInfo.InvariantCulture, out value)
            && double.IsFinite(value);
    }

    private static void Add(Accumulator contig, int start, int end, double value, int binSize)
    {
        contig.Length = Math.Max(contig.Length, end);
        for (var position = start; position < end;)
        {
            var bin = position / binSize;
            while (contig.Sums.Count <= bin)
            {
                contig.Sums.Add(0);
                contig.Counts.Add(0);
            }

            var take = Math.Min(end, (bin + 1) * binSize) - position;
            contig.Sums[bin] += value * take;
            contig.Counts[bin] += take;
            position += take;
        }
    }

    private static EntropyContig ToContig(string name, Accumulator contig, int binSize)
    {
        var bins = (contig.Length + binSize - 1) / binSize;
        var means = new float[bins];
        for (var i = 0; i < bins; i++)
        {
            means[i] = i < contig.Sums.Count && contig.Counts[i] > 0 ? (float)(contig.Sums[i] / contig.Counts[i]) : float.NaN;
        }

        return new EntropyContig(name, contig.Length, binSize, means);
    }
}

/// <summary>Shrinks a series for a small picture (a sparkline) by averaging neighbours; never invents points.</summary>
public static class TrackResampler
{
    public static float[] Resample(IReadOnlyList<float> values, int points)
    {
        if (values.Count <= points || points < 1)
        {
            return [.. values];
        }

        var result = new float[points];
        for (var i = 0; i < points; i++)
        {
            var from = (int)((long)i * values.Count / points);
            var to = Math.Max(from + 1, (int)((long)(i + 1) * values.Count / points));
            double sum = 0;
            var n = 0;
            for (var j = from; j < to; j++)
            {
                if (!float.IsNaN(values[j]))
                {
                    sum += values[j];
                    n++;
                }
            }

            result[i] = n == 0 ? float.NaN : (float)(sum / n);
        }

        return result;
    }
}
