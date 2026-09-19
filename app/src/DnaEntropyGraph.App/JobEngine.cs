using System.Collections.Concurrent;
using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Contract;

namespace DnaEntropyGraph.App;

/// <summary>
/// Owns the active runners and (once wired) publishes
/// <c>RunProgressChanged</c>/<c>RunPhaseChanged</c> over
/// <c>WeakReferenceMessenger</c> (docs/architecture.md section 3). The
/// skeleton records a run id and nothing else - <c>CloudJobRunner</c> and
/// <c>LocalJobRunner</c> wiring is a follow-up issue under this epic.
/// </summary>
public sealed class JobEngine : IJobEngine
{
    private readonly ConcurrentDictionary<string, RunOptions> _activeRuns = new();

    public IReadOnlyList<WorkerResult> CompletedRuns { get; } = Array.Empty<WorkerResult>();

    public Task<string> StartRunAsync(RunOptions options, CancellationToken cancellationToken)
    {
        var jobId = Guid.NewGuid().ToString("n");
        _activeRuns[jobId] = options;
        return Task.FromResult(jobId);
    }

    public Task CancelRunAsync(string jobId, CancellationToken cancellationToken)
    {
        _activeRuns.TryRemove(jobId, out _);
        return Task.CompletedTask;
    }
}
