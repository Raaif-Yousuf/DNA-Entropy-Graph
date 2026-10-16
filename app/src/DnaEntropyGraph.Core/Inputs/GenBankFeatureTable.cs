namespace DnaEntropyGraph.Core.Inputs;

/// <summary>One feature of a record's feature table: its key (<c>gene</c>, <c>CDS</c> ...) and its location text, lines joined, as the scanner hands it to the parser.</summary>
internal sealed record GenBankTableFeature(string Key, string Location);

/// <summary>
/// What differs between the two scanners that share Biopython's <c>InsdcScanner.parse_features</c>: GenBank (<c>FEATURES</c>, columns of spaces)
/// and EMBL (<c>FH</c> / <c>FT</c>, a <c>FT</c> prefix on every line, <c>XX</c> ending the table, #796).
/// </summary>
internal sealed record FeatureTableFormat(
    string Label,
    string[] StartMarkers,
    string[] EndMarkers,
    string[] SequenceHeaders,
    int HeaderWidth,
    string Spacer,
    string Tail)
{
    private const int QualifierIndent = 21; // FEATURE_QUALIFIER_INDENT, the same in both scanners

    /// <summary>Bio.GenBank.Scanner.GenBankScanner; the refusal action is the worker's genbank.py text.</summary>
    public static FeatureTableFormat GenBank { get; } = new(
        "GenBank",
        ["FEATURES             Location/Qualifiers", "FEATURES"],
        [],
        ["CONTIG", "ORIGIN", "BASE COUNT", "WGS", "TSA", "TLS"],
        12,
        new string(' ', QualifierIndent),
        ", and that its ORIGIN block matches its own LOCUS/FEATURES.");

    /// <summary>Bio.GenBank.Scanner.EmblScanner; the refusal action is the worker's embl.py text.</summary>
    public static FeatureTableFormat Embl { get; } = new(
        "EMBL",
        ["FH   Key             Location/Qualifiers", "FH"],
        ["XX"],
        ["SQ", "CO"],
        5,
        "FT" + new string(' ', QualifierIndent - 2),
        ", or re-export it from the tool that made it.");
}

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

    /// <summary>True for a line Biopython reads as the start of the feature table (<c>line.rstrip() in FEATURE_START_MARKERS</c>).</summary>
    public static bool IsStartMarker(string line, FeatureTableFormat? format = null) =>
        Array.IndexOf((format ?? FeatureTableFormat.GenBank).StartMarkers, PythonText.TrimEnd(line)) >= 0;

    /// <summary>True for a line Biopython reads as the start of the sequence block (<c>line[:HEADER_WIDTH].rstrip() in SEQUENCE_HEADERS</c>).</summary>
    public static bool IsSequenceHeader(string line, FeatureTableFormat? format = null)
    {
        format ??= FeatureTableFormat.GenBank;
        return Array.IndexOf(format.SequenceHeaders, PythonText.TrimEnd(Slice(line, 0, format.HeaderWidth))) >= 0;
    }

    /// <summary>
    /// Walks the feature table that starts at <paramref name="start" /> (a start-marker line) and throws
    /// <see cref="GenBankReadException" /> where the scanner would raise. <paramref name="recordNumber" /> is the
    /// 1-based record being read (named by index, never by id: a record id is user free text).
    /// </summary>
    public static List<GenBankTableFeature> Validate(IReadOnlyList<string> lines, int start, int recordNumber) =>
        Walk(FeatureTableFormat.GenBank, lines, start, recordNumber, out _);

    /// <summary>
    /// <see cref="Validate" /> for either scanner. <paramref name="end" /> is the index of the line that ended the table (the sequence
    /// header, or EMBL's <c>XX</c>), not consumed.
    /// </summary>
    public static List<GenBankTableFeature> Walk(FeatureTableFormat format, IReadOnlyList<string> lines, int start, int recordNumber, out int end)
    {
        var features = new List<GenBankTableFeature>();
        var count = lines.Count > 0 && lines[^1].Length == 0 ? lines.Count - 1 : lines.Count; // the "" after a final newline is end of file
        // Python counts code points; fold each surrogate pair to one placeholder char so every length and column below does too.
        // Each distinct supplementary code point gets its own private-use character, so a location keeps its identity for the
        // parser and the notice (Unfold puts the code point back).
        var placeholders = new Dictionary<int, char>();
        var originals = new Dictionary<char, int>();
        string Fold(string s) => s.Any(char.IsSurrogate)
            ? string.Concat(s.EnumerateRunes().Select(r =>
            {
                if (r.IsBmp)
                {
                    return r.ToString();
                }

                if (!placeholders.TryGetValue(r.Value, out var placeholder))
                {
                    placeholder = (char)Math.Min(0xE000 + placeholders.Count, 0xF8FF);
                    placeholders[r.Value] = placeholder;
                    originals[placeholder] = r.Value;
                }

                return placeholder.ToString();
            }))
            : s;
        string Unfold(string s) => originals.Count == 0 ? s : string.Concat(s.Select(c => originals.TryGetValue(c, out var v) ? char.ConvertFromUtf32(v) : c.ToString()));
        string? Raw(int i) => i < count ? Fold(lines[i]) : null;

        var index = start;
        while (Raw(index) is { } marker && IsStartMarker(marker, format))
        {
            index++;
        }

        while (true)
        {
            var raw = Raw(index) ?? throw RefuseLikeWorker(recordNumber, "Premature end of line during features table", format);
            if (IsSequenceHeader(raw, format))
            {
                end = index;
                return features;
            }
            var line = PythonText.TrimEnd(raw);
            if (line == "//")
            {
                throw RefuseLikeWorker(recordNumber, "Premature end of features table, marker '//' found", format);
            }
            if (Array.IndexOf(format.EndMarkers, line) >= 0)
            {
                // EmblScanner.parse_footer: the line AFTER the end marker must be the SQ / CO header, or the file is refused. The table walk
                // above runs over a following record's ID and FH lines as if they were features, so a record that lacks its XX / SQ / //
                // ends up here (MEASURED 2026-10-16: "Footer format unexpected"). The text the worker echoes is user text, so it is left out.
                if (!IsSequenceHeader(Raw(index + 1) ?? string.Empty, format))
                {
                    throw RefuseLikeWorker(recordNumber, "Footer format unexpected", format);
                }

                end = index;
                return features;
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
                throw Refuse(recordNumber, "a feature-table line is shorter than the GenBank layout allows (a misaligned feature key or location)", format);
            }

            var featureLines = new List<string>();
            string key;
            if (line[QualifierIndent] != ' ' && line.AsSpan(QualifierIndent).Contains(' '))
            {
                // Over-indented key (IMGT style): "key location" split on the first whitespace run.
                var rest = PythonText.Trim(line[2..]);
                var cut = rest.ToList().FindIndex(PythonText.IsSpace);
                if (cut < 0)
                {
                    throw Refuse(recordNumber, "a feature key has no location", format);
                }
                key = rest[..cut];
                featureLines.Add(PythonText.TrimStart(rest[cut..]));
            }
            else
            {
                key = PythonText.Trim(Slice(line, 2, QualifierIndent));
                featureLines.Add(line[QualifierIndent..]);
            }
            index++;
            while (Raw(index) is { } next && (Slice(next, 0, QualifierIndent) == format.Spacer || PythonText.TrimEnd(next).Length == 0))
            {
                featureLines.Add(PythonText.Trim(next.Length > QualifierIndent ? next[QualifierIndent..] : string.Empty));
                index++; // a blank line in the middle of a feature belongs to it
            }
            features.Add(new GenBankTableFeature(Unfold(key), Unfold(ParseFeature(featureLines, recordNumber, format))));
        }
    }

    // Scanner.parse_feature: the location (possibly wrapped over several lines), then the qualifiers. Returns the location text.
    private static string ParseFeature(List<string> lines, int recordNumber, FeatureTableFormat format)
    {
        var queue = new Queue<string>(lines.Where(l => l.Length > 0));
        string Next() => queue.Count > 0
            ? queue.Dequeue()
            : throw Refuse(recordNumber, "a feature in the feature table is incomplete (a location or a quoted qualifier is not closed)", format);

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
                // Scanner.parse_feature: the location's closing parenthesis wrapped onto its own line is APPENDED to the location. When
                // the location is already balanced the text is then "1..42)", which the location parser refuses (#611).
                location += PythonText.Trim(line);
                continue;
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
                throw Refuse(recordNumber, "a feature qualifier line is missing its leading slash (it should start with '/', for example /gene=\"name\")", format);
            }
        }

        return location;
    }

    private static int Count(string s, char c) => s.Count(x => x == c);

    // Python's s[a:b] never throws on a short string.
    private static string Slice(string s, int from, int to) => from >= s.Length ? string.Empty : s[from..Math.Min(to, s.Length)];

    /// <summary>The refusal for a feature location Biopython raises on (not one it drops with a warning): named by record index, never by text (a location is user free text).</summary>
    public static GenBankReadException RefuseLocation(int recordNumber, FeatureTableFormat? format = null) =>
        Refuse(recordNumber, $"a feature location is not valid {(format ?? FeatureTableFormat.GenBank).Label} (it cannot be read as a position or a range)", format);

    /// <summary>
    /// A refusal for a shape Biopython's own scanner raises on with a fixed text (<paramref name="biopythonReason" />, MEASURED), worded
    /// exactly as the worker's <c>read_genbank</c> / <c>read_embl</c> words it so a user who sees the app's text first and the worker's later
    /// reads one message (#611).
    /// </summary>
    public static GenBankReadException RefuseLikeWorker(int recordNumber, string biopythonReason, FeatureTableFormat? format = null)
    {
        format ??= FeatureTableFormat.GenBank;
        return new($"Could not parse the {format.Label} file (record {recordNumber}): {biopythonReason}. Check the file was not truncated or hand-edited{format.Tail}");
    }

    private static GenBankReadException Refuse(int recordNumber, string reason, FeatureTableFormat? format) => RefuseLikeWorker(recordNumber, reason, format);
}
