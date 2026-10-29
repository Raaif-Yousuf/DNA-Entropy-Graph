using System.Text;
using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Guards.Tests;

/// <summary>
/// Issue #141: "the app never logs a note or a tag" is the premise of the support bundle not scrubbing a tag that is a log word. A real logging
/// sink whose statement names a note or tag identifier breaks that premise. A sink is: Trace.Trace*/Trace.Write*, Debug.Write*/Debug.Print,
/// Console.Write*/Console.Error.Write*, or a Warning/Error/Information/Debug/Verbose/Fatal/Log*/Write* call on a receiver that is a logger: named
/// log/logger (any case, optional leading underscore, so Serilog's static <c>Log</c> too) or declared in the same file as IDiagnosticsLog, ILogger,
/// CloudRetryLog or FileDiagnosticsLog. Other receivers (File.WriteAllText, a Utf8JsonWriter, Debug.Assert) are not logging. The identifier match is
/// case-insensitive and word-wise (notes, runTags, _tagList, TagsJson, annotation); string literal text is prose and ignored, but an interpolation hole
/// or a Serilog placeholder name counts, and a name declared inside the statement itself (a lambda's <c>var notes</c>) is not user text. The statement
/// ends at the first semicolon outside a string, char, raw string or comment. NOT seen: a logger reached through an unnamed receiver (a property chain,
/// a method result) or passed to a helper that logs.
/// </summary>
public sealed partial class NotesTagsNeverLoggedGuardTests
{
    // Fixed sinks that need no receiver check.
    [GeneratedRegex(@"\b(?:Trace\s*\.\s*(?:Trace|Write)\w*|Debug\s*\.\s*(?:Write\w*|Print)|Console\s*\.\s*(?:Error\s*\.\s*)?Write\w*)\s*\(")]
    private static partial Regex FixedSink();

    // A logging-named method on some receiver; the receiver is checked afterwards.
    [GeneratedRegex(@"(?<recv>[A-Za-z_]\w*)\s*\??\s*\.\s*(?:Warning|Error|Information|Debug|Verbose|Fatal|Log\w*|Write\w*)\s*(?:<[^>()]*>\s*)?\(")]
    private static partial Regex LoggerMethod();

    [GeneratedRegex(@"^_?(?:log|logger)$", RegexOptions.IgnoreCase)]
    private static partial Regex LoggerName();

    [GeneratedRegex(@"\b(?:IDiagnosticsLog|NullDiagnosticsLog|FileDiagnosticsLog|CloudRetryLog|ILogger(?:<[^>]*>)?)\??\s+(_?\w+)")]
    private static partial Regex LoggerDeclaration();

    [GeneratedRegex(@"\b(?:var|string|int|long|bool|double|object)\s+(\w+)\s*(?:=(?!=)|;|,|\))")]
    private static partial Regex LocalDeclaration();

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
        // A name the statement declares itself (a block lambda's `var notes = ...`) is a local of that lambda, not the user's text.
        var declared = LocalDeclaration().Matches(code).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        foreach (Match token in Identifier().Matches(code))
        {
            if (declared.Contains(token.Value))
            {
                continue;
            }

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
        var loggers = LoggerDeclaration().Matches(text).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        var starts = FixedSink().Matches(text).Select(m => m.Index).ToList();
        foreach (Match call in LoggerMethod().Matches(text))
        {
            var receiver = call.Groups["recv"].Value;
            if (LoggerName().IsMatch(receiver) || loggers.Contains(receiver))
            {
                starts.Add(call.Index);
            }
        }

        var found = new List<(string, bool)>();
        foreach (var start in starts.Distinct().Order())
        {
            var (statement, code) = ReadStatement(text, start);
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
    [InlineData("_log.Warning(\"x\", null, notes);")]
    [InlineData("_log?.Warning(\"x\", null, notes);")]
    [InlineData("log.Verbose(\"x\", tags);")]
    [InlineData("logger.Error(ex, \"x {A}\", tagList);")]
    [InlineData("Log.Fatal(notesText);")]
    [InlineData("Console.WriteLine(annotations);")]
    [InlineData("Console.Error.WriteLine(annotations);")]
    [InlineData("Trace.WriteLine(note);")]
    [InlineData("Debug.Print(runTags);")]
    [InlineData("IDiagnosticsLog diag = null; diag.Warning(\"x\", null, tagsJson);")]
    [InlineData("private readonly CloudRetryLog retries; void M() { retries.LogCustom(tagsJson); }")]
    [InlineData("Log.Information(\"a;b\", notes);")]
    [InlineData("Log.Information(\"a;b\", '\\'', \";\", tags);")]
    [InlineData("Log.Information(@\"a;\"\"b\", notes);")]
    [InlineData("Log.Information(\"\"\"a;b\"\"\", notes);")]
    [InlineData("Log.Information($\"saved {notes}\");")]
    [InlineData("Log.Information($\"saved {string.Join(\",\", tags)}\");")]
    [InlineData("_log.Warning(\"x\", items.Select(i => { var other = 1; return other; }), notes);")]
    public void The_scanner_catches_a_planted_violation(string source)
    {
        Scan("void M() { " + source + " }").ShouldContain(r => r.NamesUserText, $"not caught: {source}");
    }

    [Theory]
    [InlineData("Log.Information(\"opened {Id}\", id);")]
    [InlineData("Log.Information(\"saved the notes and the tags\");")]
    [InlineData("_logger.LogInformation(\"denotes {Count}\", count);")]
    [InlineData("Console.WriteLine(footnote);")]
    [InlineData("Log.Debug(\"x\", stage); var notes = 1;")]
    [InlineData("Log.Information(\"a;b\", id); var tags = 2;")]
    [InlineData("Log.Information(\"x\"); /* ; */")]
    // Not logging sinks: narrow by design, so ordinary code never trips the guard.
    [InlineData("Debug.Assert(notes != null);")]
    [InlineData("File.WriteAllText(path, tagsJson);")]
    [InlineData("utf8Writer.WriteString(\"tags\", tags);")]
    [InlineData("sink.Write(notesText);")]
    [InlineData("results.Error(annotations);")]
    [InlineData("Volatile.Write(ref _annotationFlag, 0);")]
    // A block-bodied lambda argument that declares its own `notes` is not the user's text.
    [InlineData("_log.Warning(\"x\", items.Select(i => { var notes = i.ToString(); return notes.Length; }), id);")]
    public void The_scanner_ignores_a_log_call_that_names_neither_and_calls_that_are_not_logging(string source)
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
