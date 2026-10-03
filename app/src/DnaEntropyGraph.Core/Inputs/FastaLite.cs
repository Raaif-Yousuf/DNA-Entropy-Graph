namespace DnaEntropyGraph.Core.Inputs;

/// <summary>Raised when a FASTA file has no usable sequence records.</summary>
public sealed class FastaReadException(string message) : Exception(message)
{
    public const string ErrorCode = "INPUT_INVALID";
}

/// <summary>One raw (unvalidated) record from a FASTA file.</summary>
/// <param name="Header">Text after '&gt;', verbatim.</param>
/// <param name="Seq">Joined sequence lines, raw.</param>
/// <param name="SourceIndex">1-based position among ALL header lines in the file, counting records skipped for having no sequence (0 when built by hand). Lets a problem name the record the user sees.</param>
public sealed record FastaRecordRaw(string Header, string Seq, int SourceIndex = 0);

/// <summary>Result of reading a FASTA file/text: every record plus any non-fatal notices.</summary>
public sealed record FastaReadResult(IReadOnlyList<FastaRecordRaw> Records, IReadOnlyList<string> Notices)
{
    /// <summary>The same notices as <see cref="Notices"/>, as machine codes with their numbers (built side by side).</summary>
    public IReadOnlyList<InputNotice> NoticeCodes { get; init; } = [];
}

/// <summary>
/// Read a FASTA file into ALL its records - a hand-rolled, dependency-free C# port of
/// <c>worker/src/dna_entropy/readers/fasta.py::read_fasta</c> (issue #64) so the app can
/// sniff a dropped/typed FASTA file locally, with the SAME record-splitting and
/// duplicate-name detection the worker uses, before it ever creates a cloud resource
/// (Hard Rule 2). Returned sequences are raw (not yet validated/uppercased); run them
/// through <see cref="SequenceValidator" /> per record, exactly like the worker does.
/// </summary>
public static class FastaLite
{
    /// <summary>Read every record from <paramref name="path" /> (decoded via <see cref="TextDecoder" />).</summary>
    public static FastaReadResult ReadFile(string path) => Read(TextDecoder.ReadText(path));

    /// <summary>
    /// Return every record in already-decoded FASTA <paramref name="text" />.
    ///
    /// A record whose header line is followed by no sequence lines at all (empty after
    /// joining) is skipped with a notice. Missing (bare <c>&gt;</c>) or duplicated headers
    /// are tolerated - they never block reading or collide as contig names, since contig
    /// naming numbers by position, not by header text - but both are flagged with a
    /// notice since a biologist looking at the input may not have intended them.
    /// </summary>
    public static FastaReadResult Read(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var notices = new List<string>();
        var coded = new List<InputNotice>();
        var parsed = new List<(string Header, List<string> Lines)>();

        string? header = null;
        List<string> lines = [];
        // Python's str.splitlines() boundaries (also \v \f \x1c-\x1e \x85 U+2028 U+2029), as the worker does.
        foreach (var line in PythonText.SplitLines(text))
        {
            if (line.StartsWith('>'))
            {
                if (header is not null)
                {
                    parsed.Add((header, lines));
                }
                header = PythonText.Trim(line[1..]);
                lines = [];
            }
            else if (header is not null)
            {
                lines.Add(PythonText.Trim(line));
            }
        }
        if (header is not null)
        {
            parsed.Add((header, lines));
        }

        if (parsed.Count == 0)
        {
            throw new FastaReadException("No FASTA records found (expected a '>' header line).");
        }

        var records = new List<FastaRecordRaw>();
        for (var idx = 0; idx < parsed.Count; idx++)
        {
            var (recHeader, seqLines) = parsed[idx];
            var seq = string.Concat(seqLines);
            if (seq.Length == 0)
            {
                var headerDesc = recHeader.Length > 0 ? PrivacySafeText.DescribeLen(recHeader) : "no header text";
                // 1-based position among ALL parsed records (Python's enumerate(parsed, start=1)).
                notices.Add($"Skipped FASTA record {idx + 1} ({headerDesc}): no sequence lines after its header.");
                coded.Add(new InputNotice(InputNoticeCode.RecordSkippedNoSequence, idx + 1));
                continue;
            }
            records.Add(new FastaRecordRaw(recHeader, seq, idx + 1));
        }

        if (records.Count == 0)
        {
            throw new FastaReadException("The FASTA file has no records with a sequence.");
        }

        var nMissingHeader = records.Count(r => r.Header.Length == 0);
        if (nMissingHeader > 0)
        {
            notices.Add($"{nMissingHeader} record(s) have an empty header line (a bare '>' with no name).");
            coded.Add(new InputNotice(InputNoticeCode.EmptyHeaders, nMissingHeader));
        }

        // issue #351: key on the record ID - the header up to its first whitespace, the
        // part every other FASTA-consuming tool treats as the sequence's identifier - not
        // the full header line.
        var seen = new Dictionary<string, int>();
        foreach (var r in records)
        {
            if (r.Header.Length == 0)
            {
                continue;
            }
            var recId = PythonText.FirstWord(r.Header);
            seen[recId] = seen.GetValueOrDefault(recId) + 1;
        }
        var dupes = seen.Where(kv => kv.Value > 1).Select(kv => kv.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();
        if (dupes.Count > 0)
        {
            var shown = string.Join(", ", dupes.Take(5).Select(PrivacySafeText.Fingerprint));
            var more = dupes.Count > 5 ? "..." : string.Empty;
            notices.Add(
                $"{dupes.Count} id(s) repeat across records (fingerprints: {shown}{more}); "
                + "records are still kept and numbered separately.");
            coded.Add(new InputNotice(InputNoticeCode.RepeatedIds, dupes.Count));
        }

        if (records.Count > 1)
        {
            notices.Add($"Read {records.Count} record(s) from the FASTA (all processed).");
            coded.Add(new InputNotice(InputNoticeCode.RecordsRead, records.Count));
        }

        return new FastaReadResult(records, notices) { NoticeCodes = coded };
    }
}
