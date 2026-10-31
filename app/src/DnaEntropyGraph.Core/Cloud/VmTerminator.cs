using System.Globalization;

namespace DnaEntropyGraph.Core.Cloud;

/// <summary>
/// Review F5: why a cancel's delete or read-back could not finish, as the sentence the row keeps AND the error class that decided it. The class is typed, so the row's code
/// (<see cref="RunErrorCodes.AccountSignedOut"/> or <see cref="RunErrorCodes.CancelFailed"/>) is chosen from it, never by searching the English sentence.
/// </summary>
internal sealed record EndFailure(string Message, string ErrorClass)
{
    public bool AccountSignedOut => ErrorClass == RunErrorCodes.AccountSignedOut;

    /// <summary>"{stem} ({class}). {action}", except that a signed-out account's sentence names the sign-in action instead of the Cloud page (nothing on the Cloud page can fix it).</summary>
    public static EndFailure Of(string stem, string errorClass, string cloudAction)
        => new(errorClass == RunErrorCodes.AccountSignedOut
            ? $"{stem} ({errorClass}). Sign in as the Google account this run was made under, then try again."
            : $"{stem} ({errorClass}). {cloudAction}", errorClass);

    /// <summary>A failure with no error class of its own (a timeout, a read that never answered).</summary>
    public static EndFailure Other(string message) => new(message, RunErrorCodes.Other);
}

