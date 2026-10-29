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
        var settled = await provisioner.SettleInflightCreatesAsync(jobId).ConfigureAwait(false)
            ? CreateSettleState.Settled
            : provisioner.HasInflightCreate(jobId) ? CreateSettleState.CreateInFlight : CreateSettleState.ReuseInFlight;

        // Issue #938: the creates THIS attempt asked Compute for, claimed now so they are this cancel's alone. Each is looked up BY NAME below whatever became of its task (the real gateway
        // honours cancellation, so a cancelled create ends Canceled although Compute may have accepted the insert), even when the label listing lags.
        var sent = provisioner.ClaimSentCreates(jobId);

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

            failure = await DeleteAllAsync(jobId, settled, queued, sent, cancellationToken).ConfigureAwait(false);
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
                failure = await DeleteAllAsync(jobId, settled, queued, sent, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                failure = ex.Message;
            }
        }

        // Hard Rule 11: a cancel ends in exactly one recorded terminal state. Failed names the VM to delete by hand.
        await RecordCancelOutcomeAsync(jobId, failure).ConfigureAwait(false);

        // A create whose outcome is unknown (still in flight, or ended Canceled or by a network failure: Compute may have accepted it) stays under watch, by name, after the outcome is
        // recorded. One that a look saw, or that Compute refused, is finished with.
        foreach (var create in sent)
        {
            if (create.Landed || create.Definitive)
            {
                provisioner.ReleaseSent(create);
                continue;
            }

            var reap = Task.Run(() => ReapLateCreateAsync(create));
            _reaps[reap] = 0;
            _ = reap.ContinueWith(
                finished =>
                {
                    _reaps.TryRemove(finished, out _);
                    provisioner.ReleaseSent(create);
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<Task, byte> _reaps = new();

    /// <summary>Tests: completes when every late clean-up started so far has ended (a signal, so a test needs no sleep to know a reaper that would have deleted something had its chance).</summary>
    internal Task WhenReapsEndedAsync() => Task.WhenAll(_reaps.Keys);

    /// <summary>
    /// Issue #938: watches one create the cancel could not settle, deletes the VM it makes (by name, whatever became of the task), tells the listener how that ended (ids and classes only), and says in
    /// the run's row what became of the VM, but only while the row is still the one this attempt's cancel recorded. Never throws.
    /// </summary>
    private async Task ReapLateCreateAsync(SentCreate create)
    {
        var outcome = await terminator.ReapLateCreateAsync(create).ConfigureAwait(false);
        try
        {
            var detail = outcome.State switch
            {
                LateCreateState.Deleted => "The create request that was still in flight landed later, and the VM it made was deleted.",
                LateCreateState.NothingToDelete => "The create request that was still in flight landed later, and no VM of this run was left to delete.",
                LateCreateState.CreateFailed => "The create request that was still in flight ended without a VM, and none appeared when the app looked for it by name.",
                LateCreateState.DeleteFailed => "The create request that was still in flight landed later, and its VM could not be deleted. Delete it from the Cloud page.",
                LateCreateState.Superseded => null,
                _ => "The create request that was still in flight had not landed after the app stopped watching for it. If a VM appears, delete it from the Cloud page.",
            };
            if (detail is not null)
            {
                // Only a row this cancel recorded, and only while no newer attempt of the job has started: a later attempt's own cancel_failed is not this one's to annotate.
                var current = provisioner.CurrentGeneration(create.JobId) == create.Generation;
                var worthSaying = outcome.State is LateCreateState.Deleted or LateCreateState.DeleteFailed or LateCreateState.GaveUp;
                await rows.UpdateRowAsync(
                    create.JobId,
                    row => current && ((row.Phase == JobPhase.Failed && row.ErrorCode == RunErrorCodes.CancelFailed) || (row.Phase == JobPhase.Cancelled && worthSaying))
                        ? row with { ErrorDetail = $"{row.ErrorDetail} {detail}".Trim() }
                        : row,
                    CancellationToken.None).ConfigureAwait(false);
            }
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

    private async Task<string?> DeleteAllAsync(string jobId, CreateSettleState settled, IReadOnlyList<(string Name, string Zone)> queued, IReadOnlyList<SentCreate> sent, CancellationToken cancellationToken)
    {
        var queuedNote = await terminator.DeleteQueuedRequestsAsync(queued, cancellationToken).ConfigureAwait(false);
        var ownedNote = await terminator.DeleteOwnedVmsAndVerifyAsync(jobId, settled, cancellationToken, sent).ConfigureAwait(false);
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
