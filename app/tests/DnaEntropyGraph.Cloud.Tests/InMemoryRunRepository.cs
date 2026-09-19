using System.Collections.Concurrent;
using DnaEntropyGraph.Core.Abstractions;

namespace DnaEntropyGraph.Cloud.Tests;

/// <summary>
/// A tiny in-memory <see cref="IRunRepository"/> for <see cref="CloudJobRunner"/>
/// tests only - the real SQLite-backed implementation is Lane D's
/// (`DnaEntropyGraph.Persistence`), not this project's to build or edit.
/// Appends every upsert rather than replacing by job id, so a test can
/// assert the exact sequence of phases a run passed through, in order -
/// which is also what lets <see cref="CloudJobRunner.GetPhaseAsync"/>'s
/// "latest by CreatedUtc" lookup be exercised honestly instead of trivially.
/// </summary>
internal sealed class InMemoryRunRepository : IRunRepository
{
    private readonly ConcurrentQueue<RunRecord> _runs = new();

    public IReadOnlyList<RunRecord> AllRecordedInOrder => _runs.ToList();

    public Task<IReadOnlyList<RunRecord>> GetAllAsync(CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<RunRecord>>(_runs.ToList());

    public Task UpsertAsync(RunRecord run, CancellationToken cancellationToken)
    {
        _runs.Enqueue(run);
        return Task.CompletedTask;
    }
}
