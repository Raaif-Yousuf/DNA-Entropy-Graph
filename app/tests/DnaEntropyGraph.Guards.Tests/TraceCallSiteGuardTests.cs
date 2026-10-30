using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Guards.Tests;

/// <summary>
/// Issue #923: Trace output now lands in logs\app.log and the diagnostics zip, so every <c>Trace.TraceWarning/TraceError/TraceInformation</c>
/// call in app/src may interpolate only an exception type name (<c>x.GetType().Name</c>, <c>x.GetBaseException().GetType().Name</c>) or one of
/// the listed members, never a path, file name, message or any other variable (Hard Rule: never log the sequence, the file name, the email).
/// The listener redacts again as defence in depth; this guard keeps the call sites themselves clean.
/// </summary>
public class TraceCallSiteGuardTests
{
    // The leading boundary keeps FooTrace.Write and My.Trace.Write out; an optional System.Diagnostics. prefix is still a match.
    private static readonly Regex Call = new(
        @"(?<![\w.])(?:(?:global::)?System\.)?(?:Diagnostics\.)?(?:Trace\.(?<m>TraceWarning|TraceError|TraceInformation)|(?:Trace|Debug)\.(?<m>WriteLineIf|WriteIf|WriteLine|Write|Fail|Assert))\(",
        RegexOptions.Compiled);

    private static readonly Regex TypeNameHole = new(
        @"^\w+(?:\.GetBaseException\(\))?\.GetType\(\)\.Name$", RegexOptions.Compiled);

    /// <summary>Holes that are not a type name, each with why it is safe. Add here AND say why, or fix the call site.</summary>
    private static readonly Dictionary<string, string> Whitelist = new()
    {
        ["refused.Reason"] = "FileAssociationRefusal enum member (FileAssociationSettingsViewModel)",
        ["ex.Rule"] = "the name of a diagnostics redaction rule, a constant (SettingsViewModel)",
        ["ex.Kind"] = "the CloudErrorKind enum member of a CloudOperationException, never Google text (BudgetSettingsViewModel)",
        ["what"] = "CloudResourcesViewModel.LogFailure's parameter; every caller passes a literal phrase",
    };

    /// <summary>The unsafe holes in one call's argument text; empty when the call is clean. A non-interpolated argument must be one plain literal.</summary>
    internal static List<string> Violations(string argument)
    {
        var bad = new List<string>();
        var arg = argument.Trim();
        var interpolated = arg.StartsWith("$\"", StringComparison.Ordinal);
        if (!interpolated && !arg.StartsWith('"'))
        {
            // A variable, a call, a verbatim or raw string: not a constant message.
            bad.Add(arg);
            return bad;
        }

        // Walk to the closing quote of the one leading literal; anything after it (a + concatenation, a format argument list) is unsafe.
        var i = interpolated ? 2 : 1;
        for (; i < arg.Length && arg[i] != '"'; i++)
        {
            if (arg[i] == '\\')
            {
                i++;
            }
        }

        var literal = i < arg.Length ? arg[..(i + 1)] : arg;
        var rest = i < arg.Length ? arg[(i + 1)..].Trim() : "unterminated literal";
        if (rest.Length > 0)
        {
            bad.Add("after the literal: " + rest);
        }

        if (interpolated)
        {
            foreach (Match hole in Regex.Matches(literal, @"\{([^{}]*)\}"))
            {
                var expr = hole.Groups[1].Value.Trim();
                if (!TypeNameHole.IsMatch(expr) && !Whitelist.ContainsKey(expr))
                {
                    bad.Add(expr);
                }
            }
        }

        return bad;
    }

    /// <summary>The top-level arguments of one call's argument text (commas inside parentheses, brackets, braces and string literals do not split).</summary>
    private static List<string> SplitArguments(string text)
    {
        var parts = new List<string>();
        var depth = 0;
        var inString = false;
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                if (c == '\\')
                {
                    i++;
                }
                else if (c == '"')
                {
                    inString = false;
                }

                continue;
            }

