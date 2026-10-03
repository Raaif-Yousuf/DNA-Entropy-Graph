using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DnaEntropyGraph.Core.Inputs;

namespace DnaEntropyGraph.Core.Diagnostics;

/// <summary>
/// The keys a known JSON file may carry into the bundle. A key not listed is dropped with its whole subtree (and
/// every name and string inside it joins the scrub list, so the same text is also removed from the logs); a listed
/// key with <c>null</c> is a leaf, a listed key with a <see cref="KeySpec"/> is a container whose children are checked
/// the same way. Arrays are transparent: each element is checked against the array key's spec.
/// </summary>
internal sealed class KeySpec(IReadOnlyDictionary<string, KeySpec?> keys)
{
    public IReadOnlyDictionary<string, KeySpec?> Keys { get; } = keys;

    public static KeySpec Of(params (string Key, KeySpec? Child)[] entries) =>
        new(entries.ToDictionary(e => e.Key, e => e.Child, StringComparer.Ordinal));

    /// <summary><c>status.json</c>: the worker's heartbeat snapshot (docs/job_contract.md section 4).</summary>
    public static KeySpec Status { get; } = Of(
        ("schema", null), ("jobId", null), ("stage", null), ("percent", null),
        ("detail", Of(("window", null), ("windows", null), ("direction", null))),
        ("startedAt", null), ("updatedAt", null), ("heartbeatSeq", null),
        ("vm", Of(("zone", null), ("gpu", null), ("driver", null))),
        ("worker", Of(("version", null), ("image", null))),
        ("error", Of(("code", null), ("retriable", null))));

    /// <summary>One line of <c>progress.jsonl</c>. The free-text <c>message</c> and <c>data</c> name contigs and inputs, so they are not kept.</summary>
    public static KeySpec Progress { get; } = Of(("seq", null), ("ts", null), ("stage", null), ("level", null), ("percent", null));

    /// <summary><c>result.json</c> (docs/job_contract.md section 7), without output paths, file lists or notices.</summary>
    public static KeySpec Result { get; } = Of(
        ("schema", null), ("jobId", null), ("status", null),
        ("inputs", Of(
            ("id", null), ("status", null),
            ("stats", Of(("contigs", null), ("totalNt", null), ("meanEntropy", null), ("minEntropy", null), ("maxEntropy", null))))),
        ("timing", Of(("startedAt", null), ("finishedAt", null))),
        ("gpu", Of(("name", null), ("zone", null), ("spot", null))),
        ("error", Of(("code", null), ("retriable", null))));
}

/// <summary>
/// Hard Rule 14's counterpart for support files: nothing that identifies the user's data leaves the machine.
/// JSON is cut down by per-file key allowlists (<see cref="KeySpec"/>) and a value allowlist (short, plain characters);
/// text is scrubbed of tokens, paths, known names, emails, file names and sequence in any layout.
/// <see cref="DiagnosticsLeakScan"/> is a separate, stricter implementation that checks the result.
/// </summary>
internal sealed partial class DiagnosticsRedactor
{
    public const string RedactedValue = "<redacted>";

    private static readonly JsonSerializerOptions CompactOptions = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    // Keys that describe the user's data or identity; used to HARVEST names from files we read but do not copy
    // (a run's manifest and options), so the same names can be scrubbed out of the logs.
    [GeneratedRegex("(?i)(name|file|path|sequence|seq|input|email|account|dir|folder|contig|record|bucket|project)")]
    private static partial Regex HarvestKey();

    [GeneratedRegex(@"^[A-Za-z0-9_.:+\- ]{0,60}$")]
    private static partial Regex SafeValue();

