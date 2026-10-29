using System.Collections.Concurrent;
using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;
using Xunit;

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

    /// <summary>How many times the whole table was read (every <see cref="GetAllAsync"/>), so a test can prove a write path reads one job's row instead.</summary>
    public int GetAllCount => Volatile.Read(ref _getAllCount);

    private int _getAllCount;

    /// <summary>The tokens the per-job reads (<see cref="GetAsync"/>) were given, in order.</summary>
    public ConcurrentQueue<CancellationToken> GetTokens { get; } = new();

    public Task<RunRecord?> GetAsync(string jobId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        GetTokens.Enqueue(cancellationToken);
        RunRecord? latest = null;
        foreach (var run in _runs)
        {
            if (run.JobId == jobId && (latest is null || run.CreatedUtc >= latest.CreatedUtc))
            {
                latest = run;
            }
        }

        return Task.FromResult(latest);
    }

    public async Task<IReadOnlyList<RunRecord>> GetAllAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _getAllCount);
        var call = Interlocked.Increment(ref _getAllCalls);
        if (BeforeGetAll is { } hook)
        {
            await hook(call, cancellationToken).ConfigureAwait(false);
        }

        return _runs.ToList();
    }

    /// <summary>Runs before each write (the row is not stored until it returns): a test parks a write here, honouring its token. Null: writes pass straight through.</summary>
    public Func<RunRecord, CancellationToken, Task>? BeforeUpsert { get; set; }

    public async Task UpsertAsync(RunRecord run, CancellationToken cancellationToken)
    {
        if (BeforeUpsert is { } hook)
        {
            await hook(run, cancellationToken).ConfigureAwait(false);
        }

        _runs.Enqueue(run);
        Recorded?.Invoke(run);
    }

    private event Action<RunRecord>? Recorded;

    /// <summary>
    /// Completes once a stored row satisfies <paramref name="predicate"/>: a signal instead of a poll of the rows on a real clock (#567). Subscribes first and reads second, so a row
    /// stored before or after the call is seen either way. Bounded by <see cref="RunSignals.Safety"/> only so a lost signal fails the test.
    /// </summary>
    public async Task WhenRecordedAsync(Func<RunRecord, bool> predicate)
    {
        var seen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(RunRecord run)
        {
            if (predicate(run))
            {
                seen.TrySetResult();
            }
        }

        Recorded += Handler;
        try
        {
            if (_runs.Any(predicate))
            {
                seen.TrySetResult();
            }

            await seen.Task.WaitAsync(RunSignals.Safety, TestContext.Current.CancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Recorded -= Handler;
        }
    }

    private readonly object _conditionalGate = new();

    /// <summary>Atomic like the SQLite one: the check and the append happen under one lock. <see cref="BeforeUpsert"/> runs BEFORE it, in the gap between a writer's read and its write.</summary>
    public async Task<bool> TryUpsertIfPhaseAsync(RunRecord run, JobPhase? expectedPhase, CancellationToken cancellationToken)
    {
        if (BeforeUpsert is { } hook)
        {
            await hook(run, cancellationToken).ConfigureAwait(false);
        }

        lock (_conditionalGate)
        {
            if (DnaEntropyGraph.Core.Cloud.RunRowStore.LatestRecord(_runs.ToList(), run.JobId)?.Phase != expectedPhase)
            {
                return false;
            }

            _runs.Enqueue(run);
        }

        // Signalled like UpsertAsync: a test waiting on a row (WhenRecordedAsync) must see a row the runner stored through this conditional write too (#567).
        Recorded?.Invoke(run);
        return true;
    }
}