            switch (c)
            {
                case '"':
                    inString = true;
                    break;
                case '(' or '[' or '{':
                    depth++;
                    break;
                case ')' or ']' or '}':
                    depth--;
                    break;
                case ',' when depth == 0:
                    parts.Add(text[start..i]);
                    start = i + 1;
                    break;
            }
        }

        parts.Add(text[start..]);
        return parts;
    }

    /// <summary>
    /// Every message argument that reaches the log. A plain call is checked as a whole (so a trailing category or format argument is a violation);
    /// for WriteIf, WriteLineIf and Assert the first argument is a condition and is not checked, the message (and an Assert's detail message) are.
    /// </summary>
    internal static IEnumerable<string> Arguments(string source)
    {
        foreach (Match m in Call.Matches(source))
        {
            var depth = 1;
            var i = m.Index + m.Length;
            var start = i;
            var inString = false;
            for (; i < source.Length && depth > 0; i++)
            {
                var c = source[i];
                if (inString)
                {
                    if (c == '\\')
                    {
                        i++;
                    }
                    else if (c == '"')
                    {
                        inString = false;
                    }

                    continue;
                }

                if (c == '"')
                {
                    inString = true;
                }
                else if (c == '(')
                {
                    depth++;
                }
                else if (c == ')')
                {
                    depth--;
                }
            }

            var argument = source[start..(i - 1)];
            if (m.Groups["m"].Value is "WriteIf" or "WriteLineIf" or "Assert")
            {
                // Condition first; everything after it is message text.
                var parts = SplitArguments(argument);
                yield return parts.Count > 1 ? parts[1] : string.Empty;
                if (parts.Count > 2 && m.Groups["m"].Value == "Assert")
                {
                    yield return parts[2];
                }
            }
            else
            {
                yield return argument;
            }
        }
    }
    [Fact]
    public void Every_Trace_call_in_app_src_interpolates_only_type_names_or_whitelisted_members()
    {
        var calls = 0;
        var offenders = new List<string>();
        foreach (var file in RepoPaths.AllCSharpFiles)
        {
            foreach (var argument in Arguments(File.ReadAllText(file)))
            {
                calls++;
                foreach (var hole in Violations(argument))
                {
                    offenders.Add($"{Path.GetRelativePath(RepoPaths.AppRoot, file)}: {hole}");
                }
            }
        }

        calls.ShouldBeGreaterThanOrEqualTo(50, "vacuity: the scan must see the app's real Trace calls (59 at 2026-10-29)");
        offenders.ShouldBeEmpty("a Trace line reaches logs\\app.log and the diagnostics zip; pass an exception type name, not data");
    }

    [Fact]
    public void Program_Main_registers_the_trace_listener_on_the_resolved_data_folder_before_anything_else_can_trace()
    {
        var main = File.ReadAllText(Path.Combine(RepoPaths.AppRoot, "src", "DnaEntropyGraph.App", "Program.cs"));

        var resolved = main.IndexOf("StartupDataRoot.TryResolve(", StringComparison.Ordinal);
        var installed = main.IndexOf("DiagnosticsTraceListener.Install(StartupDataRoot.Current.Path)", StringComparison.Ordinal);
        var firstHandOff = main.IndexOf("SingleInstance.HandedToRunningInstance(", StringComparison.Ordinal);

        resolved.ShouldBeGreaterThan(-1);
        installed.ShouldBeGreaterThan(resolved, "the listener is installed once the data folder is known");
        installed.ShouldBeLessThan(firstHandOff, "before the single-instance hand-off and the first Trace call after it");
    }

    [Fact]
    public void Every_whitelist_entry_is_still_used_by_a_real_call()
    {
        var all = string.Join('\n', RepoPaths.AllCSharpFiles.SelectMany(f => Arguments(File.ReadAllText(f))));

        foreach (var entry in Whitelist.Keys)
        {
            all.ShouldContain("{" + entry + "}", customMessage: "a stale whitelist entry is a hole nobody reviews");
        }
    }

    [Theory]
    [InlineData("MESSAGE")]
    [InlineData("PATH")]
    [InlineData("VARIABLE")]
    [InlineData("CONCAT")]
    [InlineData("INTERP_CONCAT")]
    [InlineData("INTERP_ARGS")]
    [InlineData("PLAIN_ARGS")]
    [InlineData("VERBATIM")]
    [InlineData("WRITE")]
    [InlineData("WRITELINE")]
    [InlineData("FAIL")]
    [InlineData("DEBUG_WRITE")]
    [InlineData("DEBUG_WRITELINE")]
    [InlineData("DEBUG_FAIL")]
    [InlineData("WRITELINEIF")]
    [InlineData("ASSERT")]
    [InlineData("DEBUG_WRITEIF")]
    public void The_scanner_catches_a_planted_unsafe_call(string kind)
    {
        // Built at run time so this file never contains a bad call itself.
        var call = kind switch
        {
            "WRITE" => "Trace." + "Write(",
            "WRITELINE" => "Trace." + "WriteLine(",
            "FAIL" => "Trace." + "Fail(",
            "DEBUG_WRITE" => "Debug." + "Write(",
            "DEBUG_WRITELINE" => "Debug." + "WriteLine(",
            "DEBUG_FAIL" => "Debug." + "Fail(",
            "WRITELINEIF" => "Trace." + "WriteLineIf(",
            "ASSERT" => "Debug." + "Assert(",
            "DEBUG_WRITEIF" => "System.Diagnostics.Debug." + "WriteIf(",
            _ => "System.Diagnostics.Trace." + "TraceWarning(",
        };
        var planted = kind switch
        {
            "INTERP_CONCAT" => "$\"x {ex.GetType().Name}\" + " + "path",
            "INTERP_ARGS" => "$\"x {ex.GetType().Name}\", " + "path",
            "PLAIN_ARGS" => "\"x {0}\", " + "path",
            "VERBATIM" => "@\"C:\\" + "x\"",
            "WRITELINEIF" or "DEBUG_WRITEIF" => "enabled, " + "fileName",
            "ASSERT" => "ok, " + "path",
            "WRITE" or "WRITELINE" or "FAIL" or "DEBUG_WRITE" or "DEBUG_WRITELINE" or "DEBUG_FAIL" => "fileName",
            "MESSAGE" => "$\"failed: {" + "ex.Message}\"",
            "PATH" => "$\"could not open {" + "inputPath}\"",
            "VARIABLE" => "$\"seq {" + "sequence}\"",
            _ => "\"a \" + " + "fileName",
        };
        var source = call + planted + ");";

        Arguments(source).SelectMany(Violations).ShouldNotBeEmpty();
    }

    [Fact]
    public void The_scanner_ignores_a_longer_name_and_checks_the_message_not_the_condition()
    {
        Arguments("FooTrace." + "Write(fileName);").ShouldBeEmpty();
        Arguments("My.Trace." + "WriteLine(fileName);").ShouldBeEmpty();

        var clean = Arguments("Trace." + "WriteLineIf(someFlag && other, \"constant\");" + "Debug." + "Assert(items.Count > 0, \"constant\", \"detail\");").ToList();

        clean.Count.ShouldBe(3, "one message per call, plus the Assert detail");
        clean.SelectMany(Violations).ShouldBeEmpty();
    }

    [Fact]
    public void The_scanner_accepts_the_clean_shapes()
    {
        var source = "Trace." + "TraceWarning($\"x failed: {ex.GetType().Name} {ex.GetBaseException().GetType().Name} {refused.Reason}\");"
            + "Trace." + "TraceError(\"constant text (with parens)\");";

        var args = Arguments(source).ToList();

        args.Count.ShouldBe(2);
        args.SelectMany(Violations).ShouldBeEmpty();
    }
}
