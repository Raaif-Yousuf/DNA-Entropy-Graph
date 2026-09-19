namespace DnaEntropyGraph.Core.Inputs;

/// <summary>Raised when a GenBank file has no LOCUS line or no usable ORIGIN sequence.</summary>
public sealed class GenBankReadException(string message) : Exception(message)
{
    public const string ErrorCode = "INPUT_INVALID";
}

/// <summary>
/// A light local read of one GenBank record: enough to preflight a file before it reaches
/// the cloud, not a full parse. The worker's own reader (Biopython-backed
/// <c>readers/genbank.py</c>) remains the source of truth for the real run; this exists
/// only for local sniffing/validation (Hard Rule 2), per the design's own scope note for
/// this class ("light GenBank parse: LOCUS, ORIGIN, count gene/CDS").
/// </summary>
public sealed record GenBankLiteRecord(string LocusName, string Seq, int GeneCount, int CdsCount);

/// <summary>Result of a light GenBank read: every record found plus any non-fatal notices.</summary>
public sealed record GenBankLiteResult(IReadOnlyList<GenBankLiteRecord> Records, IReadOnlyList<string> Notices);

/// <summary>
/// C# port (deliberately partial - see <see cref="GenBankLiteRecord" />'s docs) of the
/// shape of <c>worker/src/dna_entropy/readers/genbank.py::read_genbank</c>: split a
/// GenBank file into per-record LOCUS name, ORIGIN sequence, and gene/CDS feature counts.
/// A GenBank file may hold multiple records (<c>//</c>-separated); this reads all of them,
/// exactly like the worker.
/// </summary>
public static class GenBankLite
{
    public static GenBankLiteResult ReadFile(string path) => Read(TextDecoder.ReadText(path));

    public static GenBankLiteResult Read(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\r", "\n", StringComparison.Ordinal);
        var lines = normalized.Split('\n');

        var records = new List<GenBankLiteRecord>();
        var notices = new List<string>();

        string? locusName = null;
        var geneCount = 0;
        var cdsCount = 0;
        var inOrigin = false;
        var seq = new System.Text.StringBuilder();
        var recordIndex = 0;

        void FlushRecord()
        {
            recordIndex++;
            if (locusName is null)
            {
                return; // never entered a LOCUS block; nothing to flush
            }
            var recSeq = seq.ToString();
            if (recSeq.Length == 0)
            {
                notices.Add($"Skipped GenBank record {recordIndex} (locus {PrivacySafeText.DescribeLen(locusName)}): no nucleotide sequence.");
            }
            else
            {
                records.Add(new GenBankLiteRecord(locusName, recSeq, geneCount, cdsCount));
            }
            locusName = null;
            geneCount = 0;
            cdsCount = 0;
            inOrigin = false;
            seq.Clear();
        }

        foreach (var rawLine in lines)
        {
            if (rawLine.StartsWith("LOCUS", StringComparison.Ordinal))
            {
                if (locusName is not null)
                {
                    FlushRecord(); // a LOCUS line with no preceding "//" - tolerate it, start fresh
                }
                var parts = rawLine.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                locusName = parts.Length > 1 ? parts[1] : string.Empty;
                continue;
            }
            if (locusName is null)
            {
                continue; // before the first LOCUS line - not a GenBank record yet
            }
            if (rawLine.StartsWith("//", StringComparison.Ordinal))
            {
                FlushRecord();
                continue;
            }
            if (rawLine.StartsWith("ORIGIN", StringComparison.Ordinal))
            {
                inOrigin = true;
                continue;
            }
            if (inOrigin)
            {
                foreach (var c in rawLine)
                {
                    if (char.IsLetter(c))
                    {
                        seq.Append(char.ToUpperInvariant(c));
                    }
                }
                continue;
            }

            var trimmed = rawLine.TrimStart();
            if (trimmed.StartsWith("gene", StringComparison.Ordinal) && (trimmed.Length == 4 || char.IsWhiteSpace(trimmed[4])))
            {
                geneCount++;
            }
            else if (trimmed.StartsWith("CDS", StringComparison.Ordinal) && (trimmed.Length == 3 || char.IsWhiteSpace(trimmed[3])))
            {
                cdsCount++;
            }
        }
        if (locusName is not null)
        {
            FlushRecord(); // file ended without a trailing "//"
        }

        if (records.Count == 0)
        {
            throw new GenBankReadException("No GenBank records with a nucleotide sequence found (expected a LOCUS/ORIGIN block).");
        }

        if (records.Count > 1)
        {
            var totalFeatures = records.Sum(r => r.GeneCount + r.CdsCount);
            notices.Add($"Read {records.Count} record(s) with {totalFeatures} gene/CDS feature(s) from the GenBank (not re-annotated).");
        }

        return new GenBankLiteResult(records, notices);
    }
}
