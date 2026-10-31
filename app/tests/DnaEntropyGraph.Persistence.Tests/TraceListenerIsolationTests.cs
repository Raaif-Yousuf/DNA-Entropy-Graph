using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Persistence.Tests;

/// <summary>
/// MEASURED 2026-10-31: the "traced once per run" assertion in SpendLedgerTests counts lines on the process-global
/// <c>Trace.Listeners</c>. LedgerRunCostSourceTests books an inverted span (end before start) too, which also traces
/// "vm-span-inverted". xUnit runs test classes in parallel, so when the two overlap the first test sees two lines
/// (landing premerge of feat/260: expected 1 but was 2). The fix is one xUnit collection for every class that books
/// an inverted span or listens to Trace, so they never overlap.
/// </summary>
public class TraceListenerIsolationTests
{
    [Theory]
    [InlineData(typeof(SpendLedgerTests))]
    [InlineData(typeof(LedgerRunCostSourceTests))]
    public void A_class_that_listens_to_or_triggers_the_inverted_span_trace_is_in_the_serial_trace_collection(Type testClass)
    {
        var attribute = testClass.GetCustomAttributes(typeof(CollectionAttribute), inherit: false).Cast<CollectionAttribute>().SingleOrDefault();

        attribute.ShouldNotBeNull($"{testClass.Name} must not run in parallel with the other users of the global Trace listeners");
        attribute.Name.ShouldBe(TraceCollection.Name);
    }

    [Fact]
    public void Every_Persistence_test_class_that_touches_Trace_Listeners_is_in_the_collection()
    {
        var sources = Directory.EnumerateFiles(Path.GetDirectoryName(SourceDir())!, "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !p.EndsWith("TraceListenerIsolationTests.cs", StringComparison.Ordinal))
            .Where(p => File.ReadAllText(p).Contains("Trace.Listeners", StringComparison.Ordinal))
            .ToList();

        sources.ShouldNotBeEmpty("vacuity: SpendLedgerTests listens to Trace.Listeners");
        foreach (var path in sources)
        {
            File.ReadAllText(path).ShouldContain("[Collection(TraceCollection.Name)]", customMessage: $"{Path.GetFileName(path)} listens to the global Trace listeners");
        }
    }

    private static string SourceDir([System.Runtime.CompilerServices.CallerFilePath] string path = "") => path;
}

/// <summary>Name of the xUnit collection that serialises the tests sharing the process-global Trace listeners.</summary>
public static class TraceCollection
{
    public const string Name = "Persistence trace listeners";
}
