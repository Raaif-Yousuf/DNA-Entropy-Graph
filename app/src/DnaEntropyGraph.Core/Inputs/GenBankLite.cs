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
public sealed record GenBankLiteRecord(string LocusName, string Seq, int GeneCount, int CdsCount, int SourceIndex = 0)
{
    /// <summary>
    /// How many features the worker maps for this record: its gene features if it has any (even when every one was dropped), else its CDS
    /// features. When built by hand, the surviving gene count, else the surviving CDS count.
    /// </summary>
    public int Mapped => MappedCount ?? (GeneCount > 0 ? GeneCount : CdsCount);

    /// <summary>Set by the readers, which know whether the record had gene features at all.</summary>
    public int? MappedCount { get; init; }
}

/// <summary>One feature of a record's feature table as the readers hand it to <see cref="GenBankLite.ReadTableFeatures" />: its key, its worker id, and what Biopython made of its location.</summary>
internal sealed record TableFeature(string Key, string Id, LocationReading Reading);

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
    private static string DroppedFeaturesNotice(string label, int record, string key, IReadOnlyList<string> locationTexts)
    {
        var named = locationTexts.Select(t => $"{key} {PythonText.Ascii(CutLocationText(t))}").ToList();
        var shown = string.Join(", ", named.Take(DroppedShown));
        var more = named.Count > DroppedShown ? $", and {named.Count - DroppedShown} more" : string.Empty;
        return $"{label} record {record}: {named.Count} {key} feature(s) were dropped because their location text could not be read ({shown}{more}). "
            + "A gene with an unreadable location is missing from the exported tracks. Correct the location in the source file "
            + "(for example 1..42 or complement(85..126)) and run again.";
    }

    /// <summary>
    /// What the worker's <c>records_from_parsed</c> / <c>_features_of</c> make of one record's feature table, for GenBank and EMBL alike: it maps the record's gene
    /// features (else its CDS features), dropping one whose location cannot be read (#543, #788, #796) or does not fit the sequence (#331, #694), each with a
    /// notice, and a gene that was dropped is replaced by the CDS carrying the same gene id (#694). Returns the gene and CDS counts that survive and the number
    /// the worker maps. Not modelled: the notices for a compound (spliced or origin-wrapping) feature that is kept.
    /// </summary>
    internal static (int Genes, int Cds, int Mapped) ReadTableFeatures(
        string label, int recordIndex, IReadOnlyList<TableFeature> tableFeatures, int sequenceLength, string recordId, List<string> notices, List<InputNotice> coded)
    {
        var geneFeatures = tableFeatures.Where(f => f.Key == "gene").ToList();
        var cdsFeatures = tableFeatures.Where(f => f.Key == "CDS").ToList();
        var sourceKey = geneFeatures.Count > 0 ? "gene" : "CDS";
        var source = geneFeatures.Count > 0 ? geneFeatures : cdsFeatures;

        var lost = source.Where(f => f.Reading.Verdict == LocationVerdict.Dropped).ToList();
        if (lost.Count > 0)
        {
            notices.Add(DroppedFeaturesNotice(label, recordIndex, sourceKey, lost.Select(f => f.Reading.QuotedText).ToList()));
            coded.Add(new InputNotice(InputNoticeCode.GenBankFeaturesDropped, lost.Count, recordIndex));
        }

        var outOfRange = 0;

        // _map: False when the feature is dropped, with its notice when it has one (an unreadable location was reported above).
        bool Map(TableFeature f)
        {
            if (f.Reading.Verdict == LocationVerdict.Dropped)
            {
                return false;
            }

            if (Fits(f))
            {
                return true;
            }

            notices.Add(MalformedNotice(label, f.Id, f.Reading.Extent!, recordId, sequenceLength));
            outOfRange++;
            return false;
        }

        // #331: a feature's own coordinates must fit inside the record's sequence; only a single location ending in ">" may run past it.
        bool Fits(TableFeature f)
        {
            var extent = f.Reading.Extent!;
            var allowEndOverrun = extent.PartialEnd && !extent.IsCompound;
            return !(extent.Parts.Any(p => p.Start < 0 || p.End <= p.Start)
                || (!allowEndOverrun && extent.Parts.Any(p => p.End > sequenceLength)));
        }

        var mappedIds = new List<string>();
        var droppedGeneIds = new List<string>();
        foreach (var f in source)
        {
            if (Map(f))
            {
                mappedIds.Add(f.Id);
            }
            else if (geneFeatures.Count > 0 && f.Id != f.Key)
            {
                droppedGeneIds.Add(f.Id); // a gene with no name of its own cannot be matched to a CDS
            }
        }

        // #694: a gene feature that is dropped is replaced by the CDS feature carrying the same gene id, one stand-in per id, unless a kept
        // feature already carries that id.
        var fallbackIds = new List<string>();
        if (droppedGeneIds.Count > 0)
        {
            var wanted = droppedGeneIds.Where(id => !mappedIds.Contains(id)).ToHashSet(StringComparer.Ordinal);
            foreach (var cds in cdsFeatures)
            {
                if (wanted.Contains(cds.Id) && Map(cds))
                {
                    wanted.Remove(cds.Id);
                    fallbackIds.Add(cds.Id);
                }
            }

            if (fallbackIds.Count > 0)
            {
                var shown = string.Join(", ", fallbackIds.Take(DroppedShown).Select(id => PythonText.Ascii(CutLocationText(id))));
                var more = fallbackIds.Count > DroppedShown ? $", and {fallbackIds.Count - DroppedShown} more" : string.Empty;
                notices.Add($"{label} record {recordIndex}: {fallbackIds.Count} gene feature(s) could not be used, so the CDS feature with the same gene id was used instead ({shown}{more}). "
                    + "Its span is the coding sequence, not the full gene. Correct the gene feature's location in the source file to use the gene's own span.");
                coded.Add(new InputNotice(InputNoticeCode.GenBankGeneStoodInByCds, fallbackIds.Count, recordIndex));
            }
        }

        if (outOfRange > 0)
        {
            coded.Add(new InputNotice(InputNoticeCode.GenBankFeaturesOutOfRange, outOfRange, recordIndex));
        }

        // The counts are what survives: the genes kept, the CDS features readable and in range (a CDS beside genes is counted though it is not mapped).
        var mapped = mappedIds.Count + fallbackIds.Count;
        var genes = geneFeatures.Count(f => f.Reading.Verdict != LocationVerdict.Dropped && Fits(f));
        var cdsKept = cdsFeatures.Count(f => f.Reading.Verdict != LocationVerdict.Dropped && Fits(f));
        return (genes, cdsKept, mapped);
    }

    /// <summary>The worker's <c>_malformed_notice</c> text, word for word (an id is shown as Python's ascii() shows it, which is repr() for plain text).</summary>
    private static string MalformedNotice(string label, string id, LocationExtent extent, string recordId, int sequenceLength)
    {
        var shownId = PythonText.Ascii(id);
        if (extent.Parts.Any(p => p.End == p.Start))
        {
            return $"{label} feature {shownId} has a location between two bases (a site such as 1^2), not a span of bases, so it has no bases to measure; dropped from the gene features. "
                + "Give it a span in the source file (for example 1..42) and run again.";
        }

        if (extent.Parts.Any(p => p.End > long.MaxValue))
        {
            return $"{label} feature {shownId} has a position too large to be a real coordinate (more than {long.MaxValue}); dropped from the gene features. "
                + "Correct the position in the source file (for example 1..42) and run again.";
        }

        var begin = extent.Parts.Min(p => p.Start) + 1;
        var end = extent.Parts.Max(p => p.End);
        return $"{label} feature {shownId} has coordinates {begin}..{end}, which fall outside record {PythonText.Ascii(recordId)}'s own sequence length of {sequenceLength} nt; "
            + "dropped from the gene features (not reported) rather than kept with a wrong span. This means the file's feature table disagrees with its own "
            + "ORIGIN block; check the file was not truncated or hand-edited.";
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
        RecordIdentity? identity = null; // Biopython's record id, fed the header lines (#847)
        var originLine = -1; // the line of the ORIGIN header, to tell a file that ends right after it
        var tableChecked = false; // Biopython reads the feature table once per record, in its header
        long? locusLength = null; // the LOCUS line's length and topology: they decide how an origin-wrapping location reads
        var locusCircular = false;
        var tableFeatures = new List<TableFeature>(); // the record's features in table order, each with what Biopython made of its location

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
                // The worker measures Biopython's record id (the VERSION or ACCESSION text, else the LOCUS name), never the id itself.
                notices.Add($"Skipped GenBank record {recordIndex} (id {PrivacySafeText.DescribeLen(identity!.Id)}): no nucleotide sequence.");
                coded.Add(new InputNotice(InputNoticeCode.GenBankRecordSkippedNoSequence, recordIndex));
            }
            else
            {
                // Issue #543/#788: the worker drops a gene (or, in a record with no gene features, a CDS) whose location cannot
                // be read, with one counted notice per record; so does the pill's gene count.
                (geneCount, cdsCount, var mapped) = ReadTableFeatures("GenBank", recordIndex, tableFeatures, recSeq.Length, identity!.Id, notices, coded);
                records.Add(new GenBankLiteRecord(locusName, recSeq, geneCount, cdsCount, recordIndex) { MappedCount = mapped });
            }
            identity = null;
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
            // A LOCUS line while a record is open and its ORIGIN has not begun is header text: Biopython's parse_header reads it with the other
            // header lines, so it neither starts a record nor changes the first LOCUS line's name, length or topology (MEASURED 2026-10-29,
            // readers_parity.json genbank_second_locus_line_*).
            if (!inOrigin && locusName is null && rawLine.StartsWith("LOCUS", StringComparison.Ordinal))
            {
                var parts = rawLine.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                locusName = parts.Length > 1 ? parts[1] : string.Empty;
                identity = new RecordIdentity(locusName);
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

                        tableFeatures.Add(new TableFeature(feature.Key, feature.Id, reading));
                    }
                    tableChecked = true;
                }
                else if (GenBankFeatureTable.IsSequenceHeader(rawLine))
                {
                    tableChecked = true; // no feature table in this record
                }
                else if (PythonText.TrimEnd(rawLine) == "//")
                {
                    // Scanner.parse_header: a record terminator before any feature table or sequence header (#611).
                    throw GenBankFeatureTable.RefuseLikeWorker(recordIndex + 1, "Premature end of sequence data marker '//' found");
                }
                else
                {
                    identity?.FeedGenBankHeader(rawLine);
                }
            }
            if (PythonText.TrimEnd(rawLine) == "//") // Biopython compares the right-stripped line with "//" exactly
            {
                FlushRecord();
                continue;
            }
            if (IsOriginHeader(rawLine))
            {
                inOrigin = true;
                originLine = lineIndex;
                continue;
            }
        }
        if (locusName is not null)
        {
            // The last real line: the "" after a final newline is not a line.
            var lastLine = lines[^1].Length == 0 ? lines.Length - 2 : lines.Length - 1;
            if (!inOrigin)
            {
                // Scanner.parse_header / parse_footer: the file ends before the sequence block starts (header only), or right after a sequence
                // header line such as BASE COUNT (#611).
                throw GenBankFeatureTable.RefuseLikeWorker(recordIndex + 1, tableChecked ? "Premature end of file" : "Premature end of line during sequence data");
            }

            if (originLine == lastLine)
            {
                // parse_footer reads the line after the ORIGIN header and raises when there is none (MEASURED 2026-10-16). A file that ends
                // later, inside the sequence, is only warned about ("Premature end of file in sequence data"), so it is read.
                throw GenBankFeatureTable.RefuseLikeWorker(recordIndex + 1, "Premature end of file");
            }

            FlushRecord(); // file ended without a trailing "//"
        }

        if (records.Count == 0)
        {
            // readers/genbank.py: no LOCUS record at all, or every record skipped for want of a sequence. Same words, #689.
            throw new GenBankReadException(recordIndex == 0
                ? "No GenBank records found in the file."
                : "The GenBank file has no records with a nucleotide sequence (a truncated or sequence-less export). "
                  + "Re-export the file from the tool that made it, with its sequence included.");
        }

        // The worker counts the features it maps: a record's gene features, else its CDS features (records_from_parsed).
        var totalFeatures = records.Sum(r => r.Mapped);
        notices.Add($"Read {records.Count} record(s) with {totalFeatures} gene feature(s) from the GenBank (not re-annotated).");
        coded.Add(new InputNotice(InputNoticeCode.GenBankRead, records.Count, totalFeatures));

        return new GenBankLiteResult(records, notices) { NoticeCodes = coded };
    }
}