    [GeneratedRegex(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}")]
    private static partial Regex Email();

    // Any Windows path: a drive letter or a UNC share. The first form ends at a file extension, so a name with spaces
    // ("D:\Lab\Patient42\My Plasmid.fasta") is taken whole. The second is a folder path with no extension: a segment with
    // spaces is part of the path while a backslash follows it ("C:\Lab\Patient 42\out"), at most 80 characters per
    // segment, and the last segment ends at the next space. It over-redacts rather than leaving a folder name behind.
    [GeneratedRegex(@"(?:[A-Za-z]:|\\\\[^\\/\s""'<>|]+)[\\/](?:[^\\/:*?""<>|\r\n]+[\\/])*[^\\/:*?""<>|\r\n]*?\.[A-Za-z0-9]{1,8}(?![A-Za-z0-9])|(?:[A-Za-z]:|\\\\[^\\/\s""'<>|]+)[\\/](?:[^\\/:*?""<>|\r\n]{1,80}\\|[^\s\\/:*?""<>|]+/)*[^\s""'<>|]*")]
    private static partial Regex WindowsPath();

    // Credentials, in every shape the app or Google could print one.
    [GeneratedRegex(@"ya29\.[A-Za-z0-9_\-.]+|1//[A-Za-z0-9_\-]{10,}|eyJ[A-Za-z0-9_\-]+\.[A-Za-z0-9_\-]+\.[A-Za-z0-9_\-]*|GOCSPX-[A-Za-z0-9_\-]+|(?i:Bearer)\s+[A-Za-z0-9_\-.=~+/]+")]
    private static partial Regex Token();

    [GeneratedRegex(@"(?i)((?:access_token|refresh_token|id_token|client_secret)[""']?\s*[:=]\s*)[""']?[^\s""',}&]+")]
    private static partial Regex TokenPair();

    private static readonly Regex SequenceFileName = BuildFileNameRegex();

    private static Regex BuildFileNameRegex()
    {
        var extensions = SequenceFileTypes.Extensions
            .Concat([".fsa", ".ape", ".gp", ".sbd", ".seq", ".dna", ".gbk", ".gb", ".fa", ".fna", ".ffn", ".faa", ".fasta", ".fas", ".embl", ".gbff", ".genbank", ".txt"])
            .Select(e => Regex.Escape(e.TrimStart('.')))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        // Up to three words before the extension are taken with it, so "My Plasmid.fasta" goes whole.
        return new Regex(@"(?:[\w.\-()\[\]]+ ){0,3}[\w.\-()\[\]]+\.(?:" + string.Join('|', extensions) + @")(?![\w])", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    }

    private readonly string _profilePath;
    private readonly IReadOnlyList<SensitiveValue> _sensitive;

    public DiagnosticsRedactor(string userProfilePath, IEnumerable<string> sensitiveValues)
    {
        _profilePath = userProfilePath.TrimEnd('\\', '/');
        _sensitive = SensitiveValue.Normalize(sensitiveValues);
    }

    /// <summary>Text redaction for a whole log or file. Output uses LF line endings.</summary>
    public string RedactText(string text)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var result = new List<string>(lines.Length);
        for (var i = 0; i < lines.Length;)
        {
            // A block of consecutive sequence-looking lines (GenBank ORIGIN, 10-mers, a 60-column wrap, a sequence split
            // over two lines) is dropped whole when together they hold 20 or more characters.
            var end = i;
            var total = 0;
            while (end < lines.Length && SequenceShape.IsSequenceLine(lines[end], minLength: 6, out var length))
            {
                total += length;
                end++;
            }

            if (end > i && total >= 20)
            {
                result.Add("<sequence>");
                i = end;
                continue;
            }

            result.Add(RedactLine(lines[i]));
            i++;
        }

        return string.Join('\n', result);
    }

    private string RedactLine(string line)
    {
        line = TokenPair().Replace(line, "$1<token>");
        line = Token().Replace(line, "<token>");
        line = WindowsPath().Replace(line, match => IsUnderProfile(match.Value) ? "<user>" : "<path>");
        if (_profilePath.Length > 0)
        {
            line = line.Replace(_profilePath.Replace('\\', '/'), "<user>", StringComparison.OrdinalIgnoreCase);
        }

        foreach (var value in _sensitive)
        {
            line = value.Replace(line, RedactedValue);
        }

        line = Email().Replace(line, "<email>");
        line = SequenceFileName.Replace(line, "<file>");
        return SequenceShape.RedactRuns(line);
    }

    private bool IsUnderProfile(string path) =>
        _profilePath.Length > 0 && path.StartsWith(_profilePath, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True for any reason a piece of JSON could not be read: bad syntax (<see cref="JsonException"/>), a duplicate key
    /// (<see cref="ArgumentException"/>), or anything else the parser raises. Such a file is left out, never a failed export.
    /// </summary>
    public static bool IsUnreadableJson(Exception ex) => ex is not OutOfMemoryException;

    /// <summary>A parsed JSON document cut down to <paramref name="spec"/>; null when the text cannot be parsed.</summary>
    public string? RedactJson(string json, KeySpec spec)
    {
        try
        {
            return Sanitize(JsonNode.Parse(json), spec)?.ToJsonString(JsonOptions);
        }
        catch (Exception ex) when (IsUnreadableJson(ex))
        {
            return null;
        }
    }

    /// <summary>One JSON object per line; a line that does not parse is replaced by a note, never copied.</summary>
    public string RedactJsonLines(string text, KeySpec spec)
    {
        var lines = text.Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .Select(line =>
            {
                try
                {
                    return Sanitize(JsonNode.Parse(line), spec)?.ToJsonString(CompactOptions) ?? "null";
                }
                catch (Exception ex) when (IsUnreadableJson(ex))
                {
                    return "\"<unparseable line omitted>\"";
                }
            });
        return string.Join('\n', lines) + "\n";
    }

    private JsonNode? Sanitize(JsonNode? node, KeySpec? spec)
    {
        switch (node)
        {
            case JsonObject obj when spec is not null:
                var kept = new JsonObject();
                foreach (var (key, value) in obj)
                {
                    if (spec.Keys.TryGetValue(key, out var child) && (child is not null || value is not (JsonObject or JsonArray)))
                    {
                        kept[key] = Sanitize(value, child);
                    }
                }

                return kept;
            case JsonArray array when spec is not null:
                var items = new JsonArray();
                foreach (var item in array)
                {
                    items.Add(Sanitize(item, spec));
                }

                return items;
            case JsonValue value when value.TryGetValue<string>(out var text):
                return SafeValue().IsMatch(text) && !SequenceShape.IsSequenceLine(text, minLength: 20, out _) ? RedactText(text) : RedactedValue;
            case JsonValue:
                return node.DeepClone();
            default:
                // A container where the spec expects a leaf, or no spec at all: nothing of it is kept.
                return null;
        }
    }

    /// <summary>
    /// Every string in <paramref name="node"/> that the bundle will not copy (a subtree under a key outside
    /// <paramref name="spec"/>, or, with no spec, under a key that looks like data) goes into <paramref name="sink"/>, so
    /// the same text is scrubbed from the logs. A key inside such a subtree is added too when it looks like a name
    /// (<c>my_patient_42</c>) rather than a schema word (<c>contigs</c>): adding every key would scrub ordinary words.
    /// </summary>
    public static void Harvest(JsonNode? node, KeySpec? spec, ISet<string> sink, bool inDropped = false)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, value) in obj)
                {
                    if (inDropped)
                    {
                        if (LooksLikeData(key))
                        {
                            sink.Add(key);
                        }

                        Harvest(value, null, sink, inDropped: true);
                        continue;
                    }

                    KeySpec? child = null;
                    var known = spec is not null && spec.Keys.TryGetValue(key, out child);
                    if (spec is null)
                    {
                        Harvest(value, null, sink, HarvestKey().IsMatch(key));
                    }
                    else if (!known)
                    {
                        Harvest(value, null, sink, inDropped: true);
                    }
                    else if (child is not null)
                    {
                        Harvest(value, child, sink);
                    }
                }

