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

    /// <summary>Runs before each read with the 1-based number of the read since it was set: a test parks the Nth reader here (honouring its token). Null: reads pass straight through.</summary>
    public Func<int, CancellationToken, Task>? BeforeGetAll
    {
        get => _beforeGetAll;
        set
        {
            _getAllCalls = 0;
            _beforeGetAll = value;
        }
    }

    private Func<int, CancellationToken, Task>? _beforeGetAll;
    private int _getAllCalls;

    public async Task<IReadOnlyList<RunRecord>> GetAllAsync(CancellationToken cancellationToken)
    {
        var call = Interlocked.Increment(ref _getAllCalls);
        if (BeforeGetAll is { } hook)
        {
            await hook(call, cancellationToken).ConfigureAwait(false);
        }

        return _runs.ToList();
    }

    public Task UpsertAsync(RunRecord run, CancellationToken cancellationToken)
    {
        _runs.Enqueue(run);
        return Task.CompletedTask;
    }
}
