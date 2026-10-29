using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Guards.Tests;

/// <summary>
/// Issue #245: a per-call deadline in front of a gateway call is made by <c>DeadlineSource</c>, never by a raw
/// <c>CancelAfter</c> or a timed <c>new CancellationTokenSource(timeout)</c> in Core/Cloud: those run a callee's throwing cancellation callback on the timer thread and end the process.
/// </summary>
public partial class GatewayDeadlineGuardTests
{
    [GeneratedRegex(@"\.CancelAfter\(|new\s+CancellationTokenSource\(\s*[^)\s]")]
    private static partial Regex RawDeadline();

    private static string[] CloudFiles() => Directory.GetFiles(Path.Combine(RepoPaths.AppRoot, "src", "DnaEntropyGraph.Core", "Cloud"), "*.cs", SearchOption.AllDirectories)
        .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
        .ToArray();

    [Fact]
    public void No_Core_Cloud_file_outside_DeadlineSource_makes_a_timed_cancellation_source()
    {
        var files = CloudFiles();
        files.Length.ShouldBeGreaterThan(20, "the scanner must see the Cloud folder");
        files.ShouldContain(f => Path.GetFileName(f) == "DeadlineSource.cs");

        var offenders = files
            .Where(f => Path.GetFileName(f) != "DeadlineSource.cs")
            .SelectMany(f => File.ReadAllLines(f).Select((line, i) => (f, line, n: i + 1)))
            .Where(x => !x.line.TrimStart().StartsWith("//") && !x.line.TrimStart().StartsWith("///") && RawDeadline().IsMatch(x.line))
            .Select(x => $"{Path.GetFileName(x.f)}:{x.n}")
            .ToList();

        offenders.ShouldBeEmpty("make a call deadline with DeadlineSource (Core/Cloud/Audit)");
    }

    [Theory]
    [InlineData("cts.CancelAfter(timeout);", true)]
    [InlineData("var t = new CancellationTokenSource(limit, clock);", true)]
    [InlineData("var t = new CancellationTokenSource();", false)]
    public void The_pattern_sees_the_forbidden_shapes(string line, bool expected) => RawDeadline().IsMatch(line).ShouldBe(expected);
}
