using System.Collections.Concurrent;
using DnaEntropyGraph.Core.Abstractions;

namespace DnaEntropyGraph.Core.Cloud;

/// <summary>
/// Run-row bookkeeping for <see cref="CloudJobRunner"/>: reading the latest row of a job, writing a phase (legal transitions
/// only, committed BEFORE the UI is told) and annotating the row. The only writer of phases in the run machinery.
/// </summary>
internal sealed class RunRowStore(IRunRepository runs, Action<string, JobPhase>? onPhaseChanged)
{
    // Issue #113 (H2): every phase write of a job is atomic. One process-wide lock per job id (shared by every RunRowStore: the runner's, the cleanup's, the
    // reconciler's) so two in-process writers never interleave their read and their write, AND a write conditional on the phase that was read
    // (IRunRepository.TryUpsertIfPhaseAsync) so a writer outside this lock (another process on the same database) cannot be overwritten either.
    // A job id is a short string: the dictionary is not pruned (a few bytes per run this process touched).
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> JobLocks = new();

    private const int MaxLostRaces = 8;

    private static async Task<IDisposable> LockJobAsync(string jobId, CancellationToken cancellationToken)
    {
        var gate = JobLocks.GetOrAdd(jobId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Releaser(gate);
    }

    private sealed class Releaser(SemaphoreSlim gate) : IDisposable
    {
        public void Dispose() => gate.Release();
    }

    public async Task<RunRecord?> LatestRecordAsync(string jobId, CancellationToken cancellationToken)
    {
        var all = await runs.GetAllAsync(cancellationToken).ConfigureAwait(false);
        return LatestRecord(all, jobId);
    }

    public async Task<RunRecord?> TryLatestRecordAsync(string jobId)
    {
        try
        {
            return await LatestRecordAsync(jobId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Issue #109: records <paramref name="at"/> (the worker's own heartbeat time) as the row's last sign of life, only when it is newer than the one already recorded (a stale
    /// status.json found on a resume must not move it). Reads this job's row alone, never the whole table, and honours <paramref name="cancellationToken"/> (a cancelled run
    /// writes no more heartbeats; the cancellation propagates). Conditional on the phase read, like every row write.
    /// </summary>
    public async Task RecordHeartbeatAsync(string jobId, DateTimeOffset at, CancellationToken cancellationToken)
    {
        using var _ = await LockJobAsync(jobId, cancellationToken).ConfigureAwait(false);
        for (var attempt = 0; attempt <= MaxLostRaces; attempt++)
        {
            var existing = await runs.GetAsync(jobId, cancellationToken).ConfigureAwait(false);
            if (existing is null || (existing.LastHeartbeatAt is { } recorded && recorded >= at))
            {
                return;
            }

            if (await runs.TryUpsertIfPhaseAsync(existing with { LastHeartbeatAt = at }, existing.Phase, cancellationToken).ConfigureAwait(false))
            {
                return;
            }
        }
    }

    /// <summary>
    /// Issue #1059: moves the row's kept-aside count by <paramref name="delta"/> the moment a changed file is renamed aside (or put back), so the count outlives a cancel, an
    /// unclassified failure and an app that closed mid-download. A record, not a step: the download goes on if the row cannot be written (the tally still carries the count on the
    /// normal paths).
    /// </summary>
    public async Task AdjustKeptAsideAsync(string jobId, int delta)
    {
        try
        {
            await UpdateRowAsync(jobId, row => row with { ErrorDetail = KeptAsideDetail.Set(row.ErrorDetail, Math.Max(0, KeptAsideDetail.TryRead(row.ErrorDetail) + delta)) }, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Nothing more to do here; see the summary.
        }
    }

    /// <summary>The kept-aside count the job's row holds now (0 when there is no row or no mark).</summary>
    public async Task<int> KeptAsideAsync(string jobId)
        => KeptAsideDetail.TryRead((await TryLatestRecordAsync(jobId).ConfigureAwait(false))?.ErrorDetail);

    /// <summary>The result a persisted terminal row stands for.</summary>
    public static CloudJobResult ResultFromRow(RunRecord row)
        => new(row.Phase, row.ErrorCode is null ? null : CloudErrorKind.Other, row.ErrorDetail, row.ErrorCode);

    /// <summary>Rewrites the latest row with <paramref name="change"/> applied, phase untouched. A job with no row yet has nothing to annotate.</summary>
    public async Task UpdateRowAsync(string jobId, Func<RunRecord, RunRecord> change, CancellationToken cancellationToken)
    {
        using var _ = await LockJobAsync(jobId, cancellationToken).ConfigureAwait(false);
        for (var attempt = 0; ; attempt++)
        {
            var existing = await LatestRecordAsync(jobId, cancellationToken).ConfigureAwait(false);
            if (existing is null)
            {
                return;
            }

            // Conditional on the phase that was read, like a phase write: an annotation that read Running must not turn a Cancelled back into Running.
            if (await runs.TryUpsertIfPhaseAsync(change(existing), existing.Phase, cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            if (attempt >= MaxLostRaces)
            {
                throw new InvalidOperationException($"The run row of job '{jobId}' kept changing under an annotation.");
            }
        }
    }

    /// <summary>
    /// Issue #109: stores the span <see cref="VmSpan"/> worked out, the only writer of the ledger's two inputs. Each column keeps a value already there (a re-attach must not move the
    /// start; the terminal write keeps a finish that is set), and a row that is already terminal takes no end. A record, not a step: a failure here is swallowed.
    /// </summary>
    public async Task StampVmSpanAsync(string jobId, DateTimeOffset? start, DateTimeOffset? end)
    {
        if (start is null && end is null)
        {
            return;
        }

        try
        {
            await UpdateRowAsync(
                jobId,
                row => row with
                {
                    VmReadyAt = row.VmReadyAt ?? start,
                    FinishedAt = row.FinishedAt ?? (JobStateMachine.IsTerminal(row.Phase) ? null : end),
                },
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The row keeps what it had; the terminal write still lands.
        }
    }

    /// <summary>
    /// Issue #91: the one automatic on-demand retry after a Spot preemption. The row goes back to Provisioning (so the UI shows a computer being started again;
    /// <see cref="SetPhaseAsync"/> would treat that as an already-passed phase and do nothing), records that this attempt is not Spot, and counts the preemption,
    /// in ONE write. Throws the state machine's own error for a phase that may not go back (a cancel that won the race is Cancelling), which the runner's guard
    /// treats as standing down.
    /// </summary>
    public async Task RestartProvisioningAsync(string jobId, CancellationToken cancellationToken)
    {
        var existing = await LatestRecordAsync(jobId, cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            return;
        }

        JobStateMachine.EnsureLegalTransition(existing.Phase, JobPhase.Provisioning);
        await runs.UpsertAsync(existing with { Phase = JobPhase.Provisioning, IsSpot = false, PreemptionCount = existing.PreemptionCount + 1 }, cancellationToken).ConfigureAwait(false);
        try
        {
            onPhaseChanged?.Invoke(jobId, JobPhase.Provisioning);
        }
        catch (Exception)
        {
            // The row is committed; a notification that throws is not part of the run (as in SetPhaseAsync).
        }
    }

    public async Task SetPhaseAsync(string jobId, JobPhase phase, CancellationToken cancellationToken, string? errorCode = null, string? errorDetail = null)
    {
        if (phase is not (JobPhase.Failed or JobPhase.Cancelled or JobPhase.Cancelling))
        {
            // A cancelled run must not walk on to the next happy-path phase
            // just because a call that ignores its token happened to return.
            cancellationToken.ThrowIfCancellationRequested();
        }

        var committed = false;
        using (await LockJobAsync(jobId, cancellationToken).ConfigureAwait(false))
        {
            for (var attempt = 0; ; attempt++)
            {
                var existing = await LatestRecordAsync(jobId, cancellationToken).ConfigureAwait(false);
                var current = existing?.Phase ?? JobPhase.Draft;
                if (current == phase || JobStateMachine.HasAlreadyPassed(current, phase))
                {
                    return;
                }

                // A terminal phase is never left: from Completed, Cancelled or Failed every other phase is illegal, so a writer that lost a race to one throws here.
                JobStateMachine.EnsureLegalTransition(current, phase);

                // The repository replaces every column on upsert, so start from the
                // row the engine wrote first (name, options, project, installation
                // id...) and change only the phase; a fresh blank record would wipe
                // them on the first phase change.
                var now = DateTimeOffset.UtcNow;
                var next = existing is null
                    ? new RunRecord(jobId, phase, now)
                    : existing with { Phase = phase };
                if (JobStateMachine.IsTerminal(phase))
                {
                    next = next with { FinishedAt = next.FinishedAt ?? now };
                }

                if (errorCode is not null)
                {
                    // Issue #1059: a file renamed aside earlier in the run is still the user's to hear about, whatever wrote this terminal detail.
                    next = next with { ErrorCode = errorCode, ErrorDetail = KeptAsideDetail.Carry(errorDetail, existing?.ErrorDetail) };
                }

                // Hard Rule 16 discipline named in issue #58's own Done-when:
                // committed to the repository BEFORE the UI (onPhaseChanged) is told.
                if (await runs.TryUpsertIfPhaseAsync(next, existing?.Phase, cancellationToken).ConfigureAwait(false))
                {
                    committed = true;
                    break;
                }

                // Lost a race to a writer outside this lock: read again and judge the new row.
                if (attempt >= MaxLostRaces)
                {
                    throw new InvalidOperationException($"The run row of job '{jobId}' kept changing under a phase write to '{phase}'.");
                }
            }
        }

        if (!committed)
        {
            return;
        }

        try
        {
            onPhaseChanged?.Invoke(jobId, phase);
        }
        catch (Exception)
        {
            // The row is committed. A notification is not part of the run: a callback that throws (a window already
            // closed) must never fail the run, end a kept-alive VM, or make the result disagree with the row.
        }
    }

    /// <summary>The newest row of a job in <paramref name="all"/> (a repository that appends keeps every version; the last of equal times wins).</summary>
    internal static RunRecord? LatestRecord(IReadOnlyList<RunRecord> all, string jobId)
    {
        RunRecord? latest = null;
        foreach (var run in all)
        {
            if (run.JobId == jobId && (latest is null || run.CreatedUtc >= latest.CreatedUtc))
            {
                latest = run;
            }
        }

        return latest;
    }
}
