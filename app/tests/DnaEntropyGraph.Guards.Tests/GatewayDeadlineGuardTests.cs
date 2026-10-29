using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Guards.Tests;

/// <summary>
/// Issue #245: a per-call deadline in front of a gateway call is made by <c>DeadlineSource</c>, never by a raw <c>CancelAfter</c>, a timed
/// <c>CancellationTokenSource</c>, a timer that calls Cancel, or an unreviewed <c>CreateLinkedTokenSource</c>: those run a callee's throwing cancellation callback inside
/// the user's Cancel() or on a timer thread and end the process.
///
/// What this checks, exactly: every .cs file under app/src whose code (comments removed) names a gateway (an <c>I...Gateway</c> interface, <c>GatewayCalls</c> or a class named
/// <c>...Gateway</c>) is scanned, whitespace and line breaks tolerated, for the shapes in <see cref="Shapes"/>. It is a text scan, not a compiler: a timer that reaches Cancel through
/// another method, or a timed source built in a file that does not name a gateway and then handed to one, is not seen.
/// </summary>
public partial class GatewayDeadlineGuardTests
{
    private static readonly (string Name, Regex Pattern)[] Shapes =
    [
        ("CancelAfter", CancelAfterRegex()),
        ("new CancellationTokenSource(<timeout>)", TimedNewRegex()),
        ("CancellationTokenSource x = new(<timeout>)", TimedTargetTypedRegex()),
        ("CreateTimer(... Cancel ...)", TimerCancelRegex()),
    ];

    [GeneratedRegex(@"\.\s*CancelAfter\s*\(")]
    private static partial Regex CancelAfterRegex();

    [GeneratedRegex(@"new\s+CancellationTokenSource\s*\(\s*[^)\s]")]
    private static partial Regex TimedNewRegex();

    [GeneratedRegex(@"CancellationTokenSource\s*\??\s+\w+\s*=\s*new\s*\(\s*[^)\s]")]
    private static partial Regex TimedTargetTypedRegex();

    [GeneratedRegex(@"CreateTimer\s*\([^;]*?\.\s*Cancel(Async)?\s*\(")]
    private static partial Regex TimerCancelRegex();

    [GeneratedRegex(@"CreateLinkedTokenSource\s*\(")]
    private static partial Regex LinkedRegex();

    [GeneratedRegex(@"\bI\w*Gateway\b|\bGatewayCalls\b|\bclass\s+\w*Gateway\b")]
    private static partial Regex NamesGatewayRegex();

