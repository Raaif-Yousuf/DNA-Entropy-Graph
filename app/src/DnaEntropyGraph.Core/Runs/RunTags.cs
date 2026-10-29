using System.Text.Json;

namespace DnaEntropyGraph.Core.Runs;

/// <summary>
/// The tags of a run (issue #141). The user types them as one line ("clone7, redo"); they are stored as a JSON array of
/// strings in the <c>Runs.TagsJson</c> column. Reading never throws: a value that is not an array of strings is no tags.
/// </summary>
public static class RunTags
{
    public const int MaxTagLength = 40;
    public const int MaxTags = 20;

    /// <summary>The longest note kept, in UTF-16 characters. The Results box stops at it and a save cuts to it, so an enormous paste can never reach the database, the Runs search or a bundle.</summary>
    public const int MaxNotesLength = 2000;

    private static readonly char[] Separators = [',', ';', '\n', '\r'];

    /// <summary>Splits typed text into tags: trimmed, no empties, no repeats (ignoring case), each cut to <see cref="MaxTagLength"/>, at most <see cref="MaxTags"/>.</summary>
    public static IReadOnlyList<string> ParseInput(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var tags = new List<string>();
        foreach (var part in text.Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var tag = CutTo(part, MaxTagLength);
            if (tag.Length > 0 && !tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
            {
                tags.Add(tag);
            }

            if (tags.Count == MaxTags)
            {
                break;
            }
        }

        return tags;
    }

    /// <summary>The note cut to <see cref="MaxNotesLength"/> (never in the middle of a surrogate pair); null stays null.</summary>
    public static string? ClampNotes(string? notes) => notes is null ? null : CutTo(notes, MaxNotesLength);

    // A cut that lands between the two halves of a surrogate pair (an emoji) drops the lone half, so the text always serializes.
    private static string CutTo(string text, int maxLength)
    {
        if (text.Length <= maxLength)
        {
            return text;
        }

        var end = char.IsHighSurrogate(text[maxLength - 1]) ? maxLength - 1 : maxLength;
        return text[..end].TrimEnd();
    }

    /// <summary>The tags stored in a <c>TagsJson</c> value; none for null, empty or anything that is not an array of strings.</summary>
    public static IReadOnlyList<string> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<string[]>(json)?.Where(t => !string.IsNullOrWhiteSpace(t)).ToList() ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>The value to store: a JSON array, or null when there are no tags.</summary>
    public static string? Serialize(IReadOnlyList<string> tags) => tags.Count == 0 ? null : JsonSerializer.Serialize(tags);

    /// <summary>The one-line form the user edits: "a, b".</summary>
    public static string Format(IReadOnlyList<string> tags) => string.Join(", ", tags);
}
