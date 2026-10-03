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
/// <param name="SourceIndex">1-based position among all LOCUS blocks in the file, counting skipped ones (0 when built by hand).</param>
public sealed record GenBankLiteRecord(string LocusName, string Seq, int GeneCount, int CdsCount, int SourceIndex = 0);

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
    // Biopython: `line[:12].rstrip() == "ORIGIN"`.
    private static bool IsOriginHeader(string line) =>
        line.StartsWith("ORIGIN", StringComparison.Ordinal) && PythonText.TrimEnd(line.Length > 12 ? line[..12] : line) == "ORIGIN";

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
            // The worker skips a record with no sequence OR one made only of N (readers/genbank.py:
            // `not seq or set(seq.upper()) <= {"N"}`): a placeholder, not data.
            if (recSeq.Length == 0 || recSeq.All(c => c == 'N'))
            {
                notices.Add($"Skipped GenBank record {recordIndex} (locus {PrivacySafeText.DescribeLen(locusName)}): no nucleotide sequence.");
            }
            else
            {
                records.Add(new GenBankLiteRecord(locusName, recSeq, geneCount, cdsCount, recordIndex));
            }
            locusName = null;
            geneCount = 0;
            cdsCount = 0;
            inOrigin = false;
            seq.Clear();
        }

        foreach (var rawLine in lines)
        {
            // Inside ORIGIN Biopython only ends a record at "//" (or refuses at CONTIG), so a LOCUS
            // line there is sequence data, not a new record (MEASURED 2026-10-02: the worker's
            // genbank_two_locus_no_separator fixture is refused as "Invalid character 'E'").
            if (!inOrigin && rawLine.StartsWith("LOCUS", StringComparison.Ordinal))
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
            if (inOrigin)
            {
                // Bio.GenBank.Scanner.parse_footer, column based: skip blank lines, "//" ends the
                // record, CONTIG is refused, a line whose 10th column is not a space is shifted by
                // one (a one-space-too-far indent) and refused if still wrong, and only line[10:]
                // is sequence (so a line of 10 characters or fewer contributes nothing). Only
                // spaces are removed from it; digits and anything else stay for the validator.
                var line = PythonText.TrimEnd(rawLine);
                if (line.Length == 0)
                {
                    continue;
                }
                if (line == "//")
                {
                    FlushRecord();
                    continue;
                }
                if (line.StartsWith("CONTIG", StringComparison.Ordinal))
                {
                    throw new GenBankReadException("A CONTIG line follows the ORIGIN block (a contig-assembly record has no sequence to analyze).");
                }
                if (line.Length > 9 && line[9] != ' ')
                {
                    line = line[1..];
                    if (line.Length > 9 && line[9] != ' ')
                    {
                        throw new GenBankReadException("A sequence line in the ORIGIN block is malformed (expected a coordinate column).");
                    }
                }
                if (line.Length > 10)
                {
                    foreach (var c in line.AsSpan(10))
                    {
                        if (c != ' ')
                        {
                            seq.Append(c is >= 'a' and <= 'z' ? (char)(c - 32) : c);
                        }
                    }
                }
                continue;
            }
            if (rawLine.StartsWith("//", StringComparison.Ordinal))
            {
                FlushRecord();
                continue;
            }
            if (IsOriginHeader(rawLine))
            {
                inOrigin = true;
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
