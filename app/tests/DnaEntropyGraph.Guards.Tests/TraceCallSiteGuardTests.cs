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
    private static readonly Regex Call = new(@"Trace\.Trace(?:Warning|Error|Information)\(", RegexOptions.Compiled);

    private static readonly Regex TypeNameHole = new(
        @"^\w+(?:\.GetBaseException\(\))?\.GetType\(\)\.Name$", RegexOptions.Compiled);

    /// <summary>Holes that are not a type name, each with why it is safe. Add here AND say why, or fix the call site.</summary>
    private static readonly Dictionary<string, string> Whitelist = new()
    {
        ["refused.Reason"] = "FileAssociationRefusal enum member (FileAssociationSettingsViewModel)",
        ["ex.Rule"] = "the name of a diagnostics redaction rule, a constant (SettingsViewModel)",
        ["what"] = "CloudResourcesViewModel.LogFailure's parameter; every caller passes a literal phrase",
    };

    /// <summary>The unsafe holes in one call's argument text; empty when the call is clean. A non-interpolated argument must be one plain literal.</summary>
    internal static List<string> Violations(string argument)
    {
        var bad = new List<string>();
        var arg = argument.Trim();
        if (arg.StartsWith("$\"", StringComparison.Ordinal))
        {
            foreach (Match hole in Regex.Matches(arg, @"\{([^{}]*)\}"))
            {
                var expr = hole.Groups[1].Value.Trim();
                if (!TypeNameHole.IsMatch(expr) && !Whitelist.ContainsKey(expr))
                {
                    bad.Add(expr);
                }
            }
        }
        else if (!Regex.IsMatch(arg, "^\"(?:[^\"\\\\]|\\\\.)*\"$"))
        {
            bad.Add(arg);
        }

        return bad;
    }

    /// <summary>Every call's argument text (up to the matching close parenthesis, string literals skipped over).</summary>
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

            yield return source[start..(i - 1)];
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
    public void The_scanner_catches_a_planted_unsafe_call(string kind)
    {
        // Built at run time so this file never contains a bad call itself.
        var planted = kind switch
        {
            "MESSAGE" => "$\"failed: {" + "ex.Message}\"",
            "PATH" => "$\"could not open {" + "inputPath}\"",
            "VARIABLE" => "$\"seq {" + "sequence}\"",
            _ => "\"a \" + " + "fileName",
        };
        var source = "System.Diagnostics.Trace." + "TraceWarning(" + planted + ");";

        Arguments(source).SelectMany(Violations).ShouldNotBeEmpty();
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
