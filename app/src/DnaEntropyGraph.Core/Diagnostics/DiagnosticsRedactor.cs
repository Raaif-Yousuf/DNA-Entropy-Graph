using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DnaEntropyGraph.Core.Cloud;
using DnaEntropyGraph.Core.Inputs;

namespace DnaEntropyGraph.Core.Diagnostics;

/// <summary>
/// The keys a known JSON file may carry into the bundle. A key not listed is dropped with its whole subtree (and
/// every name and string inside it joins the scrub list, so the same text is also removed from the logs); a listed
/// key with <c>null</c> is a leaf, a listed key with a <see cref="KeySpec"/> is a container whose children are checked
/// the same way. Arrays are transparent: each element is checked against the array key's spec.
/// </summary>
internal sealed class KeySpec(IReadOnlyDictionary<string, KeySpec?> keys, Func<string, bool>? isAllowedValue = null)
{
    public IReadOnlyDictionary<string, KeySpec?> Keys { get; } = keys;

    /// <summary>For a leaf only: a string value ships only when this accepts it, otherwise it is replaced (an enum-valued field such as <c>error.code</c>, where plain-looking text is still not a known value).</summary>
    public Func<string, bool>? IsAllowedValue { get; } = isAllowedValue;

    /// <summary>A leaf whose string value must be one of <paramref name="allowed"/>.</summary>
    public static KeySpec OneOf(IReadOnlySet<string> allowed) => new(new Dictionary<string, KeySpec?>(), allowed.Contains);

    /// <summary><c>error</c> in <c>status.json</c> and <c>result.json</c>: the code must be one the worker's registry defines (#613).</summary>
    private static KeySpec ErrorSpec { get; } = Of(("code", OneOf(RunErrorCodes.WorkerErrorCodes)), ("retriable", null));

    /// <summary>Adds every key of this spec and of its children to <paramref name="sink"/>.</summary>
    public void CollectKeys(ISet<string> sink)
    {
        foreach (var (key, child) in Keys)
        {
            sink.Add(key);
            child?.CollectKeys(sink);
        }
    }

    public static KeySpec Of(params (string Key, KeySpec? Child)[] entries) =>
        new(entries.ToDictionary(e => e.Key, e => e.Child, StringComparer.Ordinal));

