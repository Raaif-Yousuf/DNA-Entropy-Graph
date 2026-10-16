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
        var inSeq = false;
        var tableChecked = false; // Biopython reads the feature table once per record
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
                notices.Add($"Skipped EMBL record {recordIndex}: no nucleotide sequence.");
                coded.Add(new InputNotice(InputNoticeCode.GenBankRecordSkippedNoSequence, recordIndex));
            }
            else
            {
                // #796: the same dropped-location rule and counts as a GenBank record (the worker shares records_from_parsed).
                var (genes, cds) = GenBankLite.ReadTableFeatures("EMBL", recordIndex, tableFeatures, notices, coded);
                records.Add(new GenBankLiteRecord(string.Empty, recSeq, genes, cds, recordIndex));
            }
            open = false;
            inSeq = false;
            tableChecked = false;
            idLength = null;
            idCircular = false;
            tableFeatures.Clear();
            seq.Clear();
        }

        for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            var line = lines[lineIndex];
            if (line.StartsWith("//", StringComparison.Ordinal))
            {
                if (open)
                {
                    Flush();
                }
                continue;
            }
            if (!open)
            {
                open = IsIdLine(line);
                if (open)
                {
                    (idLength, idCircular) = ReadIdLine(line);
                }
                continue;
            }
            if (IsIdLine(line))
            {
                // THEORY (unverified): Biopython's EMBL scanner refuses a record with no "//" before the next ID, as it does at
                // the end of the file; this does the same, so a truncated or hand-joined file is caught before a VM exists.
                throw new GenBankReadException("An EMBL record is missing its closing // line, so the file looks truncated or edited by hand.");
            }
            if (!inSeq && !tableChecked)
            {
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

                    tableChecked = true;
                    lineIndex = end - 1; // resume at the line that ended the table
                    continue;
                }

                if (GenBankFeatureTable.IsSequenceHeader(line, FeatureTableFormat.Embl))
                {
                    tableChecked = true; // no feature table in this record
                }
            }
            if (line.StartsWith("SQ", StringComparison.Ordinal))
            {
                inSeq = true;
            }
            else if (inSeq && line.Length > 0 && char.IsWhiteSpace(line[0]))
            {
                // readers/embl.py::_refuse_non_ascii_sequence skips spaces, tabs and ASCII digits (the layout and the
                // trailing position counts); only the real letters are left. ASCII lower case is raised by hand so a
                // non-ASCII letter that .NET or Python would fold into a valid base reaches the validator untouched.
                foreach (var c in line)
                {
                    if (c is ' ' or '\t' or (>= '0' and <= '9'))
                    {
                        continue;
                    }
                    seq.Append(c is >= 'a' and <= 'z' ? (char)(c - 32) : c);
                }
            }
            else if (inSeq)
            {
                inSeq = false;
            }
        }

        if (open)
        {
            throw new GenBankReadException("The EMBL file ends before its closing // line, so it looks truncated.");
        }
        if (records.Count == 0)
        {
            throw new GenBankReadException("No EMBL records with a nucleotide sequence found (expected an ID line and an SQ block).");
        }

        // The worker counts the features it maps: a record's gene features, else its CDS features (records_from_parsed).
        var totalFeatures = records.Sum(r => r.GeneCount > 0 ? r.GeneCount : r.CdsCount);
        notices.Add($"Read {records.Count} record(s) with {totalFeatures} gene feature(s) from the EMBL (not re-annotated).");
        coded.Add(new InputNotice(InputNoticeCode.GenBankRead, records.Count, totalFeatures));

        return new GenBankLiteResult(records, notices) { NoticeCodes = coded };
    }

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