                break;
            case JsonArray array:
                foreach (var item in array)
                {
                    Harvest(item, spec, sink, inDropped);
                }

                break;
            case JsonValue value when inDropped && value.TryGetValue<string>(out var text) && text.Length is >= 3 and <= 260:
                sink.Add(text);
                break;
        }
    }

    private static bool LooksLikeData(string key) =>
        key.Length >= 3 && key.Any(c => char.IsDigit(c) || c is '_' or '.' or '-' or ' ');
}

/// <summary>
/// One string the bundle must not carry (the user's name or email, an input name, a project). A value of 3 or more
/// characters is found as a substring; a 2-character one only as a whole word, so a user named "jo" is scrubbed without
/// mangling "job" or "json". A 1-character or all-digit value is dropped: it would match ordinary text and numbers.
/// </summary>
internal sealed class SensitiveValue
{
    private const int SubstringLength = 3;

    private readonly Regex _word;

    private SensitiveValue(string text, bool isFull)
    {
        Text = text;
        IsFull = isFull;
        _word = new Regex(@"(?<![\p{L}\p{N}])" + Regex.Escape(text) + @"(?![\p{L}\p{N}])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    public string Text { get; }

    /// <summary>True for a value as given; false for the file name or stem derived from it.</summary>
    public bool IsFull { get; }

    /// <summary>Each value as given, then its leaf and the leaf's stem, longest first (so "My Plasmid.fasta" goes before "My Plasmid").</summary>
    public static IReadOnlyList<SensitiveValue> Normalize(IEnumerable<string?> values)
    {
        var found = new Dictionary<string, SensitiveValue>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values.Where(v => !string.IsNullOrWhiteSpace(v)))
        {
            foreach (var (text, isFull) in Variants(value!.Trim()))
            {
                var trimmed = text.Trim();
                var usable = trimmed.Length >= 2 && !trimmed.All(char.IsDigit) && trimmed.Any(char.IsLetterOrDigit);
                if (usable && !(found.TryGetValue(trimmed, out var earlier) && (earlier.IsFull || !isFull)))
                {
                    found[trimmed] = new SensitiveValue(trimmed, isFull);
                }
            }
        }

        return found.Values.OrderByDescending(v => v.Text.Length).ToList();
    }

    private static IEnumerable<(string Text, bool IsFull)> Variants(string value)
    {
        yield return (value, true);
        var leaf = value.Split('\\', '/').LastOrDefault(part => part.Length > 0);
        if (leaf is null)
        {
            yield break;
        }

        if (leaf != value)
        {
            yield return (leaf, false);
        }

        var stem = Path.GetFileNameWithoutExtension(leaf);
        if (stem.Length > 0 && stem != leaf)
        {
            yield return (stem, false);
        }
    }

    /// <summary>The redactor's rule: substring for 3 or more characters, whole word for 2.</summary>
    public string Replace(string line, string replacement) =>
        Text.Length < SubstringLength ? _word.Replace(line, replacement) : line.Replace(Text, replacement, StringComparison.OrdinalIgnoreCase);

    /// <summary>The scan's rule: substring only for a full value of 3 or more characters; whole word otherwise.</summary>
    public bool IsIn(string text) =>
        Text.Length >= SubstringLength && IsFull ? text.Contains(Text, StringComparison.OrdinalIgnoreCase) : _word.IsMatch(text);
}

/// <summary>Sequence in any layout: IUPAC letters plus U, gaps and stops, case-insensitive, with digits and spaces ignored.</summary>
internal static class SequenceShape
{
    private const string Alphabet = "ACGTUNRYSWKMBDHV-*";

