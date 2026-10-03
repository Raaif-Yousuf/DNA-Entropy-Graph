namespace DnaEntropyGraph.Core.Inputs;

/// <summary>
/// C# port of the feature-table REFUSALS of Biopython's <c>Bio.GenBank.Scanner.GenBankScanner</c>
/// (<c>parse_features</c> and <c>parse_feature</c>), the parser behind the worker's
/// <c>readers/genbank.py</c>. Hard Rule 2: a file the worker refuses must be refused here, before a VM
/// is booted. Only what makes the scanner raise is ported (#548); the feature table is never turned
/// into data here, and a shape the scanner accepts (including the ones it only warns about) is accepted.
/// </summary>
internal static class GenBankFeatureTable
{
    private const int QualifierIndent = 21; // Scanner.FEATURE_QUALIFIER_INDENT
    private const int HeaderWidth = 12; // Scanner.HEADER_WIDTH
    private static readonly string Spacer = new(' ', QualifierIndent); // Scanner.FEATURE_QUALIFIER_SPACER
    private static readonly string[] StartMarkers = ["FEATURES             Location/Qualifiers", "FEATURES"];
    private static readonly string[] SequenceHeaders = ["CONTIG", "ORIGIN", "BASE COUNT", "WGS", "TSA", "TLS"];

    /// <summary>True for a line Biopython reads as the start of the feature table (<c>line.rstrip() in FEATURE_START_MARKERS</c>).</summary>
    public static bool IsStartMarker(string line) => Array.IndexOf(StartMarkers, PythonText.TrimEnd(line)) >= 0;

    /// <summary>True for a line Biopython reads as the start of the sequence block (<c>line[:12].rstrip() in SEQUENCE_HEADERS</c>).</summary>
    public static bool IsSequenceHeader(string line) => Array.IndexOf(SequenceHeaders, PythonText.TrimEnd(Slice(line, 0, HeaderWidth))) >= 0;

    /// <summary>
    /// Walks the feature table that starts at <paramref name="start" /> (a start-marker line) and throws
    /// <see cref="GenBankReadException" /> where the scanner would raise. <paramref name="recordNumber" /> is the
    /// 1-based record being read (named by index, never by id: a record id is user free text).
    /// </summary>
    public static void Validate(IReadOnlyList<string> lines, int start, int recordNumber)
    {
        var count = lines.Count > 0 && lines[^1].Length == 0 ? lines.Count - 1 : lines.Count; // the "" after a final newline is end of file
        // Python counts code points; fold each surrogate pair to one placeholder char so every length and column below does too.
        string? Raw(int i) => i < count ? FoldCodePoints(lines[i]) : null;

        var index = start;
        while (Raw(index) is { } marker && IsStartMarker(marker))
        {
            index++;
        }

        while (true)
        {
            var raw = Raw(index) ?? throw Refuse(recordNumber, "the feature table ends before the sequence starts (a truncated file)");
            if (IsSequenceHeader(raw))
            {
                return;
            }
            var line = PythonText.TrimEnd(raw);
            if (line == "//")
            {
                throw Refuse(recordNumber, "the feature table ends with the record terminator before the sequence starts");
            }
            if (PythonText.Trim(Slice(line, 2, QualifierIndent)).Length == 0)
            {
                index++; // an empty line between features
                continue;
            }
            if (line.Length < QualifierIndent)
            {
                index++; // Biopython only warns "line too short to contain a feature" and skips it
                continue;
            }
            if (line.Length == QualifierIndent)
            {
                // Biopython reads line[21] of the right-stripped line: an IndexError, "string index out of range".
                throw Refuse(recordNumber, "a feature-table line is shorter than the GenBank layout allows (a misaligned feature key or location)");
            }

            var featureLines = new List<string>();
            if (line[QualifierIndent] != ' ' && line.AsSpan(QualifierIndent).Contains(' '))
            {
                // Over-indented key (IMGT style): "key location" split on the first whitespace run.
                var rest = PythonText.Trim(line[2..]);
                var cut = rest.ToList().FindIndex(PythonText.IsSpace);
                if (cut < 0)
                {
                    throw Refuse(recordNumber, "a feature key has no location");
                }
                featureLines.Add(PythonText.TrimStart(rest[cut..]));
            }
            else
            {
                featureLines.Add(line[QualifierIndent..]);
            }
            index++;
            while (Raw(index) is { } next && (Slice(next, 0, QualifierIndent) == Spacer || PythonText.TrimEnd(next).Length == 0))
            {
                featureLines.Add(PythonText.Trim(next.Length > QualifierIndent ? next[QualifierIndent..] : string.Empty));
                index++; // a blank line in the middle of a feature belongs to it
            }
            ParseFeature(featureLines, recordNumber);
        }
    }

    // Scanner.parse_feature: the location (possibly wrapped over several lines), then the qualifiers.
    private static void ParseFeature(List<string> lines, int recordNumber)
    {
        var queue = new Queue<string>(lines.Where(l => l.Length > 0));
        string Next() => queue.Count > 0
            ? queue.Dequeue()
            : throw Refuse(recordNumber, "a feature in the feature table is incomplete (a location or a quoted qualifier is not closed)");

        var location = PythonText.Trim(Next());
        while (location.EndsWith(','))
        {
            location += PythonText.Trim(Next());
        }
        if (Count(location, '(') > Count(location, ')'))
        {
            while (location.EndsWith(',') || Count(location, '(') > Count(location, ')'))
            {
                location += PythonText.Trim(Next());
            }
        }

        bool lastValueIsNull = false;
        var hasQualifier = false;
        var iteration = 0;
        while (queue.Count > 0)
        {
            var line = queue.Dequeue();
            if (iteration++ == 0 && line.StartsWith(')'))
            {
                continue; // the location's closing parenthesis wrapped onto its own line
            }
            if (line[0] == '/')
            {
                var eq = line.IndexOf('=');
                if (eq < 0)
                {
                    lastValueIsNull = true; // a qualifier with no value, e.g. /pseudo
                    hasQualifier = true;
                    continue;
                }
                var value = line[(eq + 1)..];
                if (value.StartsWith(' ') && PythonText.TrimStart(value).StartsWith('"'))
                {
                    value = PythonText.TrimStart(value);
                }
                lastValueIsNull = false;
                hasQualifier = true;
                if (value.Length > 0 && value != "\"" && value[0] == '"')
                {
                    var last = value;
                    while (last[^1] != '"')
                    {
                        last = Next(); // a quoted value runs until a line ends with a quote
                    }
                }
                continue;
            }
            // A continuation line: it must extend an earlier qualifier that has a value.
            if (!hasQualifier || lastValueIsNull)
            {
                throw Refuse(recordNumber, "a feature qualifier line is missing its leading slash (it should start with '/', for example /gene=\"name\")");
            }
        }
    }

    private static string FoldCodePoints(string s) =>
        s.Any(char.IsSurrogate) ? string.Concat(s.EnumerateRunes().Select(r => r.IsBmp ? r.ToString() : "")) : s;

    private static int Count(string s, char c) => s.Count(x => x == c);

    // Python's s[a:b] never throws on a short string.
    private static string Slice(string s, int from, int to) => from >= s.Length ? string.Empty : s[from..Math.Min(to, s.Length)];

    private static GenBankReadException Refuse(int recordNumber, string reason) =>
        new($"Could not parse the GenBank file (record {recordNumber}): {reason}. Check the file was not truncated or hand-edited, then re-export it from the tool that made it.");
}