/// <summary>
/// Hard Rule 11: ends a run's VM (stop or delete), verifies it independently, and finds VMs by job-id label (Hard Rule 9).
/// Holds no run state: every method is a look at the gateway plus a bounded wait.
/// </summary>
internal sealed class VmTerminator(IComputeGateway compute, GatewayCalls calls, CloudRunSettings settings)
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _appRequestedEnds = new(StringComparer.Ordinal);

    /// <summary>Issue #938: which attempt of a job owns which VM, and the lock that orders a late clean-up's delete against a newer attempt's adoption. Shared with <see cref="VmProvisioner"/>.</summary>
    public AttemptLedger Ledger { get; } = new();

    /// <summary>
    /// Issue #91: this app itself is ending the job's VM (the run page's Stop or Delete, or a cancel). A VM that is gone after that is not a Spot preemption,
    /// so the runner must not answer it with a paid on-demand retry. Remembered for the life of this runner.
    /// </summary>
    public void NoteAppRequestedEnd(string jobId) => _appRequestedEnds[jobId] = 0;

    /// <summary>
    /// Forgets the mark. The runner calls it just before it provisions a job's first VM: a Stop or Delete the person pressed on an earlier VM of this job (a kept-alive
    /// one) says nothing about the VM about to exist, and must not silence its retry for the rest of the process. A Stop, Delete or cancel DURING the run sets it again.
    /// </summary>
    public void ClearAppRequestedEnd(string jobId) => _appRequestedEnds.TryRemove(jobId, out _);

    /// <summary>True once <see cref="NoteAppRequestedEnd"/> was called for the job (and not cleared since).</summary>
    public bool AppRequestedEnd(string jobId) => _appRequestedEnds.ContainsKey(jobId);

    /// <summary>
    /// Best effort, never throws: a run that failed or gave up must still end its VM (Hard Rule 11), by
    /// delete when the user chose delete and by stop otherwise. Returns null when the VM was confirmed
    /// ended, else a sentence for the run's detail saying it was not, so no message claims a stop nobody checked.
    /// </summary>
    public async Task<string?> EndVmAfterFailureAsync(CloudJobRequest request, string? zone, bool forceDelete = false, bool forceStop = false)
    {
        var error = await EndVmAfterFailureClassifiedAsync(request, zone, forceDelete, forceStop).ConfigureAwait(false);
        return error is null ? null : " (VM end not confirmed: " + error.Message + ")";
    }

    /// <summary><see cref="EndVmAfterFailureAsync"/> returning the typed failure (null when the VM was confirmed ended): the class, not the sentence, picks a row's code (review L4 round 3).</summary>
    internal async Task<EndFailure?> EndVmAfterFailureClassifiedAsync(CloudJobRequest request, string? zone, bool forceDelete = false, bool forceStop = false)
    {
        // forceDelete wins; forceStop (the user chose Stop at a stall, #675) overrides the run's own end choice.
        var action = forceDelete ? AfterTaskAction.Delete : forceStop ? AfterTaskAction.Stop : FailureEndAction(request);

        // Issue #999: a failed run deletes only a VM it CREATED (OwnVmName). One it borrowed by reuse keeps its owner's name and is stopped instead, whichever way the delete was asked for.
        return zone is not null
            ? await EnsureVmEndedClassifiedAsync(request, zone, action, CancellationToken.None, ownOnly: true).ConfigureAwait(false)
            : await EnsureVmsEndedByLabelAsync(request, action).ConfigureAwait(false);
    }

    /// <summary>
    /// What a failed run does to its VM: delete when the user chose delete, or chose keep-alive with delete afterwards
    /// (startup.sh applies afterKeepAlive at once for a run that did not succeed), otherwise stop.
    /// </summary>
    internal static AfterTaskAction FailureEndAction(CloudJobRequest request) => request.AfterTask switch
    {
        AfterTaskAction.Delete => AfterTaskAction.Delete,
        AfterTaskAction.KeepAlive when request.Options.AfterKeepAlive == AfterKeepAliveAction.Delete => AfterTaskAction.Delete,
        _ => AfterTaskAction.Stop,
    };

    /// <summary>Ends whatever VMs carry this job's label (Hard Rule 9), for a run that does not know the zone: a resume, or an insert that may have landed anywhere.</summary>
    private async Task<EndFailure?> EnsureVmsEndedByLabelAsync(CloudJobRequest request, AfterTaskAction action)
    {
        IReadOnlyList<VmDescriptor> owned;
        try
        {
            owned = await calls.CallAsync(token => compute.FindByJobIdAsync(request.ResolvedScope, request.JobId, token), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Never throws (the callers are already handling a failure): anything that is not the caller cancelling is
            // "could not look", and the run says the VM end is unconfirmed.
            return new EndFailure($"the VMs for this job could not be looked up: {ex.Message}", ErrorClassOf(ex));
        }

        EndFailure? error = null;
        foreach (var vm in owned)
        {
            // Every VM is ended even when an earlier one could not be: the first failure is what is reported.
            var thisError = await EnsureVmEndedClassifiedAsync(request, vm.Zone, action, CancellationToken.None, vm.Name, ownOnly: true).ConfigureAwait(false);
            error ??= thisError;
        }

        return error;
    }

    /// <summary>
    /// Hard Rule 11: end the VM per <paramref name="action"/>, then verify it independently, never trusting a
    /// call that returned. Tolerates a worker that already acted: a VM already gone is a delete done (and a stop
    /// done), a delete that finds nothing (404) is success, a VM <c>STOPPING</c> is polled to
    /// <see cref="CloudRunSettings.LifecycleTimeout"/>, and both <c>STOPPED</c> and <c>TERMINATED</c> count as stopped.
    /// Returns null when the terminal state is confirmed, else why it is not. Never throws for a cloud
    /// failure: the caller decides what an unconfirmed VM means for the run.
    /// </summary>
    public async Task<string?> EnsureVmEndedAsync(CloudJobRequest request, string? zone, AfterTaskAction action, CancellationToken cancellationToken, string? vmName = null, bool ownOnly = false)
        => (await EnsureVmEndedClassifiedAsync(request, zone, action, cancellationToken, vmName, ownOnly).ConfigureAwait(false))?.Message;

    /// <summary><see cref="EnsureVmEndedAsync"/> with the failure's typed error class (review L4 round 3), so a caller picks the row's code from the class and never by reading the sentence.</summary>
    internal async Task<EndFailure?> EnsureVmEndedClassifiedAsync(CloudJobRequest request, string? zone, AfterTaskAction action, CancellationToken cancellationToken, string? vmName = null, bool ownOnly = false)
    {
        if (zone is null)
        {
            return null;
        }

        // A VM found by label is ended under its own name and zone, never the spec's.
        var name = vmName ?? request.Spec.VmName;

        // Issue #999 (ownOnly, the failure paths): a delete of a VM this run did not create (a reuse relabelled it to this job) becomes a stop. A user's own after-run Delete choice (ownOnly false) is unchanged.
        var borrowed = ownOnly && name != OwnVmName(request.JobId);
        if (borrowed && action == AfterTaskAction.Delete)
        {
            action = AfterTaskAction.Stop;
        }

        // Round 4 M2: a delete of the job's VM holds the barrier a new attempt waits behind, from before the read to the verified end.
        var hold = action == AfterTaskAction.Delete ? Ledger.HoldDelete(request.JobId) : null;
        try
        {
            var vm = await calls.CallAsync(token => compute.GetVmAsync(request.ResolvedScope, name, zone, token), cancellationToken).ConfigureAwait(false);
            if (vm is null)
            {
                return null;
            }

            if (action != AfterTaskAction.Delete && !borrowed && VmFacts.IsBooting(vm))
            {
                // THEORY (unverified): instances.stop on an instance that is not RUNNING is rejected, and a VM that never
                // reached RUNNING has no disk worth keeping: a failure that lands while it boots deletes it, as the boot
                // deadline does, whatever the user chose for after a run.
                action = AfterTaskAction.Delete;
                hold = Ledger.HoldDelete(request.JobId);
            }

            if (action == AfterTaskAction.Delete)
            {
                await calls.CallAsync(
                    async token =>
                    {
                        try
                        {
                            await compute.DeleteVmAsync(request.ResolvedScope, name, zone, token).ConfigureAwait(false);
                        }
                        catch (CloudOperationException ex) when (ex.Error.HttpStatus == 404)
                        {
                            // Already deleted, by the worker or by the platform: the state we wanted.
                        }

                        return true;
                    },
                    cancellationToken).ConfigureAwait(false);
            }
            var stopSent = true;
            if (action != AfterTaskAction.Delete && !VmFacts.IsStopped(vm) && vm.Status != "STOPPING")
            {
                if (borrowed && VmFacts.IsBooting(vm))
                {
                    // Round 7 M3: a borrowed VM is never deleted, and a stop on a VM that is not RUNNING may be refused (THEORY, unverified), so no stop is sent while it boots: the poll below sends it
                    // once the VM reads RUNNING, bounded by LifecycleTimeout.
                    stopSent = false;
                }
                else
                {
                    await StopQuietlyAsync(request.ResolvedScope, name, zone, cancellationToken).ConfigureAwait(false);
                }
            }

            var clockStart = settings.StartClock();
            while (true)
            {
                vm = await calls.CallAsync(token => compute.GetVmAsync(request.ResolvedScope, name, zone, token), cancellationToken).ConfigureAwait(false);
                if (vm is null || (action != AfterTaskAction.Delete && VmFacts.IsStopped(vm)))
                {
                    if (vm is not null && borrowed && !ReuseUnanswered(request.JobId, name, zone))
                    {
                        // Round 2 finding 1: never while a reuse of this VM has not answered (a Network failure the start POST may have survived): that start can still land and would run the VM under the donor's
                        // labels and keep-alive. The watch the provisioner starts for that reuse stops the VM if it lands, and restores only then.
                        // Issue #970: the borrowed VM is confirmed stopped, so it goes back under the labels of the run it came from (its keep-alive expiry applies again, and no label of THIS job finds it).
                        await RestoreAndNoteAsync(request.JobId, name, zone).ConfigureAwait(false);
                    }

                    return null;
                }

                if (!stopSent && vm.Status == "RUNNING")
                {
                    await StopQuietlyAsync(request.ResolvedScope, name, zone, cancellationToken).ConfigureAwait(false);
                    stopSent = true;
                }

                if (settings.ElapsedSince(clockStart) >= settings.LifecycleTimeout)
                {
                    if (!stopSent)
                    {
                        // Round 8 L2: no stop was ever sent (a borrowed VM that never left booting), so never say "after Stop".
                        return EndFailure.Other($"VM '{name}' in zone '{zone}' was still '{vm.Status}' {settings.LifecycleTimeout.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture)} s after the app started waiting, so the stop could not be sent while it was starting. Stop it from the Cloud page.");
                    }

                    return EndFailure.Other(action == AfterTaskAction.Delete
                        ? $"VM '{name}' in zone '{zone}' was still '{vm.Status}' {settings.LifecycleTimeout.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture)} s after Delete."
                        : $"VM '{name}' in zone '{zone}' was still '{vm.Status}' {settings.LifecycleTimeout.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture)} s after Stop (Hard Rule 11).");
                }

                await settings.DelayAsync(settings.LifecyclePollInterval, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && cancellationToken.IsCancellationRequested))
        {
            return new EndFailure($"VM '{name}' in zone '{zone}' could not be confirmed {(action == AfterTaskAction.Delete ? "deleted" : "stopped")}: {ex.Message}", ErrorClassOf(ex));
        }
        finally
        {
            hold?.Dispose();
        }
    }

    /// <summary>The labels a borrowed VM carried BEFORE a reuse claimed it, and the project and account the run acts in (issue #830): what a failed or cancelled run puts back.</summary>
    private sealed record Donor(CloudScope Scope, IReadOnlyDictionary<string, string> Labels, long NotedAt);

    private readonly System.Collections.Concurrent.ConcurrentDictionary<(string JobId, string Name, string Zone), SentCreate> _reuseSends = new();

    private readonly System.Collections.Concurrent.ConcurrentDictionary<(string JobId, string Name, string Zone), Donor> _donors = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, System.Collections.Concurrent.ConcurrentQueue<DonorRestoreOutcome>> _restoreOutcomes = new(StringComparer.Ordinal);

    /// <summary>
    /// Issue #970: records, BEFORE the reuse is sent, the labels the donor VM carries now, so a failed or cancelled reuse can put them back (the reuse replaces the whole label set, the VM's
    /// own job-id and lifecycle with it). The scope is the run's own (never whatever is selected later). A copy is kept: the caller's dictionary may be the descriptor's.
    /// </summary>
    public void NoteDonor(CloudScope scope, string jobId, string name, string zone, IReadOnlyDictionary<string, string> labels)
    {
        PruneExpiredDonors();
        _donors[(jobId, name, zone)] = new Donor(scope, new Dictionary<string, string>(labels, StringComparer.Ordinal), settings.StartClock());
    }

    /// <summary>
    /// Round 2 finding 6: a donor is needed only until the run's VM end, a cancel and the late watch (bounded by <see cref="CloudRunSettings.LateCreateReapWindow"/> plus the grace and the lifecycle waits)
    /// have had their turn. One whose job never released its claimed creates (a cancel that threw before it started its watch) would otherwise live for the whole process, so any older than that bound is
    /// dropped whenever a new donor is noted.
    /// </summary>
    private void PruneExpiredDonors()
    {
        var bound = settings.LateCreateReapWindow + settings.LateCreateGrace + settings.CreateSettleTimeout + settings.LifecycleTimeout + settings.LifecycleTimeout;
        foreach (var (key, donor) in _donors.ToList())
        {
            if (settings.ElapsedSince(donor.NotedAt) >= bound)
            {
                _donors.TryRemove(key, out _);
                _reuseSends.TryRemove(key, out _);
            }
        }
    }

    /// <summary>Tests: how many donors are remembered.</summary>
    internal int DonorCount => _donors.Count;

    /// <summary>Round 2 finding 1: the reuse start sent for a donor, recorded BEFORE the gateway is called, so a restore can tell whether that start has answered.</summary>
    public void NoteReuseSend(SentCreate sent) => _reuseSends[(sent.JobId, sent.Name, sent.Zone)] = sent;

    /// <summary>True while a reuse start of this VM, sent for this job, has neither answered nor been refused: it may still land, so the donor's labels must not be put back yet.</summary>
    internal bool ReuseUnanswered(string jobId, string name, string zone)
        => _reuseSends.TryGetValue((jobId, name, zone), out var sent) && !sent.Answered && !sent.Refused;

    /// <summary>Drops the donors noted for a job whose run has ended (a restore that already happened removed its own).</summary>
    public void ForgetDonors(string jobId)
    {
        foreach (var key in _donors.Keys.Where(k => k.JobId == jobId).ToList())
        {
            _donors.TryRemove(key, out _);
        }

        foreach (var key in _reuseSends.Keys.Where(k => k.JobId == jobId).ToList())
        {
            _reuseSends.TryRemove(key, out _);
        }
    }

    /// <summary>The sentence the row of a failed run gets about the VM it borrowed, taken once; null when nothing was restored, left or failed for the job.</summary>
    public string? TakeDonorRestoreSentence(string jobId)
        => _restoreOutcomes.TryRemove(jobId, out var queue) ? DonorRestoreOutcome.SentenceFor([.. queue]) : null;

    /// <summary>Forgets the outcomes noted for a job whose run ended without needing them (never reaches a row).</summary>
    public void ForgetRestoreOutcomes(string jobId) => _restoreOutcomes.TryRemove(jobId, out _);

    /// <summary>Tests: how many donors and restore outcomes are still remembered.</summary>
    internal int DonorStateCount => _donors.Count + _restoreOutcomes.Count;

    /// <summary><see cref="RestoreDonorLabelsAsync"/>, and the outcome is kept for the job's row (<see cref="TakeDonorRestoreSentence"/>). For the failure and cancel paths, which record on the row themselves.</summary>
    internal async Task RestoreAndNoteAsync(string jobId, string name, string zone)
    {
        if (await RestoreDonorLabelsAsync(jobId, name, zone).ConfigureAwait(false) is { } outcome)
        {
            _restoreOutcomes.GetOrAdd(jobId, _ => new()).Enqueue(outcome);
        }
    }

    /// <summary>
    /// Issue #970: puts a borrowed VM's labels back to what the run it came from gave it, once the VM is STOPPED (waited for, bounded by <see cref="CloudRunSettings.LifecycleTimeout"/>): a label write
    /// on a VM whose start may still be on the wire would let that start run a VM no label of the failed job finds. The write is the gateway's fingerprinted read-then-write that leaves a VM a
    /// newer run has taken. The donor is claimed for the restore (a concurrent restore of the same VM sees none), and is NOT kept again whatever the outcome: nothing retries a restore (round 2 finding 5), so a NotStopped or Failed outcome names the Cloud page and the #938 by-name guards are the backstop.
    /// Null when no donor was noted (nothing was borrowed, or the labels were already put back). Never throws; the failure is the outcome, with its class only.
    /// </summary>
    internal async Task<DonorRestoreOutcome?> RestoreDonorLabelsAsync(string jobId, string name, string zone)
    {
        var key = (jobId, name, zone);
        if (!_donors.TryRemove(key, out var donor))
        {
            return null;
        }

        DonorRestoreOutcome outcome;
        try
        {
            outcome = await RestoreCoreAsync(jobId, name, zone, donor).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            outcome = new DonorRestoreOutcome(jobId, DonorRestoreState.Failed, ErrorClassOf(ex));
        }

        try
        {
            settings.OnDonorRestore?.Invoke(outcome);
        }
        catch (Exception)
        {
            // A listener that throws must not matter to the clean-up.
        }

        return outcome;
    }

    private async Task<DonorRestoreOutcome> RestoreCoreAsync(string jobId, string name, string zone, Donor donor)
    {
        var clockStart = settings.StartClock();
        while (true)
        {
            var vm = await calls.CallAsync(token => compute.GetVmAsync(donor.Scope, name, zone, token), CancellationToken.None).ConfigureAwait(false);
            if (vm is null)
            {
                return new DonorRestoreOutcome(jobId, DonorRestoreState.Gone);
            }

            if (VmFacts.IsStopped(vm))
            {
                break;
            }

            if (settings.ElapsedSince(clockStart) >= settings.LifecycleTimeout)
            {
                return new DonorRestoreOutcome(jobId, DonorRestoreState.NotStopped);
            }

            await settings.DelayAsync(settings.LifecyclePollInterval, CancellationToken.None).ConfigureAwait(false);
        }

        var state = await calls.CallAsync(token => compute.RestoreVmLabelsAsync(donor.Scope, name, zone, jobId, donor.Labels, token), CancellationToken.None).ConfigureAwait(false);
        return new DonorRestoreOutcome(jobId, state switch
        {
            LabelRestoreState.Restored => DonorRestoreState.Restored,
            LabelRestoreState.NewerOwner => DonorRestoreState.NewerOwner,
            _ => DonorRestoreState.Gone,
        });
    }

    /// <summary>Sends a stop; a 404 is "already gone" (the wanted end state).</summary>
    private Task<bool> StopQuietlyAsync(CloudScope scope, string name, string zone, CancellationToken cancellationToken)
        => calls.CallAsync(
            async token =>
            {
                try
                {
                    await compute.StopVmAsync(scope, name, zone, token).ConfigureAwait(false);
                }
                catch (CloudOperationException ex) when (ex.Error.HttpStatus == 404)
                {
                    // Already gone (404): the wanted end state, nothing to report.
                }

                return true;
            },
            cancellationToken);

    /// <summary>
    /// Deletes every VM this job's label finds (a 404 means it is already gone: the worker or the platform got there first),
    /// then looks again and waits up to <see cref="CloudRunSettings.LifecycleTimeout"/> for none to remain. Returns null when the VMs are
    /// confirmed gone, else why not.
    /// </summary>
    /// <summary>
    /// Issue #92: deletes each queued (flex-start) request by its own name and zone and waits, up to <see cref="CloudRunSettings.LifecycleTimeout"/>, for it to read gone.
    /// Defence in depth beside the label listing: THEORY (unverified, no live project) a pending request is a listable instance; if it is not, only this finds it.
    /// Null when every one is gone (or none was given), else why not. A request already gone (404) is the state wanted.
    /// </summary>
    public async Task<EndFailure?> DeleteQueuedRequestsAsync(CloudScope scope, IReadOnlyList<(string Name, string Zone)> requests, CancellationToken cancellationToken)
    {
        EndFailure? error = null;
        foreach (var (name, zone) in requests)
        {
            try
            {
                await calls.CallAsync(
                    async token =>
                    {
                        try
                        {
                            await compute.DeleteVmAsync(scope, name, zone, token).ConfigureAwait(false);
                        }
                        catch (CloudOperationException ex) when (ex.Error.HttpStatus == 404)
                        {
                            // Already gone.
                        }

                        return true;
                    },
                    cancellationToken).ConfigureAwait(false);

                var clockStart = settings.StartClock();
                while (await calls.CallAsync(token => compute.GetVmAsync(scope, name, zone, token), cancellationToken).ConfigureAwait(false) is not null)
                {
                    if (settings.ElapsedSince(clockStart) >= settings.LifecycleTimeout)
                    {
                        error ??= EndFailure.Other($"The waiting request '{name}' in zone '{zone}' was still there after Delete.");
                        break;
                    }

                    await settings.DelayAsync(settings.LifecyclePollInterval, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (!(ex is OperationCanceledException && cancellationToken.IsCancellationRequested))
            {
                error ??= EndFailure.Of($"The waiting request '{name}' in zone '{zone}' could not be deleted", ErrorClassOf(ex), "Delete it from the Cloud page.");
            }
        }

        return error;
    }

    internal static string ErrorClassOf(Exception ex)
        => ex is CloudOperationException cloud ? RunErrorCodes.For(cloud.Kind)
            : ex is AccountAuthException { Code: AuthErrorCodes.SigninExpired } ? RunErrorCodes.AccountSignedOut // issue #830 review M6: the run's account is signed out, not "other"
            : "other"; // the shared roster (api_disabled, org_policy), never Kind.ToString()

    /// <summary>The name a VM THIS run created carries (<see cref="VmSpec.VmName"/> without an existing VM). A VM reused from another run keeps that run's name, so a different name means "borrowed".</summary>
    internal static string OwnVmName(string jobId) => $"deg-{jobId}";

    /// <summary>
    /// Whether a VM read by name is this create's: by its job-id label AND this installation's installation-id label (Hard Rule 9: another installation's VM that happens to carry the
    /// same job id is not ours to delete), or by the job's own name when the gateway read no labels.
    /// </summary>
    private static bool IsOurs(SentCreate sent, VmDescriptor vm)
        => vm.Labels is null
            ? vm.Name == OwnVmName(sent.JobId)
            : vm.Labels.TryGetValue("job-id", out var owner) && owner == sent.JobId
              && vm.Labels.TryGetValue("installation-id", out var installation) && installation == sent.InstallationId;

    private enum NameLook
    {
        /// <summary>No VM under the name (or one that is not this create's).</summary>
        Gone,

        /// <summary>This create's VM is there.</summary>
        Present,

        /// <summary>The look failed: the VM may be there. Treated as present, never as gone.</summary>
        Unknown,
    }

    /// <summary>
    /// One look at the VM a create asked for, BY NAME, never a remembered descriptor. A failing look is <see cref="NameLook.Unknown"/>, not a reason to skip anything. A look that finds nothing
    /// AFTER the create had answered settles the create (<see cref="SentCreate.Settled"/>): its VM is gone. One that started before the answer settles nothing.
    /// </summary>
    private async Task<(NameLook State, string? ErrorClass)> LookByNameAsync(SentCreate sent, CancellationToken cancellationToken)
    {
        var answeredBefore = sent.Answered;
        try
        {
            var vm = await calls.CallAsync(token => compute.GetVmAsync(sent.Scope, sent.Name, sent.Zone, token), cancellationToken).ConfigureAwait(false);
            if (vm is not null && IsOurs(sent, vm))
            {
                return (NameLook.Present, null);
            }

            if (answeredBefore)
            {
                sent.Settled = true;
            }

            return (NameLook.Gone, null);
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && cancellationToken.IsCancellationRequested))
        {
            return (NameLook.Unknown, ErrorClassOf(ex));
        }
    }

    private enum DeleteResult
    {
        Accepted,
        AlreadyGone,
        Failed,
    }

    /// <summary>Deletes by name; a 404 is the state wanted ("already gone"). Never throws for a cloud failure.</summary>
    private async Task<(DeleteResult Result, string? ErrorClass)> DeleteByNameAsync(CloudScope scope, string name, string zone, CancellationToken cancellationToken)
    {
        try
        {
            var gone = await calls.CallAsync(
                async token =>
                {
                    try
                    {
                        await compute.DeleteVmAsync(scope, name, zone, token).ConfigureAwait(false);
                        return false;
                    }
                    catch (CloudOperationException ex) when (ex.Error.HttpStatus == 404)
                    {
                        return true;
                    }
                },
                cancellationToken).ConfigureAwait(false);
            return (gone ? DeleteResult.AlreadyGone : DeleteResult.Accepted, null);
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && cancellationToken.IsCancellationRequested))
        {
            return (DeleteResult.Failed, ErrorClassOf(ex));
        }
    }

    private enum ReapStep
    {
        Gone,
        DeleteSent,
        DeleteFailed,
        Superseded,
        Deferred,
    }

    /// <summary>What one reap step came to. <see cref="LookError"/> is the class of a failed look (the VM may be there), else null.</summary>
    private readonly record struct ReapResult(ReapStep Step, string? ErrorClass, string? LookError = null);

    /// <summary>
    /// Issue #938: one look by name at a create's VM and, if it may be there, one delete by name. A failed look is "maybe present": the delete goes anyway (a 404 means it was never there). A VM a look SEES
    /// is deleted again on every step: an accepted delete says nothing about whether the VM existed (the real gateway answers a delete of a missing VM with a silent success, round 4 H1), so the VM
    /// being seen, never a delete having been accepted, is what proves one. <paramref name="lookOnly"/> sends nothing (the watch's last look before it judges).
    /// The delete is decided under the ledger's lock (<see cref="AttemptLedger.TryBeginDelete"/>): a VM a LATER attempt of the job owns is left alone, and so is one a later attempt that is
    /// still provisioning may be about to adopt (round 3 M2 and M3).
    /// </summary>
    private async Task<ReapResult> ReapStepAsync(SentCreate sent, bool lookOnly = false)
    {
        var (state, lookError) = await LookByNameAsync(sent, CancellationToken.None).ConfigureAwait(false);
        if (state == NameLook.Gone)
        {
            return new ReapResult(ReapStep.Gone, null);
        }

        switch (Ledger.TryBeginDelete(sent))
        {
            case DeleteDecision.Superseded:
                return new ReapResult(ReapStep.Superseded, null);
            case DeleteDecision.Defer:
                return new ReapResult(ReapStep.Deferred, null);
        }

        var unknown = state == NameLook.Unknown;
        if (!unknown)
        {
            sent.Landed = true;
        }

        if (lookOnly)
        {
            return unknown ? new ReapResult(ReapStep.DeleteFailed, lookError, lookError) : new ReapResult(ReapStep.DeleteSent, null);
        }

        var (result, deleteError) = await DeleteByNameAsync(sent.Scope, sent.Name, sent.Zone, CancellationToken.None).ConfigureAwait(false);
        return result switch
        {

            // A 404 after a look that could not read says the VM is not there NOW, not that the create will never make one: while the looks fail the watch keeps going.
            DeleteResult.AlreadyGone => unknown ? new ReapResult(ReapStep.DeleteSent, null, lookError) : new ReapResult(ReapStep.Gone, null),
            DeleteResult.Accepted => new ReapResult(ReapStep.DeleteSent, null, lookError),
            _ => new ReapResult(ReapStep.DeleteFailed, deleteError ?? lookError, lookError),
        };
    }

    /// <summary>One create under watch: the state the loop keeps for it between rounds.</summary>
    private sealed class LateWatch(SentCreate sent)
    {
        public SentCreate Sent { get; } = sent;

        public long? EndedAt { get; set; }

        /// <summary>When the watch began waiting on a delete it knows is needed: a look SAW the VM, or a delete failed. A delete that was merely accepted starts nothing: it may have gone to a VM that did not exist.</summary>
        public long? DeleteStart { get; set; }

        /// <summary>A reuse: when the watch first asked for the VM to be stopped.</summary>
        public long? StopStart { get; set; }

        public string? LastError { get; set; }
    }

    private int _reapLoops;

    /// <summary>Tests: how many late-watch loops have run (one per cancel that had a create of unknown outcome, however many creates it held).</summary>
    internal int ReapLoopsStarted => Volatile.Read(ref _reapLoops);

    /// <summary>
    /// Issue #938: the watch over the creates a cancel could not settle (in flight, or ended Canceled or by a network failure: Compute may still have accepted them). ONE loop per cancel, however
    /// many creates: each round looks for each outstanding VM BY NAME, whatever became of its task, and deletes it when it appears, until <see cref="CloudRunSettings.LateCreateReapWindow"/> runs out
    /// (a create still in flight) or <see cref="CloudRunSettings.LateCreateGrace"/> after the task ended. "No VM" is said only after those looks found nothing, and "Deleted" only for a VM a look saw. A delete that errors is
    /// judged by the next look, bounded by <see cref="CloudRunSettings.LifecycleTimeout"/>; the VM's own maxRunDuration (Hard Rule 10) and the reconciler on the next launch are the backstops. A reuse among them
    /// (round 4 M3) is STOPPED when it lands, never deleted. Returns one outcome per create, in order. Never throws.
    /// </summary>
    public async Task<IReadOnlyList<LateCreateOutcome>> ReapLateCreatesAsync(IReadOnlyList<SentCreate> creates)
    {
        Interlocked.Increment(ref _reapLoops);
        var outcomes = new Dictionary<SentCreate, LateCreateOutcome>();
        try
        {
            var windowStart = settings.StartClock();
            var watches = creates.Select(c => new LateWatch(c)).ToList();
            while (watches.Count > 0)
            {
                foreach (var watch in watches.ToList())
                {
                    LateCreateOutcome outcome;
                    try
                    {
                        if (await AdvanceAsync(watch, windowStart).ConfigureAwait(false) is not { } done)
                        {
                            continue;
                        }

                        outcome = done;
                    }
                    catch (Exception ex)
                    {
                        outcome = new LateCreateOutcome(watch.Sent.JobId, LateCreateState.DeleteFailed, ErrorClassOf(ex));
                    }

                    outcomes[watch.Sent] = outcome;
                    watches.Remove(watch);
                    Ledger.EndDelete(watch.Sent);
                }

                if (watches.Count > 0)
                {
                    await settings.DelayAsync(settings.LifecyclePollInterval, CancellationToken.None).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex)
        {
            foreach (var create in creates.Where(c => !outcomes.ContainsKey(c)))
            {
                outcomes[create] = new LateCreateOutcome(create.JobId, LateCreateState.DeleteFailed, ErrorClassOf(ex));
            }
        }
        finally
        {
            foreach (var create in creates)
            {
                Ledger.EndDelete(create);
            }
        }

        return creates.Select(c => outcomes[c]).ToList();
    }

    /// <summary>The outcome when a look found no VM, or null while the watch should keep looking. "Deleted" needs a look that SAW the VM (<see cref="SentCreate.Landed"/>).</summary>
    private LateCreateOutcome? NoVmYet(LateWatch watch, long windowStart)
    {
        var sent = watch.Sent;
        if (sent.Landed)
        {
            // The VM this create made was seen, and is gone now: by this delete, or by another one that got there between the look and the delete (a worker, the platform).
            return new LateCreateOutcome(sent.JobId, LateCreateState.Deleted);
        }

        if (sent.Refused)
        {
            // Compute refused it: no VM of this create exists.
            return new LateCreateOutcome(sent.JobId, LateCreateState.CreateFailed);
        }

        if (sent.Settled)
        {
            // It answered a VM and a look after the answer found none: the VM is gone (it deleted itself).
            return new LateCreateOutcome(sent.JobId, LateCreateState.NothingToDelete);
        }

        var windowOver = settings.ElapsedSince(windowStart) >= settings.LateCreateReapWindow;
        if (sent.Create is { IsCompleted: true })
        {
            watch.EndedAt ??= settings.StartClock();
            if (settings.ElapsedSince(watch.EndedAt.Value) >= settings.LateCreateGrace || windowOver)
            {
                // A by-name look after the create ended (and the grace for Compute to show an accepted insert) found nothing.
                return new LateCreateOutcome(sent.JobId, LateCreateState.CreateFailed);
            }

            return null;
        }

        return windowOver ? new LateCreateOutcome(sent.JobId, LateCreateState.GaveUp) : null;
    }

    /// <summary>One round for one watched create: its outcome once it has one, else null.</summary>
    private async Task<LateCreateOutcome?> AdvanceAsync(LateWatch watch, long windowStart)
    {
        var sent = watch.Sent;
        if (sent.IsReuse)
        {
            return await AdvanceReuseAsync(watch, windowStart).ConfigureAwait(false);
        }

        var reap = await ReapStepAsync(sent).ConfigureAwait(false);
        switch (reap.Step)
        {
            case ReapStep.Superseded:
                return new LateCreateOutcome(sent.JobId, LateCreateState.Superseded);
            case ReapStep.Deferred:
                // A newer attempt is still provisioning and may be about to adopt this VM: nothing is deleted yet, and the window still bounds the wait.
                return settings.ElapsedSince(windowStart) >= settings.LateCreateReapWindow ? new LateCreateOutcome(sent.JobId, LateCreateState.GaveUp) : null;
            case ReapStep.DeleteSent:
                watch.LastError = null;
                if (sent.Landed)
                {
                    watch.DeleteStart ??= settings.StartClock();
                }

                break;
            case ReapStep.DeleteFailed:
                watch.LastError = reap.ErrorClass;
                watch.DeleteStart ??= settings.StartClock();
                break;
            default:
                if (NoVmYet(watch, windowStart) is { } none)
                {
                    return none;
                }

                break;
        }

        if (watch.DeleteStart is { } started && settings.ElapsedSince(started) >= settings.LifecycleTimeout)
        {
            // One last look: a delete that errored or lagged may still have worked.
            var final = await ReapStepAsync(sent, lookOnly: true).ConfigureAwait(false);
            switch (final.Step)
            {
                case ReapStep.Gone:
                    if (NoVmYet(watch, windowStart) is { } gone)
                    {
                        return gone;
                    }

                    break;
                case ReapStep.Superseded:
                    // A newer attempt OWNS the VM now: it is handed over, nothing failed.
                    return new LateCreateOutcome(sent.JobId, LateCreateState.Superseded);
                case ReapStep.Deferred:
                    // Round 6 L2: a newer attempt that is only PENDING has adopted nothing (it may yet fail elsewhere, leaving this VM billing): keep waiting for it, bounded by the reap window, as the main switch does.
                    return settings.ElapsedSince(windowStart) >= settings.LateCreateReapWindow ? new LateCreateOutcome(sent.JobId, LateCreateState.GaveUp) : null;
                default:
                    return sent.Landed
                        ? new LateCreateOutcome(sent.JobId, LateCreateState.DeleteFailed, watch.LastError ?? "other")

                        // No look ever saw a VM, and the looks and deletes kept failing: it cannot be said that one existed.
                        : new LateCreateOutcome(sent.JobId, LateCreateState.CouldNotConfirm, watch.LastError ?? final.ErrorClass ?? "other");
            }
        }

        if (!sent.Landed && reap.LookError is { } lookClass && settings.ElapsedSince(windowStart) >= settings.LateCreateReapWindow)
        {
            // Every look failed for the whole window: the deletes by name went out, but nobody saw a VM.
            return new LateCreateOutcome(sent.JobId, LateCreateState.CouldNotConfirm, watch.LastError ?? lookClass);
        }

        return null;
    }

    /// <summary>Stops a VM by name; a 404 is "already gone". Never throws for a cloud failure.</summary>
    private async Task<(bool Ok, string? ErrorClass)> StopByNameAsync(CloudScope scope, string name, string zone)
    {
        try
        {
            await calls.CallAsync(
                async token =>
                {
                    try
                    {
                        await compute.StopVmAsync(scope, name, zone, token).ConfigureAwait(false);
                    }
                    catch (CloudOperationException ex) when (ex.Error.HttpStatus == 404)
                    {
                        // Already gone: the state wanted.
                    }

                    return true;
                },
                CancellationToken.None).ConfigureAwait(false);
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ErrorClassOf(ex));
        }
    }

    /// <summary>
    /// Issue #970: a late reuse is judged stopped (seen running and stopped again, started and answered, or past the grace with the VM stopped): the VM goes back under its donor's labels, so the cancelled
    /// job's label no longer finds it and the donor's keep-alive applies again. What became of the labels rides on the outcome (class only).
    /// </summary>
    private async Task<LateCreateOutcome> ReuseStoppedAsync(SentCreate sent)
        => new(sent.JobId, LateCreateState.ReuseStopped, Restore: await RestoreDonorLabelsAsync(sent.JobId, sent.Name, sent.Zone).ConfigureAwait(false));

    /// <summary>
    /// Round 4 M3: one round for a REUSE the cancel could not settle. The VM is another run's (reused by relabelling it to this job and starting it): it is never deleted, only STOPPED when it lands running.
    /// A reuse relabels and THEN starts, so a stopped VM that already carries this job's labels says nothing until the start has answered.
    /// </summary>
    private async Task<LateCreateOutcome?> AdvanceReuseAsync(LateWatch watch, long windowStart)
    {
        var sent = watch.Sent;
        var windowOver = settings.ElapsedSince(windowStart) >= settings.LateCreateReapWindow;
        switch (Ledger.TryBeginDelete(sent))
        {
            case DeleteDecision.Superseded:
                return new LateCreateOutcome(sent.JobId, LateCreateState.Superseded);
            case DeleteDecision.Defer:
                return windowOver ? new LateCreateOutcome(sent.JobId, LateCreateState.GaveUp) : null;
        }

        VmDescriptor? vm;
        try
        {
            vm = await calls.CallAsync(token => compute.GetVmAsync(sent.Scope, sent.Name, sent.Zone, token), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            watch.LastError = ErrorClassOf(ex);
            var stopOver = watch.StopStart is { } s && settings.ElapsedSince(s) >= settings.LifecycleTimeout;
            return windowOver || stopOver
                ? new LateCreateOutcome(sent.JobId, sent.Landed ? LateCreateState.ReuseStopFailed : LateCreateState.CouldNotConfirm, watch.LastError)
                : null;
        }

        var mine = vm?.Labels is { } labels
            && labels.TryGetValue("job-id", out var job) && job == sent.JobId
            && labels.TryGetValue("installation-id", out var installation) && installation == sent.InstallationId;
        if (!mine)
        {
            if (sent.Landed)
            {
                return new LateCreateOutcome(sent.JobId, LateCreateState.NothingToDelete);
            }

            if (sent.Refused)
            {
                return new LateCreateOutcome(sent.JobId, LateCreateState.CreateFailed);
            }

            if (sent.Create is { IsCompleted: true })
            {
                watch.EndedAt ??= settings.StartClock();
                return settings.ElapsedSince(watch.EndedAt.Value) >= settings.LateCreateGrace || windowOver ? new LateCreateOutcome(sent.JobId, LateCreateState.CreateFailed) : null;
            }

            return windowOver ? new LateCreateOutcome(sent.JobId, LateCreateState.GaveUp) : null;
        }

        if (VmFacts.IsStopped(vm!) || vm!.Status == "STOPPING")
        {
            // Round 6 H1: a stopped donor that carries this job's labels says nothing until the start has been ANSWERED, and a task that ended Canceled (or by a lost answer) answered nothing: the real
            // gateway honours the token, so Compute may still run the start POST that was on the wire. Seen running and stopped by this watch, or a start that answered, there is nothing more to wait for;
            // otherwise the VM is judged only after the same grace a create gets, counted from the task's end.
            if (sent.Landed || sent.Answered)
            {
                return await ReuseStoppedAsync(sent).ConfigureAwait(false);
            }

            if (sent.Create is { IsCompleted: true })
            {
                watch.EndedAt ??= settings.StartClock();
                return settings.ElapsedSince(watch.EndedAt.Value) >= settings.LateCreateGrace || windowOver ? await ReuseStoppedAsync(sent).ConfigureAwait(false) : null;
            }

            return windowOver ? new LateCreateOutcome(sent.JobId, LateCreateState.GaveUp) : null;
        }

        sent.Landed = true; // seen running: the start landed
        var (ok, stopError) = await StopByNameAsync(sent.Scope, sent.Name, sent.Zone).ConfigureAwait(false);
        watch.StopStart ??= settings.StartClock();
        watch.LastError = ok ? null : stopError;
        return settings.ElapsedSince(watch.StopStart.Value) >= settings.LifecycleTimeout
            ? new LateCreateOutcome(sent.JobId, LateCreateState.ReuseStopFailed, watch.LastError ?? "other")
            : null;
    }

    /// <summary>
    /// Deletes every VM this job's label finds AND every VM a create of this job asked for, by name (the listing can lag, #938), then reads them back and waits up to
    /// <see cref="CloudRunSettings.LifecycleTimeout"/> for none to remain. A look that fails is "maybe present": it deletes by name (404 is gone) and never skips a delete the listing asked for.
    /// Only a VM this run CREATED (named <see cref="OwnVmName"/>) is deleted: one the run borrowed by reuse keeps its owner's name, is STOPPED here, and is never counted as left.
    /// Returns null when the VMs are confirmed gone, else why not.
    /// </summary>
    public async Task<EndFailure?> DeleteOwnedVmsAndVerifyAsync(CloudScope scope, string jobId, CreateSettleState settle, CancellationToken cancellationToken, IReadOnlyList<SentCreate>? sentCreates = null, Action<VmDescriptor>? foundVm = null)
    {
        var sent = sentCreates ?? [];

        // Round 4 M2: held for the whole delete and its read-back, so a new attempt of the job never adopts a VM this is deleting. (A cancel's retry, the listed-VM path and the run page's delete all come through here or take the hold themselves.)
        using var hold = Ledger.HoldDelete(jobId);
        try
        {
            return await DeleteOwnedCoreAsync(scope, jobId, settle, cancellationToken, sent, foundVm).ConfigureAwait(false);
        }
        finally
        {
            foreach (var create in sent)
            {
                Ledger.EndDelete(create);
            }
        }
    }

    private async Task<EndFailure?> DeleteOwnedCoreAsync(CloudScope scope, string jobId, CreateSettleState settle, CancellationToken cancellationToken, IReadOnlyList<SentCreate> allSent, Action<VmDescriptor>? foundVm)
    {
        // Round 4 M3: a REUSE names another run's VM: it is never a delete target nor counted as left (the watch stops it if it lands), so only creates go through the by-name delete and read-back below.
        var sent = allSent.Where(c => !c.IsReuse).ToList();
        var ownName = OwnVmName(jobId);
        IReadOnlyList<VmDescriptor> owned;
        EndFailure? listError = null;
        try
        {
            owned = await calls.CallAsync(token => compute.FindByJobIdAsync(scope, jobId, token), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (sent.Count > 0 && !(ex is OperationCanceledException && cancellationToken.IsCancellationRequested))
        {
            // The names this job asked for are still deleted; only the label sweep is missing, and the run says so. The class, never the message (it can carry a path).
            owned = [];
            listError = EndFailure.Of("The VMs for this job could not be listed", ErrorClassOf(ex), "Check the Cloud page and delete any VM left for this job.");
        }

        // Issue #109: each VM the delete finds is an observation of that VM (VmSpan), whether or not the create that made it was settled.
        foreach (var observed in owned)
        {
            foundVm?.Invoke(observed);
        }

        var targets = new Dictionary<(string Name, string Zone), VmDescriptor?>();
        foreach (var vm in owned)
        {
            targets[(vm.Name, vm.Zone)] = vm;
        }

        foreach (var create in sent)
        {
            targets.TryAdd((create.Name, create.Zone), null);
        }

        EndFailure? deleteError = null;
        foreach (var ((name, zone), listed) in targets)
        {
            // Every VM is attempted even when an earlier delete failed: the first failure is what is reported.
            if (listed is not null)
            {
                if (name != ownName)
                {
                    // Round 3 H1: a VM this run did not create (a reuse relabelled it to this job) is its owner's, not this cancel's to delete. Stop it so it does not bill, leave its labels.
                    var stopError = await StopBorrowedAsync(scope, listed, cancellationToken).ConfigureAwait(false);
                    deleteError ??= stopError;
                    foreach (var reuse in allSent.Where(c => c.IsReuse && c.Name == name && c.Zone == zone))
                    {
                        // Rounds 5 and 6 H1: a reuse relabels FIRST and starts LAST, so a stopped donor that already carries this job's labels says nothing until the start has ANSWERED. A task still running, ended
                        // Canceled or ended by a lost answer has not answered (the real gateway honours the token: Compute may still run a start POST that was on the wire), so the watch keeps it
                        // (it stops the VM if the start lands). Only a start that answered successfully has been seen, and the stop above ended it.
                        if (reuse.Answered)
                        {
                            reuse.Landed = true;
                        }
                    }

                    // Issue #970: the borrowed VM goes back under its donor's labels once it is stopped, but never while a reuse of it has not answered: that start may still land, and the late watch
                    // (which restores when it has seen the VM stopped again) must still find the VM by this job's label.
                    if (stopError is null && !ReuseUnanswered(jobId, name, zone) && !allSent.Any(c => c.IsReuse && c.Name == name && c.Zone == zone && !c.Answered && !c.Refused))
                    {
                        await RestoreAndNoteAsync(jobId, name, zone).ConfigureAwait(false);
                    }

                    continue;
                }

                foreach (var create in sent.Where(c => c.Name == name && c.Zone == zone))
                {
                    create.Landed = true;
                }

                var (result, error) = await DeleteByNameAsync(scope, name, zone, cancellationToken).ConfigureAwait(false);
                if (result == DeleteResult.Failed)
                {
                    deleteError ??= EndFailure.Of($"VM '{name}' in zone '{zone}' could not be deleted", error ?? RunErrorCodes.Other, "Delete it from the Cloud page.");
                }

                continue;
            }

            var create0 = sent.First(c => c.Name == name && c.Zone == zone);
            var reap = await ReapStepAsync(create0).ConfigureAwait(false);
            if (reap.Step == ReapStep.DeleteFailed)
            {
                deleteError ??= create0.Landed
                    ? EndFailure.Of($"VM '{name}' in zone '{zone}' could not be deleted", reap.ErrorClass ?? RunErrorCodes.Other, "Delete it from the Cloud page.")
                    : EndFailure.Of($"The app could not confirm whether VM '{name}' in zone '{zone}' exists or delete it", reap.ErrorClass ?? RunErrorCodes.Other, "Check the Cloud page and delete it if it is there.");
            }
        }

        if (deleteError is not null)
        {
            return deleteError;
        }

        var clockStart = settings.StartClock();
        while (true)
        {
            HashSet<(string Name, string Zone)> stillThere;
            var listUnreadable = false;
            try
            {
                stillThere = (await calls.CallAsync(token => compute.FindByJobIdAsync(scope, jobId, token), cancellationToken).ConfigureAwait(false))
                    .Where(v => v.Name == ownName)
                    .Select(v => (v.Name, v.Zone))
                    .ToHashSet();
            }
            catch (Exception ex) when (!(ex is OperationCanceledException && cancellationToken.IsCancellationRequested))
            {
                // Round 6 L3: a listing that fails is an unreadable look, never "none" and never an unexpected failure: keep looping, bounded by LifecycleTimeout below.
                stillThere = [];
                listUnreadable = true;
            }

            var confirmed = stillThere.ToHashSet(); // READ present by the listing or by name below; an unreadable look is not a VM that is there
            var unreadable = false;
            foreach (var create in sent)
            {
                // Read back by name too: a lagging listing says "none" for a VM that is still there. A VM seen both ways is counted once.
                var (state, _) = await LookByNameAsync(create, cancellationToken).ConfigureAwait(false);
                if (state == NameLook.Present)
                {
                    stillThere.Add((create.Name, create.Zone));
                    confirmed.Add((create.Name, create.Zone));
                }
                else if (state == NameLook.Unknown)
                {
                    // Round 5 M1: unreadable is not gone, and a delete proves nothing either way (the real gateway answers a delete of a missing VM with a silent success, so it never says "already gone").
                    // Gone is decided only by a look that READ; until one does, the loop keeps looking (bounded by LifecycleTimeout below). The delete was already sent above.
                    stillThere.Add((create.Name, create.Zone));
                    unreadable = true;
                }
            }

            unreadable |= listUnreadable;
            if (stillThere.Count == 0 && !unreadable)
            {
                break;
            }

            if (settings.ElapsedSince(clockStart) >= settings.LifecycleTimeout)
            {
                if (confirmed.Count == 0)
                {
                    // Round 7 L1, round 8 L1: nothing READ present on the last look (never seen, or seen once and since read gone or unreadable). Say that, not "still there".
                    return EndFailure.Other("The app could not check whether the VMs for this job are gone. Check the Cloud page and delete any VM left for this job.");
                }

                return EndFailure.Other($"{confirmed.Count} VM(s) for this job were still there {settings.LifecycleTimeout.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture)} s after Delete. Delete them from the Cloud page.");
            }

            await settings.DelayAsync(settings.LifecyclePollInterval, cancellationToken).ConfigureAwait(false);
        }

        var seconds = settings.CreateSettleTimeout.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture);
        return listError ?? settle switch
        {
            CreateSettleState.CreateInFlight => EndFailure.Other($"A create request was still in flight after {seconds} s, so a VM may still appear. The app deletes it if it does; check the Cloud page to be sure."),
            CreateSettleState.ReuseInFlight => EndFailure.Other($"A request to start an earlier VM was still in flight after {seconds} s, so that VM may start. The app stops it again if it does; check the Cloud page to be sure."),
            _ => null,
        };
    }

    /// <summary>Stops a VM this run borrowed (it keeps its owner's name). Null when it is stopped (or stopping, or gone), else why not, with the action.</summary>
    private async Task<EndFailure?> StopBorrowedAsync(CloudScope scope, VmDescriptor vm, CancellationToken cancellationToken)
    {
        if (VmFacts.IsStopped(vm) || vm.Status == "STOPPING")
        {
            return null;
        }

        try
        {
            await calls.CallAsync(
                async token =>
                {
                    try
                    {
                        await compute.StopVmAsync(scope, vm.Name, vm.Zone, token).ConfigureAwait(false);
                    }
                    catch (CloudOperationException ex) when (ex.Error.HttpStatus == 404)
                    {
                        // Already gone: the state wanted.
                    }

                    return true;
                },
                cancellationToken).ConfigureAwait(false);
            return null;
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && cancellationToken.IsCancellationRequested))
        {
            return EndFailure.Of($"VM '{vm.Name}' in zone '{vm.Zone}' was started for this run by an earlier job and could not be stopped", ErrorClassOf(ex), "Stop it from the Cloud page.");
        }
    }

    /// <summary>
    /// The run page's "Stop VM now": stops every VM this job id owns, found
    /// by label lookup rather than a remembered zone (Hard Rule 9). A job
    /// with no VM is a no-op. The run's own phase is not changed here: the
    /// next health check in <see cref="CloudJobRunner.RunAsync"/> sees the stopped VM and
    /// records the failure, which keeps one writer per phase transition.
    /// </summary>
    public async Task StopVmsAsync(CloudScope scope, string jobId, CancellationToken cancellationToken)
    {
        NoteAppRequestedEnd(jobId);
        var owned = await calls.CallAsync(token => compute.FindByJobIdAsync(scope, jobId, token), cancellationToken).ConfigureAwait(false);
        foreach (var vm in owned)
        {
            await calls.CallAsync(
                async token =>
                {
                    await compute.StopVmAsync(scope, vm.Name, vm.Zone, token).ConfigureAwait(false);
                    return true;
                },
                cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>The run page's "Delete VM now"; same lookup and no-op rules as <see cref="StopVmsAsync"/>.</summary>
    public async Task DeleteVmsAsync(CloudScope scope, string jobId, CancellationToken cancellationToken)
    {
        NoteAppRequestedEnd(jobId);
        using var hold = Ledger.HoldDelete(jobId); // round 4 M2
        var owned = await calls.CallAsync(token => compute.FindByJobIdAsync(scope, jobId, token), cancellationToken).ConfigureAwait(false);
        foreach (var vm in owned)
        {
            await calls.CallAsync(
                async token =>
                {
                    await compute.DeleteVmAsync(scope, vm.Name, vm.Zone, token).ConfigureAwait(false);
                    return true;
                },
                cancellationToken).ConfigureAwait(false);
        }
    }
}

/// <summary>What the cancel's wait for the creates it started came to: all landed or failed, or a request is still on the wire (and of which kind, because the app deletes a late create but never stops a late reuse).</summary>
internal enum CreateSettleState
{
    Settled,
    CreateInFlight,
    ReuseInFlight,
}
