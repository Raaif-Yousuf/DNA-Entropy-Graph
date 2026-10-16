using System.Globalization;
using System.Text;

namespace DnaEntropyGraph.Core.Inputs;

/// <summary>The file extensions of a GFF3 annotation the New run page pairs with a FASTA file (issue #686).</summary>
public static class AnnotationFileTypes
{
    public static IReadOnlyList<string> Extensions { get; } = [".gff3", ".gff"];

    public static bool IsAnnotationFile(string path)
        => Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
}

/// <summary>Why a GFF3 cannot be paired with a FASTA file. One machine code per cause; the page maps it to a Resources.resw key.</summary>
public enum AnnotationProblemCode
{
    /// <summary>The GFF3 or the FASTA is missing, locked or unreadable.</summary>
    FileUnreadable,

    /// <summary>The GFF3 is larger than <see cref="InputLimits.MaxFileBytes"/>; it was not read.</summary>
    FileTooLarge,

    /// <summary>The sequence file is not a FASTA file: a GFF3 pairs only with FASTA (the worker's own rule).</summary>
    NotFasta,

    /// <summary>The GFF3 has no valid <c>gene</c> or <c>CDS</c> row at all.</summary>
    NoFeatures,

    /// <summary>No sequence name in the GFF3 matches a FASTA record name, so no gene can be placed.</summary>
    NoNameMatch,

    /// <summary>Names match, but every feature runs past the end of its sequence.</summary>
    NoneFit,
}

/// <param name="Code">Machine code.</param>
/// <param name="Detail">English diagnostic text for logs and tests, never shown to the user and never a name or a path.</param>
public sealed record AnnotationProblem(AnnotationProblemCode Code, string Detail);

/// <summary>Outcome of <see cref="AnnotationPairValidator.Check"/>.</summary>
/// <param name="Problem">The first thing wrong, or null.</param>
/// <param name="GeneCount">Features that fit their sequence (what the pill shows); 0 when there is a problem.</param>
/// <param name="NoticeCodes">Non-fatal notices as machine codes with their numbers; the page shows Resources.resw copy for them.</param>
public sealed record AnnotationCheckResult(AnnotationProblem? Problem, int GeneCount, IReadOnlyList<InputNotice> NoticeCodes)
{
    public bool IsValid => Problem is null;
}

