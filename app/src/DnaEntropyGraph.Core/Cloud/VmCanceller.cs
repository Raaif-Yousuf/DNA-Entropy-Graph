using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Contract;

namespace DnaEntropyGraph.Core.Cloud;

/// <summary>
/// Cancelling a run: <see cref="JobPhase.Cancelling"/>, wait (bounded) for any create the provisioner still has in flight,
/// delete every VM this job's id owns (by label lookup, not a remembered zone), then record exactly one terminal state.
/// The in-flight state stays owned by <see cref="VmProvisioner"/>; this only asks it to settle.
/// </summary>
internal sealed class VmCanceller(RunRowStore rows, VmProvisioner provisioner, VmTerminator terminator, GracefulCancel graceful, CloudRunSettings settings)
{
    public async Task CancelAsync(CloudScope scope, string jobId, CancellationToken cancellationToken)
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
        var settled = await provisioner.SettleInflightAsync(jobId).ConfigureAwait(false);

        // Issue #938: the creates THIS attempt asked Compute for, claimed now so they are this cancel's alone. Each is looked up BY NAME below whatever became of its task (the real gateway
        // honours cancellation, so a cancelled create ends Canceled although Compute may have accepted the insert), even when the label listing lags.
        var sent = provisioner.ClaimSentCreates(jobId);

        // Issue #92: a request still queued is deleted by its own name as well as by label.
        var queued = provisioner.TakePendingRequests(jobId);