    public static bool InAlphabet(char c) => Alphabet.Contains(char.ToUpperInvariant(c), StringComparison.Ordinal);

    /// <summary>
    /// True when the line, with whitespace and digits removed, is at least <paramref name="minLength"/> long and at least 80%
    /// sequence characters (and holds real letters, not only gaps). <paramref name="length"/> is that stripped length.
    /// </summary>
    public static bool IsSequenceLine(string line, int minLength, out int length)
    {
        length = 0;
        var hits = 0;
        var letters = 0;
        foreach (var c in line)
        {
            if (char.IsWhiteSpace(c) || char.IsDigit(c))
            {
                continue;
            }

            length++;
            if (InAlphabet(c))
            {
                hits++;
                if (char.IsLetter(c))
                {
                    letters++;
                }
            }
        }

        return length >= minLength && letters >= 4 && hits * 5 >= length * 4;
    }

    private static readonly System.Text.RegularExpressions.Regex Run =
        new("[ACGTUNRYSWKMBDHVacgtunryswkmbdhv*\\-]{20,}", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Replaces every run of 20 or more sequence characters in a line.</summary>
    public static string RedactRuns(string line) =>
        Run.Replace(line, match => match.Value.Count(char.IsLetter) >= 16 ? "<sequence>" : match.Value);
}

/// <summary>
/// The last check before a zip is written. Deliberately not the redactor's code: it works on the raw entries, joins lines
/// and ignores whitespace and digits (so a wrapped, numbered or spaced sequence is still one run), also reads entry names,
/// and refuses on any credential shape. A hit means a redaction rule missed something; nothing is written.
/// </summary>
public static partial class DiagnosticsLeakScan
{
    [GeneratedRegex(@"ya29\.[\w\-.]{8,}|1//[\w\-]{10,}|eyJ[\w\-]{8,}\.[\w\-]{8,}|GOCSPX-[\w\-]{8,}|Bearer\s+[\w\-.=~+/]{12,}", RegexOptions.IgnoreCase)]
    private static partial Regex CredentialShape();

