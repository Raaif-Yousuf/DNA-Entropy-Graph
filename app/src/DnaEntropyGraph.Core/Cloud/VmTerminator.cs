using System.Globalization;

namespace DnaEntropyGraph.Core.Cloud;

/// <summary>
/// Hard Rule 11: ends a run's VM (stop or delete), verifies it independently, and finds VMs by job-id label (Hard Rule 9).
/// Holds no run state: every method is a look at the gateway plus a bounded wait.
/// </summary>
internal sealed class VmTerminator(IComputeGateway compute, GatewayCalls calls, CloudRunSettings settings)
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _appRequestedEnds = new(StringComparer.Ordinal);

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
        // forceDelete wins; forceStop (the user chose Stop at a stall, #675) overrides the run's own end choice.
        var action = forceDelete ? AfterTaskAction.Delete : forceStop ? AfterTaskAction.Stop : FailureEndAction(request);
        var error = zone is not null
            ? await EnsureVmEndedAsync(request, zone, action, CancellationToken.None).ConfigureAwait(false)
            : await EnsureVmsEndedByLabelAsync(request, action).ConfigureAwait(false);
        return error is null ? null : " (VM end not confirmed: " + error + ")";
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
    private async Task<string?> EnsureVmsEndedByLabelAsync(CloudJobRequest request, AfterTaskAction action)
    {
        IReadOnlyList<VmDescriptor> owned;
        try
        {
            owned = await calls.CallAsync(token => compute.FindByJobIdAsync(request.JobId, token), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Never throws (the callers are already handling a failure): anything that is not the caller cancelling is
            // "could not look", and the run says the VM end is unconfirmed.
            return $"the VMs for this job could not be looked up: {ex.Message}";
        }

        string? error = null;
        foreach (var vm in owned)
        {
            // Every VM is ended even when an earlier one could not be: the first failure is what is reported.
            var thisError = await EnsureVmEndedAsync(request, vm.Zone, action, CancellationToken.None, vm.Name).ConfigureAwait(false);
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
    public async Task<string?> EnsureVmEndedAsync(CloudJobRequest request, string? zone, AfterTaskAction action, CancellationToken cancellationToken, string? vmName = null)
    {
        if (zone is null)
        {
            return null;
        }

        // A VM found by label is ended under its own name and zone, never the spec's.
        var name = vmName ?? request.Spec.VmName;
        try
        {
            var vm = await calls.CallAsync(token => compute.GetVmAsync(name, zone, token), cancellationToken).ConfigureAwait(false);
            if (vm is null)
            {
                return null;
            }

            if (action != AfterTaskAction.Delete && VmFacts.IsBooting(vm))
            {
                // THEORY (unverified): instances.stop on an instance that is not RUNNING is rejected, and a VM that never
                // reached RUNNING has no disk worth keeping: a failure that lands while it boots deletes it, as the boot
                // deadline does, whatever the user chose for after a run.
                action = AfterTaskAction.Delete;
            }

            if (action == AfterTaskAction.Delete)
            {
                await calls.CallAsync(
                    async token =>
                    {
                        try
                        {
                            await compute.DeleteVmAsync(name, zone, token).ConfigureAwait(false);
                        }
                        catch (CloudOperationException ex) when (ex.Error.HttpStatus == 404)
                        {
                            // Already deleted, by the worker or by the platform: the state we wanted.
                        }

                        return true;
                    },
                    cancellationToken).ConfigureAwait(false);
            }
            else if (!VmFacts.IsStopped(vm) && vm.Status != "STOPPING")
            {
                await calls.CallAsync(
                    async token =>
                    {
                        try
                        {
                            await compute.StopVmAsync(name, zone, token).ConfigureAwait(false);
                        }
                        catch (CloudOperationException ex) when (ex.Error.HttpStatus == 404)
                        {
                            // Already gone (404): the wanted end state, nothing to report.
                        }

                        return true;
                    },
                    cancellationToken).ConfigureAwait(false);
            }

            var clockStart = settings.StartClock();
            while (true)
            {
                vm = await calls.CallAsync(token => compute.GetVmAsync(name, zone, token), cancellationToken).ConfigureAwait(false);
                if (vm is null || (action != AfterTaskAction.Delete && VmFacts.IsStopped(vm)))
                {
                    return null;
                }

                if (settings.ElapsedSince(clockStart) >= settings.LifecycleTimeout)
                {
                    return action == AfterTaskAction.Delete
                        ? $"VM '{name}' in zone '{zone}' was still '{vm.Status}' {settings.LifecycleTimeout.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture)} s after Delete."
                        : $"VM '{name}' in zone '{zone}' was still '{vm.Status}' {settings.LifecycleTimeout.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture)} s after Stop (Hard Rule 11).";
                }

                await settings.DelayAsync(settings.LifecyclePollInterval, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && cancellationToken.IsCancellationRequested))
        {
            return $"VM '{name}' in zone '{zone}' could not be confirmed {(action == AfterTaskAction.Delete ? "deleted" : "stopped")}: {ex.Message}";
        }
    }

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
    public async Task<string?> DeleteQueuedRequestsAsync(IReadOnlyList<(string Name, string Zone)> requests, CancellationToken cancellationToken)
    {
        string? error = null;
        foreach (var (name, zone) in requests)
        {
            try
            {
                await calls.CallAsync(
                    async token =>
                    {
                        try
                        {
                            await compute.DeleteVmAsync(name, zone, token).ConfigureAwait(false);
                        }
                        catch (CloudOperationException ex) when (ex.Error.HttpStatus == 404)
                        {
                            // Already gone.
                        }

                        return true;
                    },
                    cancellationToken).ConfigureAwait(false);

                var clockStart = settings.StartClock();
                while (await calls.CallAsync(token => compute.GetVmAsync(name, zone, token), cancellationToken).ConfigureAwait(false) is not null)
                {
                    if (settings.ElapsedSince(clockStart) >= settings.LifecycleTimeout)
                    {
                        error ??= $"The waiting request '{name}' in zone '{zone}' was still there after Delete.";
                        break;
                    }

                    await settings.DelayAsync(settings.LifecyclePollInterval, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (!(ex is OperationCanceledException && cancellationToken.IsCancellationRequested))
            {
                error ??= $"The waiting request '{name}' in zone '{zone}' could not be deleted: {ex.Message}";
            }
        }

        return error;
    }

    private static string ErrorClassOf(Exception ex)
        => (ex as CloudOperationException)?.Kind.ToString().ToLowerInvariant() ?? "other";

    /// <summary>Whether a VM read by name is this job's: by its job-id label, or by the job's own name when the gateway read no labels.</summary>
    private static bool IsJobs(string jobId, VmDescriptor vm)
        => vm.Labels is null ? vm.Name == $"deg-{jobId}" : vm.Labels.TryGetValue("job-id", out var owner) && owner == jobId;

    private enum NameLook
    {
        /// <summary>No VM under the name (or one that is not this job's).</summary>
        Gone,

        /// <summary>This job's VM is there.</summary>
        Present,

        /// <summary>The look failed: the VM may be there. Treated as present, never as gone.</summary>
        Unknown,
    }

    /// <summary>One look at the VM a create asked for, BY NAME, never a remembered descriptor. A failing look is <see cref="NameLook.Unknown"/>, not a reason to skip anything.</summary>
    private async Task<(NameLook State, string? ErrorClass)> LookByNameAsync(SentCreate sent, CancellationToken cancellationToken)
    {
        try
        {
            var vm = await calls.CallAsync(token => compute.GetVmAsync(sent.Name, sent.Zone, token), cancellationToken).ConfigureAwait(false);
            return vm is not null && IsJobs(sent.JobId, vm) ? (NameLook.Present, null) : (NameLook.Gone, null);
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
    private async Task<(DeleteResult Result, string? ErrorClass)> DeleteByNameAsync(string name, string zone, CancellationToken cancellationToken)
    {
        try
        {
            var gone = await calls.CallAsync(
                async token =>
                {
                    try
                    {
                        await compute.DeleteVmAsync(name, zone, token).ConfigureAwait(false);
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
    }

    /// <summary>
    /// Issue #938: one look by name at a create's VM and, if it may be there, one delete by name. A failed look is "maybe present": the delete goes anyway (a 404 means it was never there).
    /// A VM a LATER attempt of the job owns (<see cref="SentCreate.IsSuperseded"/>) is left alone, checked just before the delete as well as at the look.
    /// </summary>
    private async Task<(ReapStep Step, string? ErrorClass)> ReapStepAsync(SentCreate sent, bool sendDelete)
    {
        var (state, lookError) = await LookByNameAsync(sent, CancellationToken.None).ConfigureAwait(false);
        if (state == NameLook.Gone)
        {
            return (ReapStep.Gone, null);
        }

        if (sent.IsSuperseded)
        {
            return (ReapStep.Superseded, null);
        }

        if (state == NameLook.Present)
        {
            sent.Landed = true;
        }

        if (!sendDelete && state == NameLook.Present)
        {
            return (ReapStep.DeleteSent, null);
        }

        var (result, deleteError) = await DeleteByNameAsync(sent.Name, sent.Zone, CancellationToken.None).ConfigureAwait(false);
        return result switch
        {
            DeleteResult.AlreadyGone => (ReapStep.Gone, null),
            DeleteResult.Accepted => (ReapStep.DeleteSent, null),
            _ => (ReapStep.DeleteFailed, deleteError ?? lookError),
        };
    }

    /// <summary>
    /// Issue #938: the watch over a create whose outcome the cancel could not settle (it was in flight, or it ended Canceled or by a network failure: Compute may still have accepted it). Looks for the VM
    /// BY NAME, whatever became of the task, and deletes it when it appears, until <see cref="CloudRunSettings.LateCreateReapWindow"/> runs out (a create still in flight) or
    /// <see cref="CloudRunSettings.LateCreateGrace"/> after the task ended. "No VM" is said only after those looks found nothing. A delete that errors is judged by the next look, bounded by
    /// <see cref="CloudRunSettings.LifecycleTimeout"/>; the VM's own maxRunDuration (Hard Rule 10) and the reconciler on the next launch are the backstops. Never throws.
    /// </summary>
    public async Task<LateCreateOutcome> ReapLateCreateAsync(SentCreate sent)
    {
        try
        {
            var windowStart = settings.StartClock();
            long? endedAt = null;
            long? deleteStart = null;
            var deleteAccepted = false;
            string? lastError = null;
            while (true)
            {
                var (step, error) = await ReapStepAsync(sent, sendDelete: !deleteAccepted).ConfigureAwait(false);
                switch (step)
                {
                    case ReapStep.Superseded:
                        return new LateCreateOutcome(sent.JobId, LateCreateState.Superseded);
                    case ReapStep.DeleteSent:
                        deleteAccepted = true;
                        lastError = null;
                        deleteStart ??= settings.StartClock();
                        break;
                    case ReapStep.DeleteFailed:
                        lastError = error;
                        deleteStart ??= settings.StartClock();
                        break;
                    default:
                        if (deleteStart is not null || sent.Landed)
                        {
                            // The VM this create made was seen, and is gone now: by this delete, or by another one that got there between the look and the delete (a worker, the platform).
                            return new LateCreateOutcome(sent.JobId, LateCreateState.Deleted);
                        }

                        if (sent.Create is { IsCompleted: true } task)
                        {
                            if (sent.Definitive)
                            {
                                // It answered a VM that is gone now, or Compute refused it: either way no VM of this create is left.
                                return new LateCreateOutcome(sent.JobId, task.IsCompletedSuccessfully ? LateCreateState.NothingToDelete : LateCreateState.CreateFailed);
                            }

                            endedAt ??= settings.StartClock();
                            if (settings.ElapsedSince(endedAt.Value) >= settings.LateCreateGrace || settings.ElapsedSince(windowStart) >= settings.LateCreateReapWindow)
                            {
                                // A by-name look after the create ended (and the grace for Compute to show an accepted insert) found nothing.
                                return new LateCreateOutcome(sent.JobId, LateCreateState.CreateFailed);
                            }
                        }
                        else if (settings.ElapsedSince(windowStart) >= settings.LateCreateReapWindow)
                        {
                            return new LateCreateOutcome(sent.JobId, LateCreateState.GaveUp);
                        }

                        break;
                }

                if (deleteStart is { } started && settings.ElapsedSince(started) >= settings.LifecycleTimeout)
                {
                    // One last look: a delete that errored or lagged may still have worked.
                    var (finalStep, _) = await ReapStepAsync(sent, sendDelete: false).ConfigureAwait(false);
                    return finalStep == ReapStep.Gone
                        ? new LateCreateOutcome(sent.JobId, LateCreateState.Deleted)
                        : finalStep == ReapStep.Superseded
                            ? new LateCreateOutcome(sent.JobId, LateCreateState.Superseded)
                            : new LateCreateOutcome(sent.JobId, LateCreateState.DeleteFailed, lastError ?? "other");
                }

                await settings.DelayAsync(settings.LifecyclePollInterval, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            return new LateCreateOutcome(sent.JobId, LateCreateState.DeleteFailed, ErrorClassOf(ex));
        }
    }

    /// <summary>
    /// Deletes every VM this job's label finds AND every VM a create of this job asked for, by name (the listing can lag, #938), then reads them back and waits up to
    /// <see cref="CloudRunSettings.LifecycleTimeout"/> for none to remain. A look that fails is "maybe present": it deletes by name (404 is gone) and never skips a delete the listing asked for.
    /// Returns null when the VMs are confirmed gone, else why not.
    /// </summary>
    public async Task<string?> DeleteOwnedVmsAndVerifyAsync(string jobId, CreateSettleState settle, CancellationToken cancellationToken, IReadOnlyList<SentCreate>? sentCreates = null)
    {
        var sent = sentCreates ?? [];
        IReadOnlyList<VmDescriptor> owned;
        string? listError = null;
        try
        {
            owned = await calls.CallAsync(token => compute.FindByJobIdAsync(jobId, token), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (sent.Count > 0 && !(ex is OperationCanceledException && cancellationToken.IsCancellationRequested))
        {
            // The names this job asked for are still deleted; only the label sweep is missing, and the run says so.
            owned = [];
            listError = $"The VMs for this job could not be listed: {ex.Message}";
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

        string? deleteError = null;
        foreach (var ((name, zone), listed) in targets)
        {
            // Every VM is attempted even when an earlier delete failed: the first failure is what is reported.
            if (listed is not null)
            {
                foreach (var create in sent.Where(c => c.Name == name && c.Zone == zone))
                {
                    create.Landed = true;
                }

                var (result, error) = await DeleteByNameAsync(name, zone, cancellationToken).ConfigureAwait(false);
                if (result == DeleteResult.Failed)
                {
                    deleteError ??= $"VM '{name}' in zone '{zone}' could not be deleted: {error}";
                }

                continue;
            }

            var create0 = sent.First(c => c.Name == name && c.Zone == zone);
            var (step, stepError) = await ReapStepAsync(create0, sendDelete: true).ConfigureAwait(false);
            if (step == ReapStep.DeleteFailed)
            {
                deleteError ??= $"VM '{name}' in zone '{zone}' could not be deleted: {stepError}";
            }
        }

        if (deleteError is not null)
        {
            return deleteError;
        }

        var clockStart = settings.StartClock();
        while (true)
        {
            var stillThere = (await calls.CallAsync(token => compute.FindByJobIdAsync(jobId, token), cancellationToken).ConfigureAwait(false)).Select(v => (v.Name, v.Zone)).ToHashSet();
            foreach (var create in sent)
            {
                // Read back by name too: a lagging listing says "none" for a VM that is still there. A VM seen both ways is counted once.
                var (state, _) = await LookByNameAsync(create, cancellationToken).ConfigureAwait(false);
                if (state == NameLook.Present)
                {
                    stillThere.Add((create.Name, create.Zone));
                }
                else if (state == NameLook.Unknown)
                {
                    // Unreadable is not gone: a delete by name says it (404 is gone), so a failing read never marks a cancel failed once the VM is.
                    var (result, _) = await DeleteByNameAsync(create.Name, create.Zone, cancellationToken).ConfigureAwait(false);
                    if (result != DeleteResult.AlreadyGone)
                    {
                        stillThere.Add((create.Name, create.Zone));
                    }
                }
            }

            if (stillThere.Count == 0)
            {
                break;
            }

            if (settings.ElapsedSince(clockStart) >= settings.LifecycleTimeout)
            {
                return $"{stillThere.Count} VM(s) for this job were still there {settings.LifecycleTimeout.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture)} s after Delete.";
            }

            await settings.DelayAsync(settings.LifecyclePollInterval, cancellationToken).ConfigureAwait(false);
        }

        var seconds = settings.CreateSettleTimeout.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture);
        return listError ?? settle switch
        {
            CreateSettleState.CreateInFlight => $"A create request was still in flight after {seconds} s, so a VM may still appear. The app deletes it if it does; check the Cloud page to be sure.",
            CreateSettleState.ReuseInFlight => $"A request to start an earlier VM was still in flight after {seconds} s, so that VM may start. The app does not stop it for you: check the Cloud page and stop or delete it there.",
            _ => null,
        };
    }

    /// <summary>
    /// The run page's "Stop VM now": stops every VM this job id owns, found
    /// by label lookup rather than a remembered zone (Hard Rule 9). A job
    /// with no VM is a no-op. The run's own phase is not changed here: the
    /// next health check in <see cref="CloudJobRunner.RunAsync"/> sees the stopped VM and
    /// records the failure, which keeps one writer per phase transition.
    /// </summary>
    public async Task StopVmsAsync(string jobId, CancellationToken cancellationToken)
    {
        NoteAppRequestedEnd(jobId);
        var owned = await calls.CallAsync(token => compute.FindByJobIdAsync(jobId, token), cancellationToken).ConfigureAwait(false);
        foreach (var vm in owned)
        {
            await calls.CallAsync(
                async token =>
                {
                    await compute.StopVmAsync(vm.Name, vm.Zone, token).ConfigureAwait(false);
                    return true;
                },
                cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>The run page's "Delete VM now"; same lookup and no-op rules as <see cref="StopVmsAsync"/>.</summary>
    public async Task DeleteVmsAsync(string jobId, CancellationToken cancellationToken)
    {
        NoteAppRequestedEnd(jobId);
        var owned = await calls.CallAsync(token => compute.FindByJobIdAsync(jobId, token), cancellationToken).ConfigureAwait(false);
        foreach (var vm in owned)
        {
            await calls.CallAsync(
                async token =>
                {
                    await compute.DeleteVmAsync(vm.Name, vm.Zone, token).ConfigureAwait(false);
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