        // Issue #675: ask the worker to stop first so the outputs it already has are kept; every way out of the wait ends in the delete below.
        // The ask sits INSIDE the same guard as the delete: whatever it throws (a caller who gave up, an unexpected failure of a look), the VM is still deleted and the row
        // still ends terminal. Nothing in the ask may skip the delete.
        var outcome = GracefulOutcome.NotAsked;
        var found = new List<VmDescriptor>();
        EndFailure? failure;
        try
        {
            if (existing.Bucket is { Length: > 0 } bucket)
            {
                try
                {
                    outcome = await graceful.AskAsync(scope, jobId, bucket, cancellationToken).ConfigureAwait(false);
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

            failure = await DeleteAllAsync(scope, jobId, settled, queued, sent, found.Add, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            failure = UnexpectedFailure(ex);
        }
        catch (OperationCanceledException)
        {
            // The caller gave up on the cancel, but the run must not stay in Cancelling with a VM that may be billing: one
            // last attempt that the caller's token cannot cut, then the terminal state is recorded from what it finds.
            try
            {
                failure = await DeleteAllAsync(scope, jobId, settled, queued, sent, found.Add, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                failure = UnexpectedFailure(ex);
            }
        }

        // Issue #109: the span of the VMs the delete found (VmSpan.Across: the earliest start, the latest end, still open when any was running).
        // A cancel that found no VM books nothing, and a create that lands after this look is surfaced by the label-based left-running scan, not booked here.
        var (vmStart, vmEnd) = VmSpan.Across(found);
        await rows.StampVmSpanAsync(jobId, vmStart, vmEnd).ConfigureAwait(false);

        // Hard Rule 11: a cancel ends in exactly one recorded terminal state. Failed names the VM to delete by hand.
        try
        {
            await RecordCancelOutcomeAsync(jobId, failure).ConfigureAwait(false);
            await AnnotateDonorRestoreAsync(jobId).ConfigureAwait(false);
        }
        finally
        {
            // Whatever happened above, the creates this cancel claimed are either finished with or put under watch: never dropped.
            StartWatch(jobId, sent);
        }
    }

    /// <summary>
    /// Issue #970: when this cancel stopped a VM the run had borrowed and put its donor's labels back (or could not), the row says so, in states and classes only. A courtesy: a row that cannot be
    /// annotated leaves the clean-up exactly as it was.
    /// </summary>
    private async Task AnnotateDonorRestoreAsync(string jobId)
    {
        try
        {
            if (terminator.TakeDonorRestoreSentence(jobId) is { } sentence)
            {
                await rows.UpdateRowAsync(jobId, row => row with { ErrorDetail = $"{row.ErrorDetail} {sentence}".Trim() }, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            // The row is a courtesy.
        }
    }

    /// <summary>What the row records when the cancel itself threw: the error class and the action, never the exception's message (it can carry a path or a name).</summary>
    private static EndFailure UnexpectedFailure(Exception ex) => CancelFailureOf(VmTerminator.ErrorClassOf(ex));

    /// <summary>The one sentence a cancel that could not finish records for <paramref name="errorClass"/>: names Sign in for a signed-out account and the Cloud page otherwise. Class only, never an exception's message.</summary>
    internal static EndFailure CancelFailureOf(string errorClass)
    {
        return errorClass == RunErrorCodes.AccountSignedOut
            ? new EndFailure($"The cancel could not finish ({RunErrorCodes.AccountSignedOut}). Sign in as the Google account this run was made under, then cancel again.", errorClass)
            : EndFailure.Of("The cancel could not finish", errorClass, "Check the Cloud page and delete or stop any VM left for this run.");
    }

    /// <summary>
    /// A create whose outcome is unknown (still in flight, or ended Canceled or by a network failure: Compute may have accepted it) stays under watch, by name, after the outcome is recorded: ONE loop
    /// for all of them. One that a look saw, one a look after its answer found gone, and one Compute refused are finished with: "answered a VM" alone is not (the VM may have been made after the
    /// cancel's last look).
    /// </summary>
    private void StartWatch(string jobId, IReadOnlyList<SentCreate> sent)
    {
        var watched = new List<SentCreate>();
        foreach (var create in sent)
        {
            if (create.Landed || create.Settled || create.Refused)
            {
                provisioner.ReleaseSent(create);
            }
            else
            {
                watched.Add(create);
            }
        }

        if (watched.Count == 0)
        {
            terminator.ForgetDonors(jobId); // issue #970: nothing is left that could still land, so no donor is needed again
            return;
        }

        var reap = Task.Run(() => WatchLateCreatesAsync(jobId, watched));
        _reaps[reap] = 0;
        _ = reap.ContinueWith(
            finished =>
            {
                _reaps.TryRemove(finished, out _);
                foreach (var create in watched)
                {
                    provisioner.ReleaseSent(create);
                }

                terminator.ForgetDonors(jobId); // issue #970: the watch has restored what it could
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<Task, byte> _reaps = new();

    /// <summary>Tests: completes when every late clean-up started so far has ended (a signal, so a test needs no sleep to know a reaper that would have deleted something had its chance).</summary>
    internal Task WhenReapsEndedAsync() => Task.WhenAll(_reaps.Keys);

    /// <summary>
    /// Issue #938: watches the creates the cancel could not settle (one loop), deletes the VMs they make (by name, whatever became of the tasks), says in the run's row what became of them in ONE sentence,
    /// but only while the row is still the one this attempt's cancel recorded, and tells the listener how each ended (ids and classes only). Never throws.
    /// </summary>
    private async Task WatchLateCreatesAsync(string jobId, IReadOnlyList<SentCreate> watched)
    {
        IReadOnlyList<LateCreateOutcome> outcomes;
        try
        {
            outcomes = await terminator.ReapLateCreatesAsync(watched).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return;
        }

        try
        {
            // Only a row this cancel recorded, and only while no newer attempt of the job has started: a later attempt's own cancel_failed is not this one's to annotate.
            var current = provisioner.CurrentGeneration(jobId) == watched.Max(c => c.Generation);
            if (current)
            {
                await rows.UpdateRowAsync(
                    jobId,
                    row =>
                    {
                        var sentence = SentenceFor(outcomes, row);
                        var code = CodeFor(outcomes, row);
                        if (sentence is null && code is null)
                        {
                            return row;
                        }

                        return row with { ErrorDetail = sentence is null ? row.ErrorDetail : $"{row.ErrorDetail} {sentence}".Trim(), ErrorCode = code ?? row.ErrorCode };
                    },
                    CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            // The row is a courtesy; the clean-up already happened.
        }

        foreach (var outcome in outcomes)
        {
            try
            {
                settings.OnLateCreate?.Invoke(outcome);
            }
            catch (Exception)
            {
                // A listener that throws must not matter to the clean-up.
            }
        }
    }

    /// <summary>
    /// What the user sees: the Runs page shows the message of a finished row's error code, never its detail. A Cancelled row whose late VM could not be confirmed deleted or stopped (or never seen) gets
    /// <see cref="RunErrorCodes.LateCreateUnconfirmed"/> (action: the Cloud page); a cancel_failed row already says it. Null when the row's code stays.
    /// </summary>
    internal static string? CodeFor(IReadOnlyList<LateCreateOutcome> outcomes, RunRecord row)
        => row.Phase == JobPhase.Cancelled && string.IsNullOrEmpty(row.ErrorCode)
           && outcomes.Any(o => o.State is LateCreateState.DeleteFailed or LateCreateState.CouldNotConfirm or LateCreateState.GaveUp or LateCreateState.ReuseStopFailed)
            ? RunErrorCodes.LateCreateUnconfirmed
            : null;

    /// <summary>
    /// The one sentence a cancel's row gets about the creates it watched, worded for the row's state. A Cancelled row says only what the user needs (a VM deleted, or one to delete by hand); a
    /// cancel_failed row also says it stays failed, so the sentence never reads as an all-clear. Null when there is nothing to add.
    /// </summary>
    internal static string? SentenceFor(IReadOnlyList<LateCreateOutcome> outcomes, RunRecord row)
    {
        var failedRow = row.Phase == JobPhase.Failed && RunErrorCodes.IsUnconfirmedCancel(row.ErrorCode);
        if (!failedRow && row.Phase != JobPhase.Cancelled)
        {
            return null;
        }

        const string StillFailed = " This cancel is still recorded as failed because of the problem above: check the Cloud page.";
        bool Any(LateCreateState state) => outcomes.Any(o => o.State == state);
        if (Any(LateCreateState.DeleteFailed))
        {
            return "A create request that was still in flight landed later, and its VM could not be deleted. Delete it from the Cloud page.";
        }

        if (Any(LateCreateState.ReuseStopFailed))
        {
            return "A request to start an earlier VM landed after the cancel, and the app could not stop it. Stop it from the Cloud page.";
        }

        if (Any(LateCreateState.CouldNotConfirm))
        {
            return "The app could not tell whether a create request that was still in flight made a VM. Check the Cloud page and delete any VM left for this run.";
        }

        if (Any(LateCreateState.GaveUp))
        {
            return "A create request that was still in flight had not landed after the app stopped watching for it. If a VM appears, delete it from the Cloud page.";
        }

        if (Any(LateCreateState.Deleted))
        {
            return "A create request that was still in flight landed later, and the VM it made was deleted." + (failedRow ? StillFailed : string.Empty);
        }

        if (Any(LateCreateState.ReuseStopped))
        {
            // Issue #970: and what became of the labels the reuse had moved onto this job (states only; a failed restore names the Cloud page).
            var restored = DonorRestoreOutcome.SentenceFor([.. outcomes.Where(o => o.Restore is not null).Select(o => o.Restore!)]);
            return "A request to start an earlier VM landed after the cancel, and the VM was stopped again." + (restored is null ? string.Empty : " " + restored) + (failedRow ? StillFailed : string.Empty);
        }

        if (failedRow && Any(LateCreateState.CreateFailed))
        {
            return "A create request that was still in flight ended without a VM, and none appeared when the app looked for it by name." + StillFailed;
        }

        return null;
    }

    private async Task<EndFailure?> DeleteAllAsync(CloudScope scope, string jobId, CreateSettleState settled, IReadOnlyList<(string Name, string Zone)> queued, IReadOnlyList<SentCreate> sent, Action<VmDescriptor> found, CancellationToken cancellationToken)
    {
        using var hold = terminator.Ledger.HoldDelete(jobId); // round 4 M2: the queued requests' deletes too
        var queuedNote = await terminator.DeleteQueuedRequestsAsync(scope, queued, cancellationToken).ConfigureAwait(false);
        var ownedNote = await terminator.DeleteOwnedVmsAndVerifyAsync(scope, jobId, settled, cancellationToken, sent, found).ConfigureAwait(false);
        return ownedNote ?? queuedNote;
    }

    /// <summary>
    /// Records how a cancel ended: Cancelled when the VMs are confirmed gone, else Failed under <see cref="RunErrorCodes.CancelFailed"/>.
    /// A row that is already terminal (a second cancel that raced the first) is left exactly as it is, and nothing here throws.
    /// </summary>
    internal async Task RecordCancelOutcomeAsync(string jobId, EndFailure? failure)
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
                // Issue #830 review M6: a sign-in is its own action (the Cloud page lists the SELECTED account, not this run's), so it gets its own code; the reconciler treats both as an unconfirmed cancel.
                // Review F5: the code comes from the typed class of the failure, never from searching its English sentence.
                var code = failure.AccountSignedOut ? RunErrorCodes.AccountSignedOut : RunErrorCodes.CancelFailed;
                await rows.SetPhaseAsync(jobId, JobPhase.Failed, CancellationToken.None, code, failure.Message).ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            // The repository itself is failing: nothing more can be recorded, and a throw here would only hide the run's state.
        }
    }
}
