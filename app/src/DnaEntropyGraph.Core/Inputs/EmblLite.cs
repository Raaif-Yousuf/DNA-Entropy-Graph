using System.Globalization;
using System.Text;

namespace DnaEntropyGraph.Core.Inputs;

/// <summary>
/// A light local read of an EMBL flat file (issue #685, Hard Rule 2): the worker's <c>readers/embl.py</c> (Biopython
/// behind it) stays the source of truth; this reads enough to pre-flight a file before any cloud resource exists.
/// It returns the same shape as <see cref="GenBankLite"/> so the validator treats every annotated format alike.
/// </summary>
public static class EmblLite
{
    /// <summary>True when a line opens an EMBL record: <c>ID</c> followed by three spaces, as the worker's sniff does.</summary>
    public static bool IsIdLine(string line) => line.StartsWith("ID   ", StringComparison.Ordinal);

    public static GenBankLiteResult ReadFile(string path) => Read(TextDecoder.ReadText(path));

    public static GenBankLiteResult Read(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\r", "\n", StringComparison.Ordinal).Split('\n');
        var records = new List<GenBankLiteRecord>();
        var notices = new List<string>();
        var coded = new List<InputNotice>();

        var open = false;
        RecordIdentity? identity = null; // Biopython's record id, fed the ID line and the AC / SV lines (#847)
        var phase = Phase.Header;
        long? idLength = null; // the ID line's length and topology decide how an origin-wrapping location reads
        var idCircular = false;
        var tableFeatures = new List<(string Key, string? DroppedText)>();
        var recordIndex = 0;
        var seq = new StringBuilder();

        void Flush()
        {
            recordIndex++;
            var recSeq = seq.ToString();
            // readers/genbank.py::records_from_parsed, shared by the EMBL reader: no sequence, or only N, is skipped.
            if (recSeq.Length == 0 || recSeq.All(c => c == 'N'))
            {
                notices.Add($"Skipped EMBL record {recordIndex} (id {PrivacySafeText.DescribeLen(identity!.Id)}): no nucleotide sequence.");
                coded.Add(new InputNotice(InputNoticeCode.GenBankRecordSkippedNoSequence, recordIndex));
            }
            else
            {
                // #796: the same dropped-location rule and counts as a GenBank record (the worker shares records_from_parsed).
                var (genes, cds, mapped) = GenBankLite.ReadTableFeatures("EMBL", recordIndex, tableFeatures, notices, coded);
                records.Add(new GenBankLiteRecord(string.Empty, recSeq, genes, cds, recordIndex) { MappedCount = mapped });
            }
            identity = null;
            open = false;
            phase = Phase.Header;
            idLength = null;
            idCircular = false;
            tableFeatures.Clear();
            seq.Clear();
        }

        // The "" after a final newline is the end of the file, not a blank line (the worker's readline() returns "" there).
        var lineCount = lines.Length > 0 && lines[^1].Length == 0 ? lines.Length - 1 : lines.Length;
        for (var lineIndex = 0; lineIndex < lineCount; lineIndex++)
        {
            var line = lines[lineIndex];
            if (!open)
            {
                // Biopython's find_start skips everything before a line that opens a record ("ID   "); a stray "//" there is skipped too.
                if (IsIdLine(line))
                {
                    open = true;
                    RefuseUnrecognisedIdLine(line, recordIndex + 1);
                    (idLength, idCircular) = ReadIdLine(line);
                    var rest = line.Length > 5 ? line[5..] : string.Empty;
                    var name = rest.Count(c => c == ';') == 6 ? rest.Trim().Split(';')[0].Trim() : rest.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
                    identity = new RecordIdentity(name);
                    identity.FeedEmblIdLine(line);
                }
                continue;
            }

            switch (phase)
            {
                case Phase.Header:
                    if (line.StartsWith("//", StringComparison.Ordinal))
                    {
                        // Scanner.parse_header: a record terminator before the feature table or the sequence block (MEASURED 2026-10-29).
                        throw GenBankFeatureTable.RefuseLikeWorker(recordIndex + 1, "Premature end of sequence data marker '//' found", FeatureTableFormat.Embl);
                    }

                    if (GenBankFeatureTable.IsStartMarker(line, FeatureTableFormat.Embl))
                    {
                        // The worker's parser refuses a bad feature table (#548) and a location it cannot recover from (#788, #796), and it drops a
                        // feature whose location it cannot read (#543): the same walk and the same location verdict as a GenBank record.
                        var walked = GenBankFeatureTable.Walk(FeatureTableFormat.Embl, lines, lineIndex, recordIndex + 1, out var end);
                        foreach (var feature in walked)
                        {
                            var reading = GenBankLocation.Read(feature.Location, idLength, idCircular);
                            if (reading.Verdict == LocationVerdict.Refused)
                            {
                                throw GenBankFeatureTable.RefuseLocation(recordIndex + 1, FeatureTableFormat.Embl);
                            }

                            tableFeatures.Add((feature.Key, reading.Verdict == LocationVerdict.Dropped ? reading.QuotedText : null));
                        }

                        phase = Phase.AfterTable;
                        lineIndex = end - 1; // resume at the line that ended the table
                    }
                    else if (GenBankFeatureTable.IsSequenceHeader(line, FeatureTableFormat.Embl))
                    {
                        phase = Phase.SequenceHeader;
                    }
                    else
                    {
                        identity?.FeedEmblHeader(line); // any other line is header text, an ID line among them
                    }

                    break;

                case Phase.AfterTable:
                    // Walk has checked the line after the table's end marker is the sequence header; the end marker itself is header text.
                    if (GenBankFeatureTable.IsSequenceHeader(line, FeatureTableFormat.Embl))
                    {
                        phase = Phase.SequenceHeader;
                    }
                    else
                    {
                        identity?.FeedEmblHeader(line);
                    }

                    break;

                case Phase.SequenceHeader:
                    // Scanner.parse_footer: SQ / CO lines, then a line that is indented (five spaces) or is the terminator.
                    if (GenBankFeatureTable.IsSequenceHeader(line, FeatureTableFormat.Embl))
                    {
                        break;
                    }

                    var trimmed = PythonText.TrimEnd(line);
                    if (!(trimmed.StartsWith("     ", StringComparison.Ordinal) || PythonText.Trim(trimmed) == "//"))
                    {
                        throw GenBankFeatureTable.RefuseLikeWorker(recordIndex + 1, "Unexpected content after SQ or CO line", FeatureTableFormat.Embl);
                    }

                    phase = Phase.Sequence;
                    goto case Phase.Sequence;

                case Phase.Sequence:
                    // Scanner.parse_footer's sequence loop: "//" ends the record; a blank line is refused; a line is its text
                    // minus the whitespace-separated number that ends it (the position count), with its spaces removed.
                    var seqLine = PythonText.Trim(line);
                    if (seqLine.Length == 0)
                    {
                        throw GenBankFeatureTable.RefuseLikeWorker(recordIndex + 1, "Blank line in sequence data", FeatureTableFormat.Embl);
                    }

                    if (seqLine == "//")
                    {
                        Flush();
                        break;
                    }

                    AppendSequenceLine(seq, seqLine);
                    break;
            }
        }

        if (open)
        {
            // The scanner reads to the end of the file and raises, worded by where it stood (MEASURED 2026-10-29, the embl_file_ends_* cases).
            var reason = phase switch
            {
                Phase.SequenceHeader => "Premature end of file",
                Phase.Sequence => "Premature end of file in sequence data",
                _ => "Premature end of line during sequence data", // the header, or the table and its end marker
            };
            throw GenBankFeatureTable.RefuseLikeWorker(recordIndex + 1, reason, FeatureTableFormat.Embl);
        }
        if (records.Count == 0)
        {
            // readers/genbank.py::records_from_parsed with the EMBL label: no record at all, or every record skipped for want of a sequence.
            throw new GenBankReadException(recordIndex == 0
                ? "No EMBL records found in the file."
                : "The EMBL file has no records with a nucleotide sequence (a truncated or sequence-less export). "
                  + "Re-export the file from the tool that made it, with its sequence included.");
        }

        // The worker counts the features it maps: a record's gene features, else its CDS features (records_from_parsed).
        var totalFeatures = records.Sum(r => r.Mapped);
        notices.Add($"Read {records.Count} record(s) with {totalFeatures} gene feature(s) from the EMBL (not re-annotated).");
        coded.Add(new InputNotice(InputNoticeCode.GenBankRead, records.Count, totalFeatures));

        return new GenBankLiteResult(records, notices) { NoticeCodes = coded };
    }

