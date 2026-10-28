namespace DnaEntropyGraph.Core.Inputs;

/// <summary>
/// The id Biopython gives a record (<c>SeqRecord.id</c>), which the worker's skipped-record notice measures ("(id 12 chars)"): the first
/// accession (or the VERSION line's text), with the sequence version appended, else the LOCUS / ID name (#847). A port of
/// <c>Bio.GenBank._FeatureConsumer</c>'s <c>accession</c>, <c>version</c>, <c>version_suffix</c> and <c>record_end</c>, fed the header lines in file order.
/// </summary>
internal sealed class RecordIdentity(string name)
{
    private readonly List<string> _accessions = [];
    private string? _id;
    private string? _sequenceVersion;

    /// <summary>The id as <c>record_end</c> leaves it.</summary>
    public string Id
    {
        get
        {
            if (string.IsNullOrEmpty(_id))
            {
                return name;
            }

            return !_id.Contains('.', StringComparison.Ordinal) && _sequenceVersion is not null ? _id + "." + _sequenceVersion : _id;
        }
    }

    /// <summary>The id's length in code points, as Python's <c>len()</c> counts it.</summary>
    public int Length => Id.EnumerateRunes().Count();

    /// <summary>One GenBank header line: <c>ACCESSION</c> and <c>VERSION</c> matter, every other line does not.</summary>
    public void FeedGenBankHeader(string line)
    {
        var type = PythonText.Trim(PythonText.Slice(line, 0, 12));
        var data = PythonText.Trim(line.Length > 12 ? line[12..] : string.Empty);
        if (type == "ACCESSION")
        {
            Accession(data);
        }
        else if (type == "VERSION")
        {
            while (data.Contains("  ", StringComparison.Ordinal))
            {
                data = data.Replace("  ", " ", StringComparison.Ordinal);
            }

            var gi = data.IndexOf(" GI:", StringComparison.Ordinal);
            Version(gi >= 0 ? data[..gi] : data);
        }
    }

    /// <summary>The EMBL ID line of the 2006 layout: the accession is its first field, and <c>SV n</c> its second gives the version.</summary>
    public void FeedEmblIdLine(string line)
    {
        var rest = line.Length > 5 ? line[5..] : string.Empty;
        if (rest.Count(c => c == ';') != 6)
        {
            return; // the older layouts name the record only (their AC / SV lines give the id)
        }

        var fields = rest.Trim().Split(';').Select(f => f.Trim()).ToArray();
        Accession(fields[0]);
        var versionParts = fields[1].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (versionParts.Length == 2 && versionParts[0] == "SV" && IsDigits(versionParts[1]))
        {
            _sequenceVersion = Normalised(versionParts[1]);
        }
    }

    /// <summary>One EMBL header line: <c>AC</c> and the obsolete <c>SV</c> matter.</summary>
    public void FeedEmblHeader(string line)
    {
        var type = PythonText.Trim(PythonText.Slice(line, 0, 5));
        var data = PythonText.Trim(line.Length > 5 ? line[5..] : string.Empty);
        if (type == "AC")
        {
            Accession(data);
        }
        else if (type == "SV")
        {
            Version(data);
        }
    }

    private void Accession(string content)
    {
        foreach (var accession in content.Replace('\n', ' ').Replace(';', ' ').Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!_accessions.Contains(accession))
            {
                _accessions.Add(accession);
            }
        }

        if (string.IsNullOrEmpty(_id) && _accessions.Count > 0)
        {
            _id = _accessions[0];
        }
    }

    private void Version(string content)
    {
        var parts = content.Split('.');
        if (parts.Length == 2 && IsDigits(parts[1]))
        {
            Accession(parts[0]);
            _sequenceVersion = Normalised(parts[1]);
        }
        else if (content.Length > 0)
        {
            _id = content; // for backwards compatibility, Biopython takes the whole text as the id
        }
    }

    // Python's str.isdigit() on an ASCII-only version: one or more decimal digits.
    private static bool IsDigits(string s) => s.Length > 0 && s.All(c => c is >= '0' and <= '9');

    // "%i" % int(text): leading zeros drop.
    private static string Normalised(string digits) => System.Numerics.BigInteger.Parse(digits, System.Globalization.CultureInfo.InvariantCulture).ToString(System.Globalization.CultureInfo.InvariantCulture);
}