    /// <summary><c>status.json</c>: the worker's heartbeat snapshot (docs/job_contract.md section 4).</summary>
    public static KeySpec Status { get; } = Of(
        ("schema", null), ("jobId", null), ("stage", null), ("percent", null),
        ("detail", Of(("window", null), ("windows", null), ("direction", null))),
        ("startedAt", null), ("updatedAt", null), ("heartbeatSeq", null),
        ("vm", Of(("zone", null), ("gpu", null), ("driver", null))),
        ("worker", Of(("version", null), ("image", null))),
        ("error", ErrorSpec));

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
        ("error", ErrorSpec));
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

    private readonly Regex? _userText;

    /// <param name="userProfilePath">The Windows profile folder, redacted as a user placeholder.</param>
    /// <param name="sensitiveValues">Identifiers (names, projects, paths): matched as substrings, like the leak scan.</param>
    /// <param name="userText">
    /// The user's own free text (a run's tags and notes, issue #141). Matched as WHOLE tokens only, case-insensitively, in one
    /// pass, and never checked by the leak scan: a tag such as "completed" or "error" is an ordinary word the bundle itself
    /// writes, so a substring match would mangle fixed vocabulary and a scan would refuse every bundle.
    /// </param>
    public DiagnosticsRedactor(string userProfilePath, IEnumerable<string> sensitiveValues, IEnumerable<string>? userText = null)
    {
        _profilePath = userProfilePath.TrimEnd('\\', '/');
        _sensitive = SensitiveValue.Normalize(sensitiveValues);
        _userText = WholeTokenRegex(userText);
    }

    /// <summary>True when the text still holds one of the user's own words (see the <c>userText</c> argument) as a whole token.</summary>
    public bool ContainsUserText(string text) => _userText?.IsMatch(MarkerWord().Replace(text, " ")) == true;

    // The fixed markers this redactor and the bundle builder write. They carry no user text, so the backstop must not read their
    // words ("user", "path", "redacted") as the user's own.
    [GeneratedRegex(@"<(?:redacted|user|path|token|email|sequence|file)>|<omitted: [a-z ]{3,40}>|<unparseable line omitted>")]
    private static partial Regex MarkerWord();

    // DECISION (agent-made, reversible): words that are the bundle's own vocabulary are never scrubbed as "user text". The app never
    // writes a note or tag into a log, so such a word in a log is the log's own: scrubbing a tag "info" blanked every log level and a
    // tag "level" or "status" made the backstop withhold progress.jsonl, status.json and result.json (cold review of #141).
    // Distinctive text (a digit, a punctuation mark, an uncommon word) and every multi-word note is still scrubbed.
    private static readonly HashSet<string> LogVocabulary = BuildLogVocabulary();

    private static HashSet<string> BuildLogVocabulary()
    {
        var words = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "debug", "trace", "info", "information", "warn", "warning", "error", "errors", "fatal", "critical", "failed", "failure", "fail",
            "ok", "done", "completed", "complete", "success", "succeeded", "running", "started", "stopped", "cancelled", "canceled",
            "pending", "queued", "none", "null", "true", "false", "unknown", "message", "detail", "details", "job", "run", "runs",
            "redacted", "user", "path", "token", "email", "sequence", "file", "omitted", "withheld",
        };
        foreach (var phase in Enum.GetNames<JobPhase>())
        {
            words.Add(phase);
        }

        foreach (var spec in new[] { KeySpec.Status, KeySpec.Progress, KeySpec.Result })
        {
            spec.CollectKeys(words);
        }

        return words;
    }

    private static Regex? WholeTokenRegex(IEnumerable<string>? values)
    {
        var usable = (values ?? [])
            .Select(v => v?.Trim() ?? string.Empty)
            .Where(v => v.Length >= 2 && !v.All(char.IsDigit) && v.Any(char.IsLetterOrDigit) && !LogVocabulary.Contains(v))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(v => v.Length)
            .Select(Regex.Escape)
            .ToList();
        return usable.Count == 0
            ? null
            : new Regex(@"(?<![\p{L}\p{N}])(?:" + string.Join('|', usable) + @")(?![\p{L}\p{N}])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
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

        if (_userText is not null)
        {
            line = _userText.Replace(line, RedactedValue);
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
    /// <param name="json">The file text.</param>
    /// <param name="spec">The keys it may carry.</param>
    /// <param name="jobId">When given, every <c>jobId</c> leaf is rewritten to it: the file's own run folder decides the id, not the file.</param>
    /// <param name="rewrite">Applied to every kept string leaf before it is checked (never to a key).</param>
    /// <param name="rewriteNumber">Given a kept number leaf's text; a non-null result (the whole text is a withheld id) replaces the number with that string.</param>
    public string? RedactJson(string json, KeySpec spec, string? jobId = null, Func<string, string>? rewrite = null, Func<string, string?>? rewriteNumber = null)
    {
        try
        {
            return Sanitize(JsonNode.Parse(json), spec, jobId, rewrite, rewriteNumber)?.ToJsonString(JsonOptions);
        }
        catch (Exception ex) when (IsUnreadableJson(ex))
        {
            return null;
        }
    }

    /// <summary>One JSON object per line; a line that does not parse is replaced by a note, never copied.</summary>
    public string RedactJsonLines(string text, KeySpec spec, Func<string, string>? rewrite = null, Func<string, string?>? rewriteNumber = null)
    {
        var lines = text.Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .Select(line =>
            {
                try
                {
                    return Sanitize(JsonNode.Parse(line), spec, rewrite: rewrite, rewriteNumber: rewriteNumber)?.ToJsonString(CompactOptions) ?? "null";
                }
                catch (Exception ex) when (IsUnreadableJson(ex))
                {
                    return "\"<unparseable line omitted>\"";
                }
            });
        return string.Join('\n', lines) + "\n";
    }

    private JsonNode? Sanitize(JsonNode? node, KeySpec? spec, string? jobId = null, Func<string, string>? rewrite = null, Func<string, string?>? rewriteNumber = null)
    {
        switch (node)
        {
            case JsonObject obj when spec is not null && spec.IsAllowedValue is null:
                var kept = new JsonObject();
                foreach (var (key, value) in obj)
                {
                    if (spec.Keys.TryGetValue(key, out var child) && (child is not null || value is not (JsonObject or JsonArray)))
                    {
                        kept[key] = jobId is not null && key == "jobId" && value is JsonValue
                            ? JsonValue.Create(jobId)
                            : Sanitize(value, child, jobId, rewrite, rewriteNumber);
                    }
                }

                return kept;
            case JsonArray array when spec is not null && spec.IsAllowedValue is null:
                var items = new JsonArray();
                foreach (var item in array)
                {
                    items.Add(Sanitize(item, spec, jobId, rewrite, rewriteNumber));
                }

                return items;
            case JsonValue value when value.TryGetValue<string>(out var text):
                if (spec?.IsAllowedValue is { } isAllowed && !isAllowed(text))
                {
                    return RedactedValue;
                }

                text = rewrite is null ? text : rewrite(text);
                return SafeValue().IsMatch(text) && !SequenceShape.IsSequenceLine(text, minLength: 20, out _) ? RedactText(text) : RedactedValue;
            case JsonValue when spec?.IsAllowedValue is not null:
                // An enum-valued leaf (error.code) holding a number, bool or null is not a registry code either (#613).
                return RedactedValue;
            case JsonValue number when rewriteNumber is not null:
                // A number is not copied as it is: a withheld all-digit id (`"id": 12`) is a number leaf too. Only a leaf the
                // rewrite changes becomes a string; any other number keeps its type and value.
                // Whole-text match only: a token match would split 0.1 at the dot.
                var rewritten = rewriteNumber(number.ToJsonString());
                return rewritten is null ? node.DeepClone() : JsonValue.Create(rewritten);
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
                    else if (child?.IsAllowedValue is { } isAllowed)
                    {
                        // An enum-valued leaf (error.code) holding anything else is not shipped, so it is scrubbed from the logs too,
                        // but only when it looks like data (a digit or punctuation): a plain word such as "job" would blank that word in every log line.
                        if (value is JsonValue leaf && leaf.TryGetValue<string>(out var rejected) && !isAllowed(rejected) && rejected.Length is >= 3 and <= 260 && LooksLikeData(rejected))
                        {
                            sink.Add(rejected);
                        }
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
    public static string? FindLeak(IReadOnlyDictionary<string, string> entries, IEnumerable<string>? sensitiveValues = null) =>
        Find(entries, sensitiveValues)?.EntryName;

    /// <summary>
    /// Like <see cref="FindLeak"/>, but also names the check that fired (a fixed word, never user text), so a refusal
    /// can be diagnosed without the data that caused it.
    /// </summary>
    public static LeakFinding? Find(IReadOnlyDictionary<string, string> entries, IEnumerable<string>? sensitiveValues = null)
    {
        var sensitive = SensitiveValue.Normalize(sensitiveValues ?? []);
        foreach (var (name, text) in entries)
        {
            var rule = Leaky(name, sensitive) ?? Leaky(text, sensitive);
            if (rule is not null)
            {
                return new LeakFinding(name, rule);
            }
        }

        return null;
    }

    private static string? Leaky(string text, IReadOnlyList<SensitiveValue> sensitive)
    {
        if (CredentialShape().IsMatch(text) || CredentialPair().IsMatch(text))
        {
            return LeakRules.Credential;
        }

        if (DriveOrUncPath().IsMatch(text))
        {
            return LeakRules.Path;
        }

        if (EmailShape().Matches(text).Any(match => !AllowedEmails.Contains(match.Value)))
        {
            return LeakRules.Email;
        }

        var plain = Placeholder().Replace(text, " ");
        if (sensitive.Any(value => value.IsIn(plain)))
        {
            return LeakRules.Identifier;
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
                    return LeakRules.Sequence;
                }
            }
            else
            {
                run = 0;
                letters = 0;
            }
        }

        return null;
    }
}

/// <summary>The check that refused a bundle: fixed words only, safe to log.</summary>
public static class LeakRules
{
    public const string Credential = "credential";
    public const string Path = "path";
    public const string Email = "email";
    public const string Identifier = "identifier";
    public const string Sequence = "sequence";
}

/// <summary>Which bundle entry tripped the final scan, and which check fired.</summary>
public sealed record LeakFinding(string EntryName, string Rule);