    private enum Phase
    {
        Header, // after the ID line, before the feature table or the sequence block
        AfterTable, // the feature table has been walked; its end marker and the sequence header follow
        SequenceHeader, // SQ / CO lines
        Sequence, // the sequence lines, up to "//"
    }

    /// <summary>
    /// EmblScanner._feed_first_line: an ID line with six semicolons (2006 layout), three (older layout, or the patent layout ending SQ) or two
    /// (KIPO patent layout) is read; any other count is refused, named by record number (the worker echoes the line, which is user text).
    /// </summary>
    private static void RefuseUnrecognisedIdLine(string line, int recordNumber)
    {
        var semicolons = (line.Length > 5 ? line[5..] : string.Empty).Count(c => c == ';');
        if (semicolons is not (2 or 3 or 6))
        {
            throw GenBankFeatureTable.RefuseLikeWorker(recordNumber, "Did not recognise the ID line layout", FeatureTableFormat.Embl);
        }
    }

    /// <summary>
    /// Scanner.parse_footer, one sequence line (already stripped): <c>line.rsplit(None, 1)</c>, and when the last word is all digits it is the
    /// position count and is dropped; a line of digits only adds nothing; otherwise the whole line is sequence. Spaces (only) are removed; ASCII
    /// lower case is raised by hand so a non-ASCII letter that .NET or Python would fold into a valid base reaches the validator untouched.
    /// </summary>
    private static void AppendSequenceLine(StringBuilder seq, string line)
    {
        var cut = -1;
        for (var i = line.Length - 1; i >= 0; i--)
        {
            if (PythonText.IsSpace(line[i]))
            {
                cut = i;
                break;
            }
        }

        var text = line;
        if (cut >= 0)
        {
            var last = line[(cut + 1)..];
            if (IsAsciiDigits(last))
            {
                text = PythonText.TrimEnd(line[..cut]);
            }
        }
        else if (IsAsciiDigits(line))
        {
            return; // the final line with no bases: just the sequence coordinate
        }

        foreach (var c in text)
        {
            if (c != ' ')
            {
                seq.Append(c is >= 'a' and <= 'z' ? (char)(c - 32) : c);
            }
        }
    }

