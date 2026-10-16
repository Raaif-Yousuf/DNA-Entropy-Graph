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
        var genes = 0;
        var cds = 0;
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
                records.Add(new GenBankLiteRecord(string.Empty, recSeq, genes, cds, recordIndex));
            }
            open = false;
            inSeq = false;
            genes = 0;
            cds = 0;
            seq.Clear();
        }

        foreach (var line in lines)
        {
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
                continue;
            }
            if (IsIdLine(line))
            {
                // THEORY (unverified): Biopython's EMBL scanner refuses a record with no "//" before the next ID, as it does at
                // the end of the file; this does the same, so a truncated or hand-joined file is caught before a VM exists.
                throw new GenBankReadException("An EMBL record is missing its closing // line, so the file looks truncated or edited by hand.");
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
            else if (IsFeatureLine(line, "gene"))
            {
                genes++;
            }
            else if (IsFeatureLine(line, "CDS"))
            {
                cds++;
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
        if (records.Count > 1)
        {
            coded.Add(new InputNotice(InputNoticeCode.GenBankRead, records.Count, records.Sum(r => r.GeneCount + r.CdsCount)));
        }

        return new GenBankLiteResult(records, notices) { NoticeCodes = coded };
    }

    // "FT   gene            1..42": the key starts in column 6; a qualifier line is indented to column 22, so it never matches.
    private static bool IsFeatureLine(string line, string key)
        => line.StartsWith("FT   ", StringComparison.Ordinal)
           && line.AsSpan(5).StartsWith(key, StringComparison.Ordinal)
           && (line.Length == 5 + key.Length || char.IsWhiteSpace(line[5 + key.Length]));
}
