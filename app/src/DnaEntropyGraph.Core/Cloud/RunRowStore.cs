using DnaEntropyGraph.Core.Abstractions;

namespace DnaEntropyGraph.Core.Cloud;

/// <summary>
/// Run-row bookkeeping for <see cref="CloudJobRunner"/>: reading the latest row of a job, writing a phase (legal transitions
/// only, committed BEFORE the UI is told) and annotating the row. The only writer of phases in the run machinery.
/// </summary>
internal sealed class RunRowStore(IRunRepository runs, Action<string, JobPhase>? onPhaseChanged)
{
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

    /// <summary>The result a persisted terminal row stands for.</summary>
    public static CloudJobResult ResultFromRow(RunRecord row)
        => new(row.Phase, row.ErrorCode is null ? null : CloudErrorKind.Other, row.ErrorDetail, row.ErrorCode);

    /// <summary>Rewrites the latest row with <paramref name="change"/> applied, phase untouched. A job with no row yet has nothing to annotate.</summary>
    public async Task UpdateRowAsync(string jobId, Func<RunRecord, RunRecord> change, CancellationToken cancellationToken)
    {
        var existing = await LatestRecordAsync(jobId, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            await runs.UpsertAsync(change(existing), cancellationToken).ConfigureAwait(false);
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

        var existing = await LatestRecordAsync(jobId, cancellationToken).ConfigureAwait(false);
        var current = existing?.Phase ?? JobPhase.Draft;
        if (current == phase || JobStateMachine.HasAlreadyPassed(current, phase))
        {
            return;
        }

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
            next = next with { FinishedAt = now };
        }

        if (errorCode is not null)
        {
            next = next with { ErrorCode = errorCode, ErrorDetail = errorDetail };
        }

        // Hard Rule 16 discipline named in issue #58's own Done-when:
        // committed to the repository BEFORE the UI (onPhaseChanged) is told.
        await runs.UpsertAsync(next, cancellationToken).ConfigureAwait(false);
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
