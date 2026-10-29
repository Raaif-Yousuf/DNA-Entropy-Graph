using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Guards.Tests;

/// <summary>
/// Issue #141: "the app never logs a note or a tag" is the premise of the support bundle not scrubbing a tag that is a log word. A logging
/// call (Serilog, ILogger, Trace, Debug) whose statement names a note or tag field breaks that premise.
/// </summary>
public sealed partial class NotesTagsNeverLoggedGuardTests
{
    [GeneratedRegex(@"(?:(?<![A-Za-z0-9])_?[Ll]og(?:ger)?\w*\s*\.\s*(?:Verbose|Debug|Information|Warning|Error|Fatal|Log\w*)|\bTrace\s*\.\s*Trace\w+|\bDebug\s*\.\s*Write\w*)\s*\(")]
    private static partial Regex LogCall();

    [GeneratedRegex(@"\b(?:Notes|TagsJson|NotesText|TagsText|Tags)\b")]
    private static partial Regex UserTextField();

    /// <summary>Every logging statement in <paramref name="text"/> (from the call to its closing semicolon), with whether it names a note or tag field.</summary>
    internal static IReadOnlyList<(string Statement, bool NamesUserText)> Scan(string text)
    {
        var found = new List<(string, bool)>();
        foreach (Match call in LogCall().Matches(text))
        {
            var end = text.IndexOf(';', call.Index);
            var statement = text[call.Index..(end < 0 ? text.Length : end)];
            found.Add((statement, UserTextField().IsMatch(statement)));
        }

        return found;
    }

    [Fact]
    public void No_logging_call_in_the_app_names_a_note_or_a_tag()
    {
        var all = RepoPaths.AllCSharpFiles.SelectMany(file => Scan(File.ReadAllText(file)).Select(r => (File: Path.GetFileName(file), r.Statement, r.NamesUserText))).ToList();

        all.Count.ShouldBeGreaterThanOrEqualTo(5, "the scanner found almost no logging calls: its pattern no longer matches how the app logs");
        all.Where(r => r.NamesUserText).Select(r => $"{r.File}: {r.Statement}").ShouldBeEmpty();
    }

    [Theory]
    [InlineData("Log.Information(\"saved {Notes}\", run.Notes);")]
    [InlineData("_logger.LogWarning(\"tags {T}\", run.TagsJson);")]
    [InlineData("System.Diagnostics.Trace.TraceError($\"bad {NotesText}\");")]
    [InlineData("Debug.WriteLine(TagsText);")]
    public void The_scanner_catches_a_planted_violation(string source)
    {
        Scan("void M() { " + source + " }").ShouldContain(r => r.NamesUserText);
    }

    [Fact]
    public void The_scanner_ignores_a_log_call_that_names_neither()
    {
        Scan("Log.Information(\"opened {Id}\", id);").ShouldAllBe(r => !r.NamesUserText);
    }
}
