using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Guards.Tests;

/// <summary>
/// Issue #245: a per-call deadline in front of a gateway call is made by <c>DeadlineSource</c>, never by a raw <c>CancelAfter</c>, a timed
/// <c>CancellationTokenSource</c>, a timer that calls Cancel, or an unreviewed <c>CreateLinkedTokenSource</c>: those run a callee's throwing cancellation callback inside
/// the user's Cancel() or on a timer thread and end the process.
///
/// What this checks, exactly: every .cs file under app/src whose code (comments and string literals removed) names a gateway (an <c>I...Gateway</c> interface, <c>GatewayCalls</c> or a class named
/// <c>...Gateway</c>) is scanned, whitespace and line breaks tolerated, for the shapes in <see cref="Shapes"/>. It is a text scan, not a compiler: a timer that reaches Cancel through
/// another method, or a timed source built in a file that does not name a gateway and then handed to one, is not seen; raw string literals are not understood.
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

    // One pass, so a "//" or "/*" inside a string cannot hide the code after it and a quote inside a comment cannot start a string. Verbatim, regular and interpolated
    // strings (their {holes} are not scanned) and char literals are blanked; comments are blanked.
    [GeneratedRegex("""(?:\$@|@\$|@|\$)?"(?:""|\\.|[^"\\\r\n])*"|'(?:\\.|[^'\\])'|//[^\r\n]*|/\*.*?\*/""", RegexOptions.Singleline)]
    private static partial Regex LiteralOrCommentRegex();

    /// <summary>
    /// A raw <c>CreateLinkedTokenSource</c> in a file that names a gateway, keyed by repo-relative path (so a same-named file elsewhere does not inherit it) with the exact
    /// count reviewed (so a further link fails). A file that no longer has its links fails (a stale entry), and a file that gains one fails until someone decides which it is.
    /// </summary>
    private static readonly Dictionary<string, (int Count, string Reason)> LinkedAllowlist = new()
    {
        ["src/DnaEntropyGraph.Core/Cloud/JobReconciler.cs"] = (3, "links two CALLER tokens (the row's cancel and the pass's); no timer; the per-call deadline inside it is a DeadlineSource"),
        ["src/DnaEntropyGraph.Core/Cloud/VmProvisioner.cs"] = (1, "pre-existing caller link (batchCts) whose token reaches compute.CreateVmAsync raw, so a throwing callee callback can still surface out of the user's Cancel() through it; tracked in #961, not fixed here"),
        ["src/DnaEntropyGraph.Presentation/ViewModels/CloudResourcesViewModel.cs"] = (1, "links the caller's token so a project switch can cancel the in-flight listing (issue #828); no timer; reached from main, reviewed at the 245 re-merge"),
        ["src/DnaEntropyGraph.Core/Cloud/ResultWaiter.cs"] = (1, "links the run's token for the waiter's own stop; no timer; each read goes through GatewayCalls"),
        ["src/DnaEntropyGraph.Core/Cloud/StreamedDownload.cs"] = (1, "links the caller's token so the stall watcher can cancel the streamed file download (issue #588); the stall limit is a poll on file length against the injected clock, not a CancelAfter timer"),
    };

    private static string Strip(string text) => LiteralOrCommentRegex().Replace(text, m => m.Value[0] switch
    {
        '"' or '@' or '$' => "\"\"",
        '\'' => "''",
        _ => " ",
    });

    private static string RelativePath(string file) => Path.IsPathRooted(file) ? Path.GetRelativePath(RepoPaths.AppRoot, file).Replace('\\', '/') : file;

    private static List<string> Offenders(IEnumerable<(string File, string Text)> files)
    {
        var offenders = new List<string>();
        foreach (var (file, raw) in files)
        {
            var relative = RelativePath(file);
            if (Path.GetFileName(relative) == "DeadlineSource.cs")
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
                    offenders.Add($"{relative}: {shape}");
                }
            }

            var links = LinkedRegex().Count(code);
            if (links > 0 && !(LinkedAllowlist.TryGetValue(relative, out var allowed) && allowed.Count == links))
            {
                offenders.Add($"{relative}: CreateLinkedTokenSource x{links} (not in LinkedAllowlist with that count)");
            }
        }

        return offenders;
    }

    [Fact]
    public void No_source_file_that_names_a_gateway_makes_a_raw_timed_or_linked_deadline()
    {
        var files = RepoPaths.AllCSharpFiles.Select(f => (File: f, Text: File.ReadAllText(f))).ToList();
        files.Count.ShouldBeGreaterThan(200, "the scanner must see the app/src tree");
        files.Count(f => NamesGatewayRegex().IsMatch(Strip(f.Text))).ShouldBeGreaterThan(20, "the scanner must find the files that name a gateway");
        files.ShouldContain(f => Path.GetFileName(f.File) == "DeadlineSource.cs");

        Offenders(files).ShouldBeEmpty("make a call deadline with DeadlineSource (Core/Cloud/Audit)");
    }

    [Fact]
    public void The_linked_allowlist_has_no_stale_entry()
    {
        var withLink = RepoPaths.AllCSharpFiles
            .Select(f => (Path: RelativePath(f), Code: Strip(File.ReadAllText(f))))
            .Where(f => NamesGatewayRegex().IsMatch(f.Code) && LinkedRegex().IsMatch(f.Code))
            .ToDictionary(f => f.Path, f => LinkedRegex().Count(f.Code));

        withLink.ShouldBe(LinkedAllowlist.ToDictionary(e => e.Key, e => e.Value.Count), ignoreOrder: true);
        LinkedAllowlist.Values.ShouldAllBe(v => v.Reason.Length > 20);
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
    [InlineData("string u = \"http://x\"; cts.CancelAfter(t);", "CancelAfter")]
    [InlineData("string u = @\"c:\\a//b\"; cts.CancelAfter(t);", "CancelAfter")]
    [InlineData("string u = $\"/*{x}\"; cts.CancelAfter(t);", "CancelAfter")]
    [InlineData("char c = '\"'; cts.CancelAfter(t);", "CancelAfter")]
    public void Every_bypass_shape_is_seen(string line, string shape)
    {
        var offenders = Offenders([("Planted.cs", "interface IFooGateway {} class X { void M() { " + line + " } }")]);

        offenders.ShouldContain($"Planted.cs: {shape}");
    }

    [Theory]
    [InlineData("var t = new CancellationTokenSource();")]
    [InlineData("// cts.CancelAfter(timeout);")]
    [InlineData("/* new CancellationTokenSource(limit) */")]
    [InlineData("string s = \"cts.CancelAfter(t)\";")]
    [InlineData("_t = clock.CreateTimer(_ => Tick(), null, due, period);")]
    [InlineData("using var d = new DeadlineSource(token, limit, clock);")]
    public void Innocent_code_is_not_flagged(string line)
        => Offenders([("Planted.cs", "interface IFooGateway {} class X { void M() { " + line + "\n } }")]).ShouldBeEmpty();

    [Fact]
    public void A_raw_linked_source_in_a_gateway_file_is_flagged_unless_allowlisted_by_path_and_count()
    {
        static string Code(int links, string gateway = "IFooGateway") => $"interface {gateway} {{}} class X {{ void M(CancellationToken t) {{ "
            + string.Concat(Enumerable.Repeat("using var l = CancellationTokenSource.CreateLinkedTokenSource(t); ", links)) + "} }";
        const string Reconciler = "src/DnaEntropyGraph.Core/Cloud/JobReconciler.cs";

        Offenders([("Planted.cs", Code(1))]).ShouldBe(["Planted.cs: CreateLinkedTokenSource x1 (not in LinkedAllowlist with that count)"]);
        Offenders([(Reconciler, Code(3))]).ShouldBeEmpty();
        Offenders([(Reconciler, Code(4))]).ShouldNotBeEmpty("a fourth link needs a review");
        Offenders([("src/Other/JobReconciler.cs", Code(3))]).ShouldNotBeEmpty("a same-named file elsewhere does not inherit the entry");
        Offenders([("Planted.cs", Code(1, "IFoo"))]).ShouldBeEmpty("a file that names no gateway is out of scope");
    }
}
