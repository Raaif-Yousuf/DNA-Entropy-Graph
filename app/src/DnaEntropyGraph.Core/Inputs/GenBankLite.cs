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
public sealed record GenBankLiteResult(IReadOnlyList<GenBankLiteRecord> Records, IReadOnlyList<string> Notices)
{
    /// <summary>The same notices as <see cref="Notices"/>, as machine codes with their numbers (built side by side).</summary>
    public IReadOnlyList<InputNotice> NoticeCodes { get; init; } = [];
}

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

    private const int DroppedShown = 3; // worker _DROPPED_SHOWN
    private const int LocationTextMax = 40; // worker _LOCATION_TEXT_MAX

    /// <summary>
    /// The length and topology Biopython takes from the LOCUS line, read from its whitespace-separated tokens: the number before the
    /// <c>bp</c>, <c>aa</c> or <c>rc</c> unit, and whether the word <c>circular</c> appears. Biopython reads fixed columns first, so a
    /// LOCUS line laid out oddly could differ; the effect is only on origin-wrapping locations such as <c>42..1</c>.
    /// </summary>
    private static (long? Length, bool Circular) ReadLocusHeader(string[] tokens)
    {
        long? length = null;
        for (var i = 2; i < tokens.Length; i++)
        {
            if (tokens[i] is "bp" or "aa" or "rc")
            {
                length = long.TryParse(tokens[i - 1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : null;
                break;
            }
        }

        return (length, tokens.Skip(2).Contains("circular"));
    }

    /// <summary>The worker's <c>_dropped_location_notice</c> text, word for word (a log and parity detail; the UI shows Resources.resw copy).</summary>
    private static string DroppedFeaturesNotice(int record, string key, IReadOnlyList<string> locationTexts)
    {
        var named = locationTexts.Select(t => $"{key} {PythonText.Ascii(CutLocationText(t))}").ToList();
        var shown = string.Join(", ", named.Take(DroppedShown));
        var more = named.Count > DroppedShown ? $", and {named.Count - DroppedShown} more" : string.Empty;
        return $"GenBank record {record}: {named.Count} {key} feature(s) were dropped because their location text could not be read ({shown}{more}). "
            + "A gene with an unreadable location is missing from the exported tracks. Correct the location in the source file "
            + "(for example 1..42 or complement(85..126)) and run again.";
    }

    // Python counts code points: cut at 40, to 37 plus "...".
    private static string CutLocationText(string text)
    {
        var runes = text.EnumerateRunes().ToList();
        return runes.Count <= LocationTextMax ? text : string.Concat(runes.Take(LocationTextMax - 3)) + "...";
    }

    public static GenBankLiteResult ReadFile(string path) => Read(TextDecoder.ReadText(path));

    public static GenBankLiteResult Read(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\r", "\n", StringComparison.Ordinal);
        var lines = normalized.Split('\n');

        var records = new List<GenBankLiteRecord>();
        var notices = new List<string>();
        var coded = new List<InputNotice>();

        string? locusName = null;
        var geneCount = 0;
        var cdsCount = 0;
        var inOrigin = false;
        var seq = new System.Text.StringBuilder();
        var recordIndex = 0;
        var tableChecked = false; // Biopython reads the feature table once per record, in its header
        long? locusLength = null; // the LOCUS line's length and topology: they decide how an origin-wrapping location reads
        var locusCircular = false;
        var tableFeatures = new List<(string Key, string? DroppedText)>(); // the record's features in table order; DroppedText is set when Biopython cannot read the location

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
                coded.Add(new InputNotice(InputNoticeCode.GenBankRecordSkippedNoSequence, recordIndex));
            }
            else
            {
                // Issue #543/#788: the worker drops a gene (or, in a record with no gene features, a CDS) whose location cannot
                // be read, with one counted notice per record; so does the pill's gene count.
                var sourceKey = tableFeatures.Any(f => f.Key == "gene") ? "gene" : "CDS";
                var lost = tableFeatures.Where(f => f.Key == sourceKey && f.DroppedText is not null).ToList();
                if (lost.Count > 0)
                {
                    notices.Add(DroppedFeaturesNotice(recordIndex, sourceKey, lost.Select(f => f.DroppedText!).ToList()));
                    coded.Add(new InputNotice(InputNoticeCode.GenBankFeaturesDropped, lost.Count, recordIndex));
                }
                // Counted from the parsed feature table (a wrapped qualifier line that starts with "gene" is not a feature),
                // without the features whose location the worker cannot read.
                geneCount = tableFeatures.Count(f => f.Key == "gene" && f.DroppedText is null);
                cdsCount = tableFeatures.Count(f => f.Key == "CDS" && f.DroppedText is null);
                records.Add(new GenBankLiteRecord(locusName, recSeq, geneCount, cdsCount, recordIndex));
            }
            locusName = null;
            geneCount = 0;
            cdsCount = 0;
            inOrigin = false;
            tableChecked = false;
            tableFeatures.Clear();
            locusLength = null;
            locusCircular = false;
            seq.Clear();
        }

        for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            var rawLine = lines[lineIndex];
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
                (locusLength, locusCircular) = ReadLocusHeader(parts);
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
            if (!tableChecked)
            {
                if (GenBankFeatureTable.IsStartMarker(rawLine))
                {
                    // The worker's parser refuses a bad feature table (#548) and a location it cannot recover from (#788); it drops a
                    // feature whose location it cannot read (#543).
                    foreach (var feature in GenBankFeatureTable.Validate(lines, lineIndex, recordIndex + 1))
                    {
                        var reading = GenBankLocation.Read(feature.Location, locusLength, locusCircular);
                        if (reading.Verdict == LocationVerdict.Refused)
                        {
                            throw GenBankFeatureTable.RefuseLocation(recordIndex + 1);
                        }

                        tableFeatures.Add((feature.Key, reading.Verdict == LocationVerdict.Dropped ? reading.QuotedText : null));
                    }
                    tableChecked = true;
                }
                else if (GenBankFeatureTable.IsSequenceHeader(rawLine))
                {
                    tableChecked = true; // no feature table in this record
                }
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
            coded.Add(new InputNotice(InputNoticeCode.GenBankRead, records.Count, totalFeatures));
        }

        return new GenBankLiteResult(records, notices) { NoticeCodes = coded };
    }
}