    [GeneratedRegex(@"(?:access_token|refresh_token|id_token|client_secret)\W{0,4}[\w\-.]{8,}", RegexOptions.IgnoreCase)]
    private static partial Regex CredentialPair();

    // A 32+ character hex digest holding a digit (an image digest, a sha256) is not sequence.
    [GeneratedRegex(@"[0-9a-fA-F]{32,}")]
    private static partial Regex HexDigest();

    // Its own copy of the alphabet, so a mistake in the redactor's list cannot also blind the scan.
    private const string Bases = "ACGTUNRYSWKMBDHV-*";

    // Its own email shape (not the redactor's), and its own drive or UNC test. A path never survives redaction, so one in
    // the finished entries is a miss: "X:\" or "X:/" not glued to a longer word ("https://" is not a drive), or "\\host\".
    [GeneratedRegex(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9\-]+(?:\.[A-Za-z0-9\-]+)*\.[A-Za-z]{2,}")]
    private static partial Regex EmailShape();

    [GeneratedRegex(@"(?<![A-Za-z0-9])[A-Za-z]:[\\/]|\\\\[\w.$\-]+[\\/]")]
    private static partial Regex DriveOrUncPath();

    // The fixed words the redactor and builder write in place of removed text; they carry no user data, so they are blanked
    // before the identifier check (a user named "user" must not make every bundle fail on "<user>").
    [GeneratedRegex(@"<(?:redacted|user|path|token|email|sequence|file)>|<omitted: [a-z ]{3,40}>|<unparseable line omitted>")]
    private static partial Regex Placeholder();

    /// <summary>Support addresses that may appear in a bundle. None yet; an address added here needs a test.</summary>
    private static readonly HashSet<string> AllowedEmails = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The name of the first entry (by name or by text) holding sequence-like or credential-like content, an email address,
    /// a drive or UNC path, or one of <paramref name="sensitiveValues"/>; null when the set is clean. An identifier is
    /// matched case-insensitively. A value of 3 or more characters hits as a substring, a 2-character value only as a
    /// whole word (so "jo" does not trip on "job" or "json"), and a 1-character or all-digit value is not checked at all (it
    /// would match ordinary text and numbers). A file name or path given as an identifier also matches by its leaf and
    /// stem, and those match as whole words only, so the folder "out" does not refuse a bundle that says "timeout".
    /// </summary>
    public static string? FindLeak(IReadOnlyDictionary<string, string> entries, IEnumerable<string>? sensitiveValues = null)
    {
        var sensitive = SensitiveValue.Normalize(sensitiveValues ?? []);
        foreach (var (name, text) in entries)
        {
            if (LooksLeaky(name, sensitive) || LooksLeaky(text, sensitive))
            {
                return name;
            }
        }

        return null;
    }

    private static bool LooksLeaky(string text, IReadOnlyList<SensitiveValue> sensitive)
    {
        if (CredentialShape().IsMatch(text) || CredentialPair().IsMatch(text) || DriveOrUncPath().IsMatch(text))
        {
            return true;
        }

        if (EmailShape().Matches(text).Any(match => !AllowedEmails.Contains(match.Value)))
        {
            return true;
        }

        var plain = Placeholder().Replace(text, " ");
        if (sensitive.Any(value => value.IsIn(plain)))
        {
            return true;
        }

        text = HexDigest().Replace(text, match => match.Value.Any(char.IsDigit) ? "|" : match.Value);

        var run = 0;
        var letters = 0;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c) || char.IsDigit(c))
            {
                continue;
            }

            if (Bases.Contains(char.ToUpperInvariant(c), StringComparison.Ordinal))
            {
                run++;
                if (char.IsLetter(c))
                {
                    letters++;
                }

                if (run >= 20 && letters >= 16)
                {
                    return true;
                }
            }
            else
            {
                run = 0;
                letters = 0;
            }
        }

        return false;
    }
}