    [GeneratedRegex(@"//[^\r\n]*|/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex CommentRegex();

    /// <summary>
    /// A raw <c>CreateLinkedTokenSource</c> in a file that names a gateway, each with the reason it is not a per-call deadline. A file that no longer has one fails (a stale entry),
    /// and a file that gains one fails until someone decides which it is.
    /// </summary>
    private static readonly Dictionary<string, string> LinkedAllowlist = new()
    {
        ["JobReconciler.cs"] = "links two CALLER tokens (the row's cancel and the pass's); no timer; the per-call deadline inside it is a DeadlineSource",
        ["VmProvisioner.cs"] = "a batch source linked to the caller's token so the winner can cancel the other zones; no timer; each gateway call below it goes through GatewayCalls",
        ["ResultWaiter.cs"] = "links the run's token for the waiter's own stop; no timer; each read goes through GatewayCalls",
    };

    private static string Strip(string text) => CommentRegex().Replace(text, " ");

    private static string[] SourceFiles() => RepoPaths.AllCSharpFiles;

    private static List<string> Offenders(IEnumerable<(string File, string Text)> files)
    {
        var offenders = new List<string>();
        foreach (var (file, raw) in files)
        {
            var name = Path.GetFileName(file);
            if (name == "DeadlineSource.cs")
            {
                continue;
            }

            var code = Strip(raw);
            if (!NamesGatewayRegex().IsMatch(code))
            {
                continue;
            }

            foreach (var (shape, pattern) in Shapes)
            {
                if (pattern.IsMatch(code))
                {
                    offenders.Add($"{name}: {shape}");
                }
            }

            if (LinkedRegex().IsMatch(code) && !LinkedAllowlist.ContainsKey(name))
            {
                offenders.Add($"{name}: CreateLinkedTokenSource (not in LinkedAllowlist)");
            }
        }

        return offenders;
    }

    [Fact]
    public void No_source_file_that_names_a_gateway_makes_a_raw_timed_or_linked_deadline()
    {
        var files = SourceFiles().Select(f => (File: f, Text: File.ReadAllText(f))).ToList();
        files.Count.ShouldBeGreaterThan(200, "the scanner must see the app/src tree");
        files.Count(f => NamesGatewayRegex().IsMatch(Strip(f.Text))).ShouldBeGreaterThan(20, "the scanner must find the files that name a gateway");
        files.ShouldContain(f => Path.GetFileName(f.File) == "DeadlineSource.cs");

        Offenders(files).ShouldBeEmpty("make a call deadline with DeadlineSource (Core/Cloud/Audit)");
    }

    [Fact]
    public void The_linked_allowlist_has_no_stale_entry()
    {
        var withLink = SourceFiles()
            .Where(f => { var code = Strip(File.ReadAllText(f)); return NamesGatewayRegex().IsMatch(code) && LinkedRegex().IsMatch(code); })
            .Select(Path.GetFileName)
            .ToHashSet();

        withLink.ShouldBe(LinkedAllowlist.Keys.ToHashSet(), ignoreOrder: true);
    }

    [Theory]
    [InlineData("cts.CancelAfter(timeout);", "CancelAfter")]
    [InlineData("cts\n    .CancelAfter\n    (timeout);", "CancelAfter")]
    [InlineData("cts.CancelAfter (timeout);", "CancelAfter")]
    [InlineData("var t = new CancellationTokenSource(limit, clock);", "new CancellationTokenSource(<timeout>)")]
    [InlineData("var t = new CancellationTokenSource(\n    limit);", "new CancellationTokenSource(<timeout>)")]
    [InlineData("var t = new  CancellationTokenSource (limit);", "new CancellationTokenSource(<timeout>)")]
    [InlineData("CancellationTokenSource c = new(limit);", "CancellationTokenSource x = new(<timeout>)")]
    [InlineData("CancellationTokenSource? c = new(\n limit);", "CancellationTokenSource x = new(<timeout>)")]
    [InlineData("_t = clock.CreateTimer(_ => source.Cancel(), null, due, Timeout.InfiniteTimeSpan);", "CreateTimer(... Cancel ...)")]
    [InlineData("_t = clock.CreateTimer(\n  _ => source\n .CancelAsync(), null, due, period);", "CreateTimer(... Cancel ...)")]
    public void Every_bypass_shape_is_seen(string line, string shape)
    {
        var offenders = Offenders([("Planted.cs", "interface IFooGateway {} class X { void M() { " + line + " } }")]);

        offenders.ShouldContain($"Planted.cs: {shape}");
    }

    [Theory]
    [InlineData("var t = new CancellationTokenSource();")]
    [InlineData("// cts.CancelAfter(timeout);")]
    [InlineData("/* new CancellationTokenSource(limit) */")]
    [InlineData("_t = clock.CreateTimer(_ => Tick(), null, due, period);")]
    [InlineData("using var d = new DeadlineSource(token, limit, clock);")]
    public void Innocent_code_is_not_flagged(string line)
        => Offenders([("Planted.cs", "interface IFooGateway {} class X { void M() { " + line + "\n } }")]).ShouldBeEmpty();

    [Fact]
    public void A_raw_linked_source_in_a_gateway_file_is_flagged_unless_allowlisted()
    {
        var code = "interface IFooGateway {} class X { void M(CancellationToken t) { using var l = CancellationTokenSource.CreateLinkedTokenSource(t); } }";

        Offenders([("Planted.cs", code)]).ShouldBe(["Planted.cs: CreateLinkedTokenSource (not in LinkedAllowlist)"]);
        Offenders([("JobReconciler.cs", code)]).ShouldBeEmpty();
        Offenders([("Planted.cs", code.Replace("IFooGateway", "IFoo"))]).ShouldBeEmpty("a file that names no gateway is out of scope");
    }
}
