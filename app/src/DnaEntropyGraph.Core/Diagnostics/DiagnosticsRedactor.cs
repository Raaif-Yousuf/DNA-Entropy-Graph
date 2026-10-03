using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace DnaEntropyGraph.Core.Diagnostics;

/// <summary>
/// Hard Rule 14's counterpart for support files: nothing that identifies the user's data leaves the machine.
/// JSON is cut down by allowlist (a string, object or array is kept only under a key that is not data-describing; a
/// string value is kept only when it is short and made of identifier characters; numbers and booleans are always kept),
/// then every kept string and every log line also passes the text redaction below. <see cref="DiagnosticsLeakScan"/>
/// is the last line of defence, not the first.
/// </summary>
internal sealed partial class DiagnosticsRedactor
{
    public const string RedactedValue = "<redacted>";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    // Keys whose text values describe the user's data or identity: dropped whatever the value looks like.
    [GeneratedRegex("(?i)(name|file|path|sequence|seq|input|email|account|token|secret|dir|folder|url|uri|message|detail|note|tag|label|title|description)")]
    private static partial Regex DroppedKey();

    [GeneratedRegex(@"^[A-Za-z0-9_.:+\-]{0,80}$")]
    private static partial Regex SafeValue();

    [GeneratedRegex(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}")]
    private static partial Regex Email();

    [GeneratedRegex(@"[^\s""'<>|\\/]+\.(?:fa|fasta|fna|ffn|faa|fas|gb|gbk|gbff|genbank|embl|dna|seq|txt)\b", RegexOptions.IgnoreCase)]
    private static partial Regex SequenceFileName();

    [GeneratedRegex("[ACGTNacgtn]{20,}")]
    private static partial Regex DnaRun();

    private readonly string _profilePath;
    private readonly IReadOnlyList<string> _sensitive;

    public DiagnosticsRedactor(string userProfilePath, IEnumerable<string> sensitiveValues)
    {
        _profilePath = userProfilePath.TrimEnd('\\', '/');
        // Longest first, so "plasmid_pUC19.fasta" is replaced whole before its stem "plasmid_pUC19" is.
        _sensitive = sensitiveValues
            .Select(value => value.Trim())
            .Where(value => value.Length >= 3)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(value => value.Length)
            .ToList();
    }

    public string RedactText(string text)
    {
        // The profile path first: once "jdoe" is a sensitive literal, "C:\Users\jdoe" would no longer be found.
        if (_profilePath.Length > 0)
        {
            text = text.Replace(_profilePath, "<user>", StringComparison.OrdinalIgnoreCase);
            text = text.Replace(_profilePath.Replace('\\', '/'), "<user>", StringComparison.OrdinalIgnoreCase);
        }

        foreach (var value in _sensitive)
        {
            text = text.Replace(value, RedactedValue, StringComparison.OrdinalIgnoreCase);
        }

        text = Email().Replace(text, "<email>");

        text = SequenceFileName().Replace(text, "<file>");
        return DnaRun().Replace(text, "<sequence>");
    }

    /// <summary>A parsed JSON document with every unsafe key and value removed; null when the text is not JSON.</summary>
    public string? RedactJson(string json)
    {
        try
        {
            return Sanitize(JsonNode.Parse(json))?.ToJsonString(JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>One JSON object per line; a line that does not parse is replaced by a note, never copied.</summary>
    public string RedactJsonLines(string text)
    {
        var lines = text.Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .Select(line =>
            {
                try
                {
                    return Sanitize(JsonNode.Parse(line))?.ToJsonString() ?? "null";
                }
                catch (JsonException)
                {
                    return "\"<unparseable line omitted>\"";
                }
            });
        return string.Join('\n', lines) + "\n";
    }

    public JsonNode? Sanitize(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                var kept = new JsonObject();
                foreach (var (key, value) in obj)
                {
                    var isPlainNumberOrBool = value is JsonValue v && !v.TryGetValue<string>(out _);
                    if (isPlainNumberOrBool || !DroppedKey().IsMatch(key))
                    {
                        kept[key] = Sanitize(value);
                    }
                }

                return kept;
            case JsonArray array:
                var items = new JsonArray();
                foreach (var item in array)
                {
                    items.Add(Sanitize(item));
                }

                return items;
            case JsonValue value when value.TryGetValue<string>(out var text):
                return SafeValue().IsMatch(text) ? RedactText(text) : RedactedValue;
            default:
                return node?.DeepClone();
        }
    }
}

/// <summary>The last check before a zip is written: no run of 20 or more A, C, G, T or N may survive anywhere.</summary>
public static partial class DiagnosticsLeakScan
{
    [GeneratedRegex("[ACGTNacgtn]{20,}")]
    private static partial Regex DnaLike();

    /// <summary>The name of the first entry holding sequence-like text, or null when the set is clean.</summary>
    public static string? FindLeak(IReadOnlyDictionary<string, string> entries)
    {
        foreach (var (name, text) in entries)
        {
            if (DnaLike().IsMatch(text))
            {
                return name;
            }
        }

        return null;
    }
}