/// <summary>
/// Checks a FASTA + GFF3 pair on this PC before any cloud resource exists (Hard Rule 2, issue #686): a C# port of the worker's
/// <c>readers/gff3.py</c> and the pairing in <c>readers/input.py</c> (branch feat/135-fasta-gff3-pair-r2). The worker re-reads
/// both files and stays the source of truth; this refuses what it would refuse and counts what it would place.
/// </summary>
public static class AnnotationPairValidator
{
    /// <summary>Checks <paramref name="annotationPath"/> against the records of <paramref name="fastaPath"/>. Never throws for a bad file.</summary>
    /// <param name="topology">The run's topology choice: Auto lets a GFF3 <c>Is_circular=true</c> decide, as the worker does.</param>
    public static AnnotationCheckResult Check(string fastaPath, string annotationPath, Topology topology = Topology.Auto, InputLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(fastaPath);
        ArgumentNullException.ThrowIfNull(annotationPath);
        limits ??= new InputLimits();

        string gffText;
        string fastaText;
        try
        {
            var info = new FileInfo(annotationPath);
            if (!info.Exists || !File.Exists(fastaPath))
            {
                return Fail(AnnotationProblemCode.FileUnreadable, "A file does not exist.");
            }
            if (info.Length > limits.MaxFileBytes)
            {
                return Fail(AnnotationProblemCode.FileTooLarge, "The annotation file is over the size limit.");
            }
            if (new FileInfo(fastaPath).Length > limits.MaxFileBytes)
            {
                return Fail(AnnotationProblemCode.FileTooLarge, "The sequence file is over the size limit.");
            }
            if (SequenceSniffer.DetectKind(fastaPath) != InputKind.Fasta)
            {
                return Fail(AnnotationProblemCode.NotFasta, "The sequence file is not FASTA.");
            }
            fastaText = TextDecoder.ReadText(fastaPath);
            gffText = TextDecoder.ReadText(annotationPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            return Fail(AnnotationProblemCode.FileUnreadable, "A file could not be read.");
        }

        FastaReadResult fasta;
        try
        {
            fasta = FastaLite.Read(fastaText);
        }
        catch (FastaReadException)
        {
            return Fail(AnnotationProblemCode.NotFasta, "The sequence file has no FASTA record.");
        }

        var gff = Gff3Lite.Read(gffText);
        var notices = new List<InputNotice>();
        if (gff.SkippedLines > 0)
        {
            notices.Add(new InputNotice(InputNoticeCode.AnnotationLinesSkipped, gff.SkippedLines, gff.FirstSkippedLine));
        }
        if (gff.BySeqId.Count == 0)
        {
            return Fail(AnnotationProblemCode.NoFeatures, "The GFF3 has no gene or CDS feature.", notices);
        }

        var matched = new HashSet<string>(StringComparer.Ordinal);
        var genes = 0;
        var pastEnd = 0;
        foreach (var record in fasta.Records)
        {
            var seqId = SeqName(record.Header);
            if (!gff.BySeqId.TryGetValue(seqId, out var features))
            {
                continue;
            }

            matched.Add(seqId);
            var circular = topology switch
            {
                Topology.Circular => true,
                Topology.Linear => false,
                _ => gff.Circular.Contains(seqId),
            };
            var length = CountBases(record.Seq);
            foreach (var feature in features)
            {
                if (Fits(feature, length, circular))
                {
                    genes++;
                }
                else
                {
                    pastEnd++;
                }
            }
        }

        if (matched.Count == 0)
        {
            return Fail(AnnotationProblemCode.NoNameMatch, "No GFF3 sequence name matches a FASTA record name.", notices);
        }

        var unmatched = gff.BySeqId.Keys.Count(k => !matched.Contains(k));
        if (unmatched > 0)
        {
            notices.Add(new InputNotice(InputNoticeCode.AnnotationUnmatchedNames, unmatched));
        }
        if (pastEnd > 0)
        {
            notices.Add(new InputNotice(InputNoticeCode.AnnotationFeaturesPastEnd, pastEnd));
        }
        if (genes == 0)
        {
            return Fail(AnnotationProblemCode.NoneFit, "Every GFF3 feature runs past the end of its sequence.", notices);
        }

        return new AnnotationCheckResult(null, genes, notices);
    }

    /// <summary>
    /// <c>readers/gff3.py::fit_to_sequence</c>: a feature inside the sequence fits; on a circular sequence a single row whose end
    /// is past the length crosses the origin and fits when its two parts do not overlap; anything else past the end is dropped.
    /// </summary>
    private static bool Fits(Gff3Feature feature, long length, bool circular)
    {
        if (feature.Segments.All(s => s.End <= length))
        {
            return true;
        }

        return circular && feature.Segments.Count == 1 && feature.Begin <= length && feature.End - length < feature.Begin;
    }

    /// <summary>The first whitespace-separated word of a FASTA header, as <c>rec.header.split(None, 1)[0]</c> (empty for a bare <c>&gt;</c>).</summary>
    private static string SeqName(string header)
    {
        var text = PythonText.Trim(header);
        var end = 0;
        while (end < text.Length && !char.IsWhiteSpace(text[end]))
        {
            end++;
        }

        return text[..end];
    }

    /// <summary>The sequence letters of a raw record: whitespace and digits are not bases (the validator removes them too).</summary>
    private static long CountBases(string raw)
    {
        long count = 0;
        foreach (var c in raw)
        {
            if (!char.IsWhiteSpace(c) && !(c is >= '0' and <= '9'))
            {
                count++;
            }
        }

        return count;
    }

    private static AnnotationCheckResult Fail(AnnotationProblemCode code, string detail, IReadOnlyList<InputNotice>? notices = null)
        => new(new AnnotationProblem(code, detail), 0, notices ?? []);
}

/// <summary>One gene (or CDS) feature: the outer span and its parts (rows sharing an <c>ID</c> are one discontinuous feature).</summary>
internal sealed record Gff3Feature(long Begin, long End, IReadOnlyList<(long Begin, long End)> Segments);

/// <summary>What <see cref="Gff3Lite.Read"/> found.</summary>
internal sealed record Gff3Result(
    Dictionary<string, List<Gff3Feature>> BySeqId,
    HashSet<string> Circular,
    int SkippedLines,
    int FirstSkippedLine);

/// <summary>A light read of a GFF3 file, a port of <c>worker/src/dna_entropy/readers/gff3.py::read_gff3</c> (issue #686).</summary>
internal static class Gff3Lite
{
    public static Gff3Result Read(string text)
    {
        var rows = new Dictionary<string, (List<GffRow> Genes, List<GffRow> Cds)>(StringComparer.Ordinal);
        var circular = new HashSet<string>(StringComparer.Ordinal);
        var skipped = 0;
        var firstSkipped = 0;

        var lineNumber = 0;
        foreach (var line in text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\r", "\n", StringComparison.Ordinal).Split('\n'))
        {
            lineNumber++;
            if (line.StartsWith("##FASTA", StringComparison.Ordinal))
            {
                break; // an embedded FASTA section follows: not annotation rows
            }
            if (PythonText.Trim(line).Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var cols = line.Split('\t');
            if (cols.Length != 9
                || !long.TryParse(cols[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var begin)
                || !long.TryParse(cols[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var end)
                || begin < 1
                || begin > end)
            {
                skipped++;
                firstSkipped = firstSkipped == 0 ? lineNumber : firstSkipped;
                continue;
            }

            var seqId = PercentDecode(cols[0]);
            var attrs = Attributes(cols[8]);
            if (attrs.TryGetValue("Is_circular", out var flag) && string.Equals(flag, "true", StringComparison.OrdinalIgnoreCase))
            {
                circular.Add(seqId);
            }

            if (cols[2] is not ("gene" or "CDS"))
            {
                continue;
            }

            if (!rows.TryGetValue(seqId, out var bucket))
            {
                rows[seqId] = bucket = ([], []);
            }

            (cols[2] == "gene" ? bucket.Genes : bucket.Cds).Add(new GffRow(begin, end, attrs.GetValueOrDefault("ID", string.Empty)));
        }

        var result = new Dictionary<string, List<Gff3Feature>>(StringComparer.Ordinal);
        foreach (var (seqId, (genes, cds)) in rows)
        {
            // Per sequence name: the file's own gene features, else its CDS features.
            var source = genes.Count > 0 ? genes : cds;
            var features = new List<Gff3Feature>();
            var groups = new Dictionary<string, List<GffRow>>(StringComparer.Ordinal);
            var order = new List<object>();
            foreach (var row in source)
            {
                if (row.Id.Length > 0)
                {
                    if (!groups.TryGetValue(row.Id, out var group))
                    {
                        groups[row.Id] = group = [];
                        order.Add(row.Id);
                    }

                    group.Add(row);
                }
                else
                {
                    order.Add(row);
                }
            }

            foreach (var item in order)
            {
                var parts = item is string id ? groups[id] : [(GffRow)item];
                var segments = parts.OrderBy(p => p.Begin).Select(p => (p.Begin, p.End)).ToList();
                features.Add(new Gff3Feature(segments[0].Begin, segments.Max(s => s.End), segments));
            }

            result[seqId] = features;
        }

        return new Gff3Result(result, circular, skipped, firstSkipped);
    }

    private sealed record GffRow(long Begin, long End, string Id);

    private static Dictionary<string, string> Attributes(string column9)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in column9.Split(';'))
        {
            var eq = part.IndexOf('=', StringComparison.Ordinal);
            if (eq < 0)
            {
                continue;
            }

            var key = PythonText.Trim(part[..eq]);
            if (key.Length > 0)
            {
                map[key] = PercentDecode(PythonText.Trim(part[(eq + 1)..]));
            }
        }

        return map;
    }

    /// <summary>
    /// Python's <c>urllib.parse.unquote</c>: each run of <c>%XX</c> escapes is decoded as UTF-8 with the replacement character for bad
    /// bytes, and anything else, including a <c>%</c> not followed by two hex digits, stays as it is.
    /// </summary>
    internal static string PercentDecode(string value)
    {
        if (!value.Contains('%', StringComparison.Ordinal))
        {
            return value;
        }

        var sb = new StringBuilder(value.Length);
        var bytes = new List<byte>();
        void Flush()
        {
            if (bytes.Count > 0)
            {
                sb.Append(Encoding.UTF8.GetString([.. bytes]));
                bytes.Clear();
            }
        }

        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '%' && i + 2 < value.Length && Uri.IsHexDigit(value[i + 1]) && Uri.IsHexDigit(value[i + 2]))
            {
                bytes.Add(Convert.ToByte(value.Substring(i + 1, 2), 16));
                i += 2;
                continue;
            }

            Flush();
            sb.Append(value[i]);
        }

        Flush();
        return sb.ToString();
    }
}
