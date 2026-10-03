using System.Globalization;

namespace DnaEntropyGraph.Core.Cloud;

/// <summary>
/// Hard Rule 11: ends a run's VM (stop or delete), verifies it independently, and finds VMs by job-id label (Hard Rule 9).
/// Holds no run state: every method is a look at the gateway plus a bounded wait.
/// </summary>
internal sealed class VmTerminator(IComputeGateway compute, GatewayCalls calls, CloudRunSettings settings)
{
    /// <summary>
    /// Best effort, never throws: a run that failed or gave up must still end its VM (Hard Rule 11), by
    /// delete when the user chose delete and by stop otherwise. Returns null when the VM was confirmed
    /// ended, else a sentence for the run's detail saying it was not, so no message claims a stop nobody checked.
    /// </summary>
    public async Task<string?> EndVmAfterFailureAsync(CloudJobRequest request, string? zone, bool forceDelete = false)
    {
        var action = forceDelete ? AfterTaskAction.Delete : FailureEndAction(request);
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
    public async Task<string?> DeleteOwnedVmsAndVerifyAsync(string jobId, bool createsSettled, CancellationToken cancellationToken)
    {
        var owned = await calls.CallAsync(token => compute.FindByJobIdAsync(jobId, token), cancellationToken).ConfigureAwait(false);
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
            var remaining = await calls.CallAsync(token => compute.FindByJobIdAsync(jobId, token), cancellationToken).ConfigureAwait(false);
            if (remaining.Count == 0)
            {
                break;
            }

            if (settings.ElapsedSince(clockStart) >= settings.LifecycleTimeout)
            {
                return $"{remaining.Count} VM(s) for this job were still there {settings.LifecycleTimeout.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture)} s after Delete.";
            }

            await settings.DelayAsync(settings.LifecyclePollInterval, cancellationToken).ConfigureAwait(false);
        }

        return createsSettled
            ? null
            : $"A create request was still in flight after {settings.CreateSettleTimeout.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture)} s, so a VM may still appear.";
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
