using System.Text;
using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Guards.Tests;

/// <summary>
/// Issue #141: "the app never logs a note or a tag" is the premise of the support bundle not scrubbing a tag that is a log word. A logging
/// call (Serilog, ILogger, Trace, Debug, Console, or any receiver's Warning/Error/Information/Debug/Verbose/Fatal/Log*/Write* method) whose
/// statement names a note or tag identifier breaks that premise. The identifier match is case-insensitive and word-wise (notes, runTags,
/// _tagList, TagsJson, annotation); string literal text is prose and ignored, but an interpolation hole or a Serilog placeholder name counts.
/// The statement ends at the first semicolon outside a string, char, raw string or comment, so a semicolon inside a message does not hide a later argument.
/// </summary>
public sealed partial class NotesTagsNeverLoggedGuardTests
{
    [GeneratedRegex(@"(?:\b(?:Trace|Debug|Console)\s*\.\s*\w+|\.\s*(?:Warning|Error|Information|Debug|Verbose|Fatal|Log\w*|Write\w*))\s*(?:<[^>()]*>\s*)?\(")]
    private static partial Regex LogCall();

    [GeneratedRegex(@"[A-Za-z_][A-Za-z0-9_]*")]
    private static partial Regex Identifier();

    [GeneratedRegex(@"[A-Z]+(?![a-z])|[A-Z]?[a-z]+|[0-9]+")]
    private static partial Regex WordPart();

