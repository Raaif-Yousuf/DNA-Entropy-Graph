using System.Diagnostics;
using Xunit;

namespace DnaEntropyGraph.Core.Tests;

/// <summary>
/// Issue #994: stands in for every other test class that reaches a real Trace call site while DiagnosticsTraceListenerTests has the process-global listener installed.
/// It deliberately sits in the default (parallel) collection, so DiagnosticsTraceListenerTests staying in a non-parallel collection is what keeps its line counts exact.
/// </summary>
public sealed class TraceNoiseNeighbourTests
{
    [Fact]
    public async Task Concurrent_Trace_calls_from_a_neighbouring_class_do_not_reach_a_non_parallel_trace_log()
    {
        await Task.Run(() =>
        {
            for (var i = 0; i < 200_000; i++)
            {
                Trace.TraceWarning("neighbour noise");
            }
        }, TestContext.Current.CancellationToken);
    }
}
