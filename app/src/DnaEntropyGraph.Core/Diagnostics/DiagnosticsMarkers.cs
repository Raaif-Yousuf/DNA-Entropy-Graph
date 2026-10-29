namespace DnaEntropyGraph.Core.Diagnostics;

/// <summary>
/// The fixed markers the redactor and the bundle builder write in place of removed text. One source: the redactor's backstop, the leak scan's
/// placeholder blanking and the log vocabulary all read it, so a marker added here is understood by all three (cold review of #141).
/// </summary>
internal static class DiagnosticsMarkers
{
    /// <summary>The word inside <c>&lt;word&gt;</c>, as a regex alternation (a <c>const</c> so the generated regexes can use it).</summary>
    public const string WordAlternation = "redacted|user|path|token|email|sequence|file";

    /// <summary>The regex matching any marker (the words in angle brackets, any <c>&lt;omitted: ...&gt;</c> note, or the unparseable-line note).</summary>
    public const string Pattern = "<(?:" + WordAlternation + ")>|<omitted: [a-z ]{3,40}>|<unparseable line omitted>";

    public const string OmittedFile = "<omitted: file withheld>";

    public const string OmittedNote = "<omitted: note withheld>";

    public const string OmittedUnreadable = "<omitted: file is not valid json>";

    public const string OmittedTooLarge = "<omitted: file too large>";

    public const string Unparseable = "<unparseable line omitted>";

    public static IReadOnlyList<string> Words { get; } = WordAlternation.Split('|');
}