    private static readonly HashSet<string> UserTextWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "note", "notes", "tag", "tags", "annotation", "annotations",
    };

    private static readonly HashSet<string> UserTextWholeTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "tagsjson", "notestext", "tagstext", "taglist", "notelist", "tagjson", "notetext", "tagtext",
    };

    /// <summary>Whether <paramref name="code"/> (string text already blanked) names a user note/tag/annotation identifier, any case.</summary>
    internal static bool NamesUserText(string code)
    {
        foreach (Match token in Identifier().Matches(code))
        {
            if (UserTextWholeTokens.Contains(token.Value.Trim('_')) || UserTextWords.Contains(token.Value.Trim('_')))
            {
                return true;
            }

            if (WordPart().Matches(token.Value).Any(w => UserTextWords.Contains(w.Value)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Ends the statement that starts at <paramref name="start"/> at the first top-level semicolon, skipping string, interpolated, verbatim, raw
    /// and char literals and comments. Returns the statement and a copy with literal text blanked (interpolation holes and Serilog
    /// <c>{Placeholder}</c> names kept).
    /// </summary>
    internal static (string Raw, string Code) ReadStatement(string text, int start)
    {
        var raw = new StringBuilder();
        var code = new StringBuilder();
        var i = start;
        var depth = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (c == ';' && depth <= 0)
            {
                break;
            }

            if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                var eol = text.IndexOf('\n', i);
                i = eol < 0 ? text.Length : eol;
                continue;
            }

            if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                var close = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = close < 0 ? text.Length : close + 2;
                continue;
            }

            if (c == '\'')
            {
                var j = i + 1;
                while (j < text.Length && text[j] != '\'' && text[j] != '\n')
                {
                    j += text[j] == '\\' ? 2 : 1;
                }

                var end = Math.Min(j + 1, text.Length);
                raw.Append(text, i, end - i);
                code.Append("''");
                i = end;
                continue;
            }

            if (c == '"' || ((c == '$' || c == '@') && IsStringStart(text, i)))
            {
                i = ReadString(text, i, raw, code);
                continue;
            }

            if (c is '(' or '[' or '{')
            {
                depth++;
            }
            else if (c is ')' or ']' or '}')
            {
                depth--;
                if (depth < 0)
                {
                    break;
                }
            }

            raw.Append(c);
            code.Append(c);
            i++;
        }

        return (raw.ToString(), code.ToString());
    }

    private static bool IsStringStart(string text, int i)
    {
        var j = i;
        while (j < text.Length && (text[j] == '$' || text[j] == '@'))
        {
            j++;
        }

        return j < text.Length && text[j] == '"';
    }

    /// <summary>Consumes one string literal starting at <paramref name="i"/>; appends it verbatim to raw and, to code, only its interpolation holes / placeholder names.</summary>
    private static int ReadString(string text, int i, StringBuilder raw, StringBuilder code)
    {
        var interpolated = false;
        var verbatim = false;
        var j = i;
        while (text[j] != '"')
        {
            interpolated |= text[j] == '$';
            verbatim |= text[j] == '@';
            j++;
        }

        var quotes = 0;
        while (j + quotes < text.Length && text[j + quotes] == '"')
        {
            quotes++;
        }

        var rawLiteral = quotes >= 3;
        var bodyStart = j + (rawLiteral ? quotes : 1);
        var k = bodyStart;
        var holes = new StringBuilder();
        while (k < text.Length)
        {
            var c = text[k];
            if (rawLiteral)
            {
                if (c == '"' && string.CompareOrdinal(text, k, new string('"', quotes), 0, quotes) == 0)
                {
                    k += quotes;
                    break;
                }
            }
            else if (verbatim)
            {
                if (c == '"')
                {
                    if (k + 1 < text.Length && text[k + 1] == '"')
                    {
                        k += 2;
                        continue;
                    }

                    k++;
                    break;
                }
            }
            else
            {
                if (c == '\\')
                {
                    k += 2;
                    continue;
                }

                if (c == '"')
                {
                    k++;
                    break;
                }
            }

            if (c == '{')
            {
                if (k + 1 < text.Length && text[k + 1] == '{')
                {
                    k += 2;
                    continue;
                }

                var close = text.IndexOf('}', k);
                if (close < 0)
                {
                    close = text.Length - 1;
                }

                // Interpolation hole or Serilog placeholder: its name is code, not prose.
                holes.Append(' ').Append(text, k + 1, close - k - 1);
                k = close + 1;
                continue;
            }

            k++;
        }

        k = Math.Min(k, text.Length);
        raw.Append(text, i, k - i);
        code.Append(interpolated || holes.Length > 0 ? "\"" + holes + "\"" : "\"\"");
        return k;
    }

    /// <summary>Every logging statement in <paramref name="text"/> (from the call to its closing semicolon), with whether it names a note or tag identifier.</summary>
    internal static IReadOnlyList<(string Statement, bool NamesUserText)> Scan(string text)
    {
        var found = new List<(string, bool)>();
        foreach (Match call in LogCall().Matches(text))
        {
            var (statement, code) = ReadStatement(text, call.Index);
            found.Add((statement, NamesUserText(code)));
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
    // New shapes (cold review round 4).
    [InlineData("_log.Warning(\"x\", null, notes);")]
    [InlineData("_diag.Verbose(\"x\", tags);")]
    [InlineData("logger.Error(ex, \"x {A}\", tagList);")]
    [InlineData("sink.Write(notesText);")]
    [InlineData("Console.WriteLine(annotations);")]
    [InlineData("Trace.WriteLine(note);")]
    [InlineData("Debug.Print(runTags);")]
    [InlineData("_trace.Fatal(annotation);")]
    [InlineData("writer.LogCustom(tagsJson);")]
    [InlineData("Log.Information(\"a;b\", notes);")]
    [InlineData("Log.Information(\"a;b\", '\\'', \";\", tags);")]
    [InlineData("Log.Information(@\"a;\"\"b\", notes);")]
    [InlineData("Log.Information(\"\"\"a;b\"\"\", notes);")]
    [InlineData("Log.Information($\"saved {notes}\");")]
    [InlineData("Log.Information($\"saved {string.Join(\",\", tags)}\");")]
    [InlineData("Log.Information(\"x\"); /* ; */")]
    public void The_scanner_catches_a_planted_violation(string source)
    {
        var rows = Scan("void M() { " + source + " }");

        if (source == "Log.Information(\"x\"); /* ; */")
        {
            rows.ShouldAllBe(r => !r.NamesUserText);
            return;
        }

        rows.ShouldContain(r => r.NamesUserText, $"not caught: {source}");
    }

    [Theory]
    [InlineData("Log.Information(\"opened {Id}\", id);")]
    [InlineData("Log.Information(\"saved the notes and the tags\");")]
    [InlineData("_logger.LogInformation(\"denotes {Count}\", count);")]
    [InlineData("Console.WriteLine(footnote);")]
    [InlineData("Log.Debug(\"x\", stage); var notes = 1;")]
    [InlineData("Log.Information(\"a;b\", id); var tags = 2;")]
    public void The_scanner_ignores_a_log_call_that_names_neither(string source)
    {
        Scan("void M() { " + source + " }").ShouldAllBe(r => !r.NamesUserText);
    }

    [Fact]
    public void A_semicolon_inside_a_string_does_not_end_the_statement()
    {
        var rows = Scan("Log.Information(\"a;b\", notes);");

        rows.Count.ShouldBe(1);
        rows[0].Statement.ShouldContain("notes");
    }
}
