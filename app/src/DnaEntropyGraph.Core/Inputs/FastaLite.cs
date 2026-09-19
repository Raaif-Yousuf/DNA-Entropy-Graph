namespace DnaEntropyGraph.Core.Inputs;

/// <summary>Raised when a FASTA file has no usable sequence records.</summary>
public sealed class FastaReadException(string message) : Exception(message)
{
    public const string ErrorCode = "INPUT_INVALID";
}

/// <summary>One raw (unvalidated) record from a FASTA file.</summary>
public sealed record FastaRecordRaw(string Header, string Seq);

/// <summary>Result of reading a FASTA file/text: every record plus any non-fatal notices.</summary>
public sealed record FastaReadResult(IReadOnlyList<FastaRecordRaw> Records, IReadOnlyList<string> Notices);

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
        var parsed = new List<(string Header, List<string> Lines)>();

        string? header = null;
        List<string> lines = [];
        foreach (var line in text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\r", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (line.StartsWith('>'))
            {
                if (header is not null)
                {
                    parsed.Add((header, lines));
                }
                header = line[1..].Trim();
                lines = [];
            }
            else if (header is not null)
            {
                lines.Add(line.Trim());
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
                continue;
            }
            records.Add(new FastaRecordRaw(recHeader, seq));
        }

        if (records.Count == 0)
        {
            throw new FastaReadException("The FASTA file has no records with a sequence.");
        }

        var nMissingHeader = records.Count(r => r.Header.Length == 0);
        if (nMissingHeader > 0)
        {
            notices.Add($"{nMissingHeader} record(s) have an empty header line (a bare '>' with no name).");
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
            var recId = r.Header.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries)[0];
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
        }

        if (records.Count > 1)
        {
            notices.Add($"Read {records.Count} record(s) from the FASTA (all processed).");
        }

        return new FastaReadResult(records, notices);
    }
}
