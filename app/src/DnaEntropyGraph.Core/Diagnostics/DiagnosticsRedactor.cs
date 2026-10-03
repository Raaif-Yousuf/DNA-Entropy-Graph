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
    // ("D:\Lab\Patient42\My Plasmid.fasta") is taken whole; the second takes a folder path up to the next space.
    [GeneratedRegex(@"(?:[A-Za-z]:|\\\\[^\\/\s""'<>|]+)[\\/](?:[^\\/:*?""<>|\r\n]+[\\/])*[^\\/:*?""<>|\r\n]*?\.[A-Za-z0-9]{1,8}(?![A-Za-z0-9])|(?:[A-Za-z]:|\\\\[^\\/\s""'<>|]+)[\\/][^\s""'<>|]*")]
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
    private readonly IReadOnlyList<string> _sensitive;

    public DiagnosticsRedactor(string userProfilePath, IEnumerable<string> sensitiveValues)
    {
        _profilePath = userProfilePath.TrimEnd('\\', '/');
        // Longest first, so "My Plasmid.fasta" is replaced whole before its stem "My Plasmid" is.
        _sensitive = sensitiveValues
            .SelectMany(Variants)
            .Select(value => value.Trim())
            .Where(value => value.Length >= 3 && !value.All(char.IsDigit))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(value => value.Length)
            .ToList();
    }

    // A path scrubs as itself, as its file name and as that file name's stem.
    private static IEnumerable<string> Variants(string value)
    {
        yield return value;
        var leaf = value.Split('\\', '/').LastOrDefault(part => part.Length > 0);
        if (leaf is not null && leaf != value)
        {
            yield return leaf;
        }

        if (leaf is not null)
        {
            var stem = Path.GetFileNameWithoutExtension(leaf);
            if (stem.Length > 0 && stem != leaf)
            {
                yield return stem;
            }
        }
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
            line = line.Replace(value, RedactedValue, StringComparison.OrdinalIgnoreCase);
        }

        line = Email().Replace(line, "<email>");
        line = SequenceFileName.Replace(line, "<file>");
        return SequenceShape.RedactRuns(line);
    }

    private bool IsUnderProfile(string path) =>
        _profilePath.Length > 0 && path.StartsWith(_profilePath, StringComparison.OrdinalIgnoreCase);

    /// <summary>A parsed JSON document cut down to <paramref name="spec"/>; null when the text is not JSON.</summary>
    public string? RedactJson(string json, KeySpec spec)
    {
        try
        {
            return Sanitize(JsonNode.Parse(json), spec)?.ToJsonString(JsonOptions);
        }
        catch (JsonException)
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
                catch (JsonException)
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

    /// <summary>The name of the first entry (by name or by text) holding sequence-like or credential-like content, or null when the set is clean.</summary>
    public static string? FindLeak(IReadOnlyDictionary<string, string> entries)
    {
        foreach (var (name, text) in entries)
        {
            if (LooksLeaky(name) || LooksLeaky(text))
            {
                return name;
            }
        }

        return null;
    }

    private static bool LooksLeaky(string text)
    {
        if (CredentialShape().IsMatch(text) || CredentialPair().IsMatch(text))
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
