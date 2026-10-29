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

    /// <summary>
    /// The VM a create that answered made, looked at AGAIN now (never a remembered descriptor): null when it is gone (the worker or the platform ended it) or is not this
    /// job's, else what is there to delete. Issue #938: the label listing can lag a create that already answered, so a cancel also deletes these by name.
    /// </summary>
    private async Task<VmDescriptor?> LookAgainAsync(string jobId, VmDescriptor answered, CancellationToken cancellationToken)
    {
        var fresh = await calls.CallAsync(token => compute.GetVmAsync(answered.Name, answered.Zone, token), cancellationToken).ConfigureAwait(false);
        return fresh is not null && (fresh.Labels is null ? fresh.Name == $"deg-{jobId}" : fresh.Labels.TryGetValue("job-id", out var owner) && owner == jobId) ? fresh : null;
    }

    private static string ErrorClassOf(Exception ex)
        => (ex as CloudOperationException)?.Kind.ToString().ToLowerInvariant() ?? "other";

    /// <summary>
    /// Issue #938: a create the cancel stopped waiting for has landed (or failed, or never will within <see cref="CloudRunSettings.LateCreateReapWindow"/>). Deletes the VM it made, by
    /// name, bounded by <see cref="CloudRunSettings.LifecycleTimeout"/> and a per-call deadline, and reads it back. The VM is looked at again first, and a delete that errors is
    /// judged by that look (another delete, the worker or the platform may have got there first), so a lost race is never a failure. Never throws.
    /// </summary>
    public async Task<LateCreateOutcome> ReapLateCreateAsync(string jobId, Task<VmDescriptor> create)
    {
        try
        {
            var winner = await Task.WhenAny(create, Task.Delay(settings.LateCreateReapWindow, settings.TimeProvider)).ConfigureAwait(false);
            if (winner != create)
            {
                return new LateCreateOutcome(jobId, LateCreateState.GaveUp);
            }

            if (create.IsFaulted || create.IsCanceled)
            {
                _ = create.Exception;
                return new LateCreateOutcome(jobId, LateCreateState.CreateFailed);
            }

            var answered = create.Result;
            var clockStart = settings.StartClock();
            string? lastError = null;
            var deleteSent = false;
            while (true)
            {
                VmDescriptor? present;
                try
                {
                    present = await LookAgainAsync(jobId, answered, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    lastError = ErrorClassOf(ex);
                    present = answered; // an unreadable VM is treated as present: the delete below is the safe move
                }

                if (present is null)
                {
                    return new LateCreateOutcome(jobId, deleteSent ? LateCreateState.Deleted : LateCreateState.NothingToDelete);
                }

                try
                {
                    await calls.CallAsync(
                        async token =>
                        {
                            try
                            {
                                await compute.DeleteVmAsync(answered.Name, answered.Zone, token).ConfigureAwait(false);
                            }
                            catch (CloudOperationException ex) when (ex.Error.HttpStatus == 404)
                            {
                                // Already gone: the state wanted.
                            }

                            return true;
                        },
                        CancellationToken.None).ConfigureAwait(false);
                    lastError = null;
                    deleteSent = true;
                }
                catch (Exception ex)
                {
                    lastError = ErrorClassOf(ex);
                }

                if (settings.ElapsedSince(clockStart) >= settings.LifecycleTimeout)
                {
                    // One last look: a delete that errored or lagged may still have worked.
                    try
                    {
                        if (await LookAgainAsync(jobId, answered, CancellationToken.None).ConfigureAwait(false) is null)
                        {
                            return new LateCreateOutcome(jobId, LateCreateState.Deleted);
                        }
                    }
                    catch (Exception ex)
                    {
                        lastError ??= ErrorClassOf(ex);
                    }

                    return new LateCreateOutcome(jobId, LateCreateState.DeleteFailed, lastError ?? "other");
                }

                await settings.DelayAsync(settings.LifecyclePollInterval, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            return new LateCreateOutcome(jobId, LateCreateState.DeleteFailed, ErrorClassOf(ex));
        }
    }

    public async Task<string?> DeleteOwnedVmsAndVerifyAsync(string jobId, bool createsSettled, CancellationToken cancellationToken, IReadOnlyList<VmDescriptor>? answeredCreates = null)
    {
        var owned = (await calls.CallAsync(token => compute.FindByJobIdAsync(jobId, token), cancellationToken).ConfigureAwait(false)).ToList();
        foreach (var answered in answeredCreates ?? [])
        {
            // The listing may lag a create that already answered (#938): such a VM is found by name, and only if it is still there and this job's.
            if (!owned.Any(o => o.Name == answered.Name && o.Zone == answered.Zone) && await LookAgainAsync(jobId, answered, cancellationToken).ConfigureAwait(false) is { } still)
            {
                owned.Add(still);
            }
        }

        string? deleteError = null;
        foreach (var vm in owned)
        {
            // Every VM is attempted even when an earlier delete failed: the first failure is what is reported.
            try
            {
                await calls.CallAsync(
                    async token =>
                    {
                        try
                        {
                            await compute.DeleteVmAsync(vm.Name, vm.Zone, token).ConfigureAwait(false);
                        }
                        catch (CloudOperationException ex) when (ex.Error.HttpStatus == 404)
                        {
                            // Already deleted: the state we wanted.
                        }

                        return true;
                    },
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException && cancellationToken.IsCancellationRequested))
            {
                deleteError ??= $"VM '{vm.Name}' in zone '{vm.Zone}' could not be deleted: {ex.Message}";
            }
        }

        if (deleteError is not null)
        {
            return deleteError;
        }

        var clockStart = settings.StartClock();
        while (true)
        {
            var remaining = (await calls.CallAsync(token => compute.FindByJobIdAsync(jobId, token), cancellationToken).ConfigureAwait(false)).Count;
            foreach (var answered in answeredCreates ?? [])
            {
                // Read back by name too: a lagging listing says "none" for a VM that is still there.
                if (await LookAgainAsync(jobId, answered, cancellationToken).ConfigureAwait(false) is not null)
                {
                    remaining++;
                }
            }

            if (remaining == 0)
            {
                break;
            }

            if (settings.ElapsedSince(clockStart) >= settings.LifecycleTimeout)
            {
                return $"{remaining} VM(s) for this job were still there {settings.LifecycleTimeout.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture)} s after Delete.";
            }

            await settings.DelayAsync(settings.LifecyclePollInterval, cancellationToken).ConfigureAwait(false);
        }

        return createsSettled
            ? null
            : $"A create request was still in flight after {settings.CreateSettleTimeout.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture)} s, so a VM may still appear. The app deletes it if it does; check the Cloud page to be sure.";
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