    private static bool IsAsciiDigits(string s) => s.Length > 0 && s.All(c => c is >= '0' and <= '9');
    /// <summary>
    /// The length and topology Biopython takes from an ID line (<c>EmblScanner._feed_first_line</c>): the 2006 layout has six semicolons
    /// (<c>ID   X56734; SV 1; linear; mRNA; STD; PLN; 1859 BP.</c>), the older one three
    /// (<c>ID   BSUB9999   standard; circular DNA; PRO; 4214630 BP.</c>). Any other layout gives neither (the worker refuses it elsewhere);
    /// only origin-wrapping locations depend on them.
    /// </summary>
    private static (long? Length, bool Circular) ReadIdLine(string line)
    {
        var rest = line.Length > 5 ? line[5..] : string.Empty;
        var semicolons = rest.Count(c => c == ';');
        if (semicolons == 6)
        {
            var fields = rest.Trim().Split(';').Select(f => f.Trim()).ToArray();
            return (ParseLength(fields[6]), fields[2] == "circular");
        }

        if (semicolons == 3 && !PythonText.TrimEnd(line).EndsWith(" SQ", StringComparison.Ordinal))
        {
            var parts = rest.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
            var fields = parts.Length == 2 ? parts[1].Split(';').Select(f => f.Trim()).ToArray() : [];
            return fields.Length >= 4 ? (ParseLength(fields[3]), fields[1].Contains("circular", StringComparison.Ordinal)) : (null, false);
        }

        return (null, false);
    }

    // "126 BP." -> 126: the number before the unit.
    private static long? ParseLength(string field)
    {
        var words = field.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return words.Length == 2 && long.TryParse(words[0], NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null;
    }
}
