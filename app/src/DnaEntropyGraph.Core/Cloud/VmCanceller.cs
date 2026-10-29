using DnaEntropyGraph.Core.Contract;

namespace DnaEntropyGraph.Core.Cloud;

/// <summary>
/// Cancelling a run: <see cref="JobPhase.Cancelling"/>, wait (bounded) for any create the provisioner still has in flight,
/// delete every VM this job's id owns (by label lookup, not a remembered zone), then record exactly one terminal state.
/// The in-flight state stays owned by <see cref="VmProvisioner"/>; this only asks it to settle.
/// </summary>
internal sealed class VmCanceller(RunRowStore rows, VmProvisioner provisioner, VmTerminator terminator, GracefulCancel graceful, CloudRunSettings settings)
{
    public async Task CancelAsync(string jobId, CancellationToken cancellationToken)
    {
        var existing = await rows.LatestRecordAsync(jobId, cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            // A job id this repository never saw: nothing to cancel.
            return;
        }

        var current = existing.Phase;
        if (JobStateMachine.IsTerminal(current))
        {
            return;
        }

        if (current == JobPhase.Draft)
        {
            // Cancelled during preflight, before the first phase: no VM can
            // exist yet, and Draft -> Cancelling is not a legal transition.
            await rows.SetPhaseAsync(jobId, JobPhase.Cancelled, cancellationToken).ConfigureAwait(false);
            return;
        }

        // Issue #91: from here the VM going away is this app's doing, never a Spot preemption to retry.
        terminator.NoteAppRequestedEnd(jobId);
        await rows.SetPhaseAsync(jobId, JobPhase.Cancelling, CancellationToken.None).ConfigureAwait(false);

        // A create the caller gave up on can still land. Wait for it (bounded) before looking, or the VM appears
        // after the run is recorded Cancelled and nothing ever deletes it.
        var settled = await provisioner.SettleInflightCreatesAsync(jobId).ConfigureAwait(false);

        // Issue #938: the creates of THIS attempt, taken now, so they belong to this cancel alone (a later attempt of the job registers its own). One that answered is
        // deleted by name below even when the label listing lags; one still in flight is cleaned up when it lands, after the outcome is recorded.
        var attemptCreates = provisioner.TakeOwnedCreates(jobId);
        // One look at each, so a create that finishes during the partition lands in exactly one list and none is missed.
        var looked = attemptCreates.Select(t => (Task: t, Done: t.IsCompleted)).ToList();
        var answered = looked.Where(x => x.Done && x.Task.IsCompletedSuccessfully).Select(x => x.Task.Result).ToList();
        var late = looked.Where(x => !x.Done).Select(x => x.Task).ToList();

        // Issue #92: a request still queued is deleted by its own name as well as by label.
        var queued = provisioner.TakePendingRequests(jobId);

        // Issue #675: ask the worker to stop first so the outputs it already has are kept; every way out of the wait ends in the delete below.
        // The ask sits INSIDE the same guard as the delete: whatever it throws (a caller who gave up, an unexpected failure of a look), the VM is still deleted and the row
        // still ends terminal. Nothing in the ask may skip the delete.
        var outcome = GracefulOutcome.NotAsked;
        string? failure;
        try
        {
            if (existing.Bucket is { Length: > 0 } bucket)
            {
                try
                {
                    outcome = await graceful.AskAsync(jobId, bucket, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    // The ask is best effort: not a reason to leave the VM.
                }

                // Hard Rule 14: the partial outputs a worker uploaded before it stopped stay reachable. The row keeps where the job lives (bucket and job folder), so
                // "download again" works while the objects exist; a row from before the folder was recorded gets it now.
                if (outcome is GracefulOutcome.Acknowledged or GracefulOutcome.Finished && existing.JobPrefix is null)
                {
                    try
                    {
                        await rows.UpdateRowAsync(jobId, row => row with { Bucket = row.Bucket ?? bucket, JobPrefix = row.JobPrefix ?? WorkerManifestBuilder.JobPrefix(jobId) }, CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception)
                    {
                        // A row that cannot be annotated must not stop the delete.
                    }
                }
            }

            failure = await DeleteAllAsync(jobId, settled, queued, answered, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            failure = ex.Message;
        }
        catch (OperationCanceledException)
        {
            // The caller gave up on the cancel, but the run must not stay in Cancelling with a VM that may be billing: one
            // last attempt that the caller's token cannot cut, then the terminal state is recorded from what it finds.
            try
            {
                failure = await DeleteAllAsync(jobId, settled, queued, answered, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                failure = ex.Message;
            }
        }

        // Hard Rule 11: a cancel ends in exactly one recorded terminal state. Failed names the VM to delete by hand.
        await RecordCancelOutcomeAsync(jobId, failure).ConfigureAwait(false);

        foreach (var create in late)
        {
            _ = Task.Run(() => ReapLateCreateAsync(jobId, create));
        }
    }

    /// <summary>
    /// Issue #938: waits (bounded by the reap window) for one create the cancel stopped waiting for, deletes the VM it made, tells the listener how that ended (ids and classes only),
    /// and, when the cancel was recorded Failed for exactly this, says in the row what became of the VM. Never throws.
    /// </summary>
    private async Task ReapLateCreateAsync(string jobId, Task<VmDescriptor> create)
    {
        var outcome = await terminator.ReapLateCreateAsync(jobId, create).ConfigureAwait(false);
        try
        {
            var detail = outcome.State switch
            {
                LateCreateState.Deleted => "The create request that was still in flight landed later, and the VM it made was deleted.",
                LateCreateState.NothingToDelete => "The create request that was still in flight landed later, and no VM of this run was left to delete.",
                LateCreateState.CreateFailed => "The create request that was still in flight failed, so it made no VM.",
                LateCreateState.DeleteFailed => "The create request that was still in flight landed later, and its VM could not be deleted. Delete it from the Cloud page.",
                _ => "The create request that was still in flight had not landed after the app stopped watching for it. If a VM appears, delete it from the Cloud page.",
            };
            await rows.UpdateRowAsync(
                jobId,
                row => row.Phase == JobPhase.Failed && row.ErrorCode == RunErrorCodes.CancelFailed ? row with { ErrorDetail = $"{row.ErrorDetail} {detail}".Trim() } : row,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The row is a courtesy; the clean-up already happened.
        }

        try
        {
            settings.OnLateCreate?.Invoke(outcome);
        }
        catch (Exception)
        {
            // A listener that throws must not matter to the clean-up.
        }
    }

    private async Task<string?> DeleteAllAsync(string jobId, bool settled, IReadOnlyList<(string Name, string Zone)> queued, IReadOnlyList<VmDescriptor> answered, CancellationToken cancellationToken)
    {
        var queuedNote = await terminator.DeleteQueuedRequestsAsync(queued, cancellationToken).ConfigureAwait(false);
        var ownedNote = await terminator.DeleteOwnedVmsAndVerifyAsync(jobId, settled, cancellationToken, answered).ConfigureAwait(false);
        return ownedNote ?? queuedNote;
    }

    /// <summary>
    /// Records how a cancel ended: Cancelled when the VMs are confirmed gone, else Failed under <see cref="RunErrorCodes.CancelFailed"/>.
    /// A row that is already terminal (a second cancel that raced the first) is left exactly as it is, and nothing here throws.
    /// </summary>
    public async Task RecordCancelOutcomeAsync(string jobId, string? failure)
    {
        try
        {
            var row = await rows.LatestRecordAsync(jobId, CancellationToken.None).ConfigureAwait(false);
            if (row is not null && JobStateMachine.IsTerminal(row.Phase))
            {
                return;
            }

            if (failure is null)
            {
                await rows.SetPhaseAsync(jobId, JobPhase.Cancelled, CancellationToken.None).ConfigureAwait(false);
            }
            else
            {
                await rows.SetPhaseAsync(jobId, JobPhase.Failed, CancellationToken.None, RunErrorCodes.CancelFailed, failure).ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            // The repository itself is failing: nothing more can be recorded, and a throw here would only hide the run's state.
        }
    }
}
