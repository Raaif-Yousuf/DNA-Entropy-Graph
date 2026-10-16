using System.Globalization;
using System.Text;

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
///
/// Speed (#700): the worker writes one row per base, so a 5 Mb genome is a ~150 MB file and the loader runs on a
/// background thread the Results page waits on. MEASURED 2026-10-15 (debug build, dev laptop): the line-by-line
/// StreamReader + double.TryParse version took about 1.24 s for 5 Mb and 13 s for 50 Mb. This version reads the file as
/// UTF-8 bytes in 1 MB blocks and parses start, end and value in place (no string per line); anything unusual (an exponent,
/// a long number) falls back to the framework parser on the same bytes, so what is accepted is unchanged.
/// </summary>
public static class BedGraphReader
{
    /// <summary>Bases per bin when the caller does not choose: a 100 Mb contig is about 6 million floats.</summary>
    public const int DefaultBinSize = 16;

    private const int BlockSize = 1 << 20;

    public static EntropyOverview? Read(string path, int binSize = DefaultBinSize)
    {
        if (binSize < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(binSize));
        }

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BlockSize, FileOptions.SequentialScan);
            return Parse(stream, binSize);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private sealed class Accumulator
    {
        public double[] Sums = new double[1024];
        public int[] Counts = new int[1024];
        public int Length;

        public void EnsureBin(int bin)
        {
            if (bin < Sums.Length)
            {
                return;
            }

            var size = Math.Max(Sums.Length * 2, bin + 1);
            Array.Resize(ref Sums, size);
            Array.Resize(ref Counts, size);
        }
    }

    private sealed class State
    {
        public readonly Dictionary<string, Accumulator> Contigs = new(StringComparer.Ordinal);
        public readonly List<string> Order = [];
        public Accumulator? Current;
        public byte[] CurrentName = [];
    }

    private static EntropyOverview? Parse(Stream stream, int binSize)
    {
        var state = new State();
        var buffer = new byte[BlockSize];
        var filled = 0;
        while (true)
        {
            if (filled == buffer.Length)
            {
                Array.Resize(ref buffer, buffer.Length * 2); // one line longer than the block
            }

            var read = stream.Read(buffer, filled, buffer.Length - filled);
            var atEnd = read == 0;
            filled += read;

            var consumed = 0;
            while (consumed < filled)
            {
                var newline = buffer.AsSpan(consumed, filled - consumed).IndexOf((byte)'\n');
                if (newline < 0)
                {
                    break;
                }

                if (!Line(buffer.AsSpan(consumed, newline), state, binSize))
                {
                    return null;
                }

                consumed += newline + 1;
            }

            if (atEnd)
            {
                if (consumed < filled && !Line(buffer.AsSpan(consumed, filled - consumed), state, binSize))
                {
                    return null;
                }

                break;
            }

            Buffer.BlockCopy(buffer, consumed, buffer, 0, filled - consumed);
            filled -= consumed;
        }

        if (state.Order.Count == 0)
        {
            return null;
        }

        return new EntropyOverview([.. state.Order.Select(name => ToContig(name, state.Contigs[name], binSize))]);
    }

    private static bool IsSpace(byte b) => b is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n' or 0x0B or 0x0C;

    /// <summary>One line. False means a row that is not valid; comments, headers and blank lines are skipped (true).</summary>
    private static bool Line(ReadOnlySpan<byte> line, State state, int binSize)
    {
        while (!line.IsEmpty && IsSpace(line[0]))
        {
            line = line[1..];
        }

        while (!line.IsEmpty && IsSpace(line[^1]))
        {
            line = line[..^1];
        }

        if (line.IsEmpty || line[0] == (byte)'#' || line.StartsWith("track"u8) || line.StartsWith("browser"u8))
        {
            return true;
        }

        if (!Field(ref line, out var chrom) || !Field(ref line, out var startText) || !Field(ref line, out var endText) || !Field(ref line, out var valueText))
        {
            return false;
        }

        if (!ParseInt(startText, out var start) || !ParseInt(endText, out var end) || end < start || !ParseDouble(valueText, out var value))
        {
            return false;
        }

        if (state.Current is null || !chrom.SequenceEqual(state.CurrentName))
        {
            var name = Encoding.UTF8.GetString(chrom);
            if (!state.Contigs.TryGetValue(name, out var found))
            {
                found = new Accumulator();
                state.Contigs[name] = found;
                state.Order.Add(name);
            }

            state.Current = found;
            state.CurrentName = chrom.ToArray();
        }

        Add(state.Current, start, end, value, binSize);
        return true;
    }

    /// <summary>The next space or tab separated token (runs of separators count as one); false when there is none.</summary>
    private static bool Field(ref ReadOnlySpan<byte> rest, out ReadOnlySpan<byte> field)
    {
        var i = 0;
        while (i < rest.Length && rest[i] is (byte)' ' or (byte)'\t')
        {
            i++;
        }

        if (i == rest.Length)
        {
            field = default;
            return false;
        }

        var begin = i;
        while (i < rest.Length && rest[i] is not ((byte)' ' or (byte)'\t'))
        {
            i++;
        }

        field = rest[begin..i];
        rest = rest[i..];
        return true;
    }

    private static bool ParseInt(ReadOnlySpan<byte> text, out int result)
    {
        result = 0;
        if (text.Length is > 0 and <= 9)
        {
            var n = 0;
            foreach (var b in text)
            {
                if (b is < (byte)'0' or > (byte)'9')
                {
                    return false; // NumberStyles.None: digits only, no sign, no separators
                }

                n = (n * 10) + (b - '0');
            }

            result = n;
            return true;
        }

        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out result);
    }

    private static bool ParseDouble(ReadOnlySpan<byte> text, out double result)
    {
        // The overwhelmingly common shape is `[-]digits[.digits]` with at most 15 significant digits: exact in a double.
        var i = 0;
        var negative = false;
        if (text.Length > 0 && text[0] is (byte)'-' or (byte)'+')
        {
            negative = text[0] == (byte)'-';
            i = 1;
        }

        long mantissa = 0;
        var digits = 0;
        var scale = 0;
        var seenPoint = false;
        for (; i < text.Length; i++)
        {
            var b = text[i];
            if (b is >= (byte)'0' and <= (byte)'9')
            {
                mantissa = (mantissa * 10) + (b - '0');
                digits++;
                if (seenPoint)
                {
                    scale++;
                }
            }
            else if (b == (byte)'.' && !seenPoint)
            {
                seenPoint = true;
            }
            else
            {
                break;
            }
        }

        if (i == text.Length && digits is > 0 and <= 15 && scale < Pow10.Length)
        {
            var v = mantissa / Pow10[scale];
            result = negative ? -v : v;
            return true;
        }

        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out result) && double.IsFinite(result);
    }

    private static readonly double[] Pow10 = [1, 1e1, 1e2, 1e3, 1e4, 1e5, 1e6, 1e7, 1e8, 1e9, 1e10, 1e11, 1e12, 1e13, 1e14, 1e15];

    private static void Add(Accumulator contig, int start, int end, double value, int binSize)
    {
        if (end > contig.Length)
        {
            contig.Length = end;
        }

        if (end - start == 1)
        {
            // The per-base row, the case that makes the file huge.
            var one = start / binSize;
            contig.EnsureBin(one);
            contig.Sums[one] += value;
            contig.Counts[one]++;
            return;
        }

        for (var position = start; position < end;)
        {
            var bin = position / binSize;
            contig.EnsureBin(bin);
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
            means[i] = i < contig.Sums.Length && contig.Counts[i] > 0 ? (float)(contig.Sums[i] / contig.Counts[i]) : float.NaN;
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
