using System.Globalization;

namespace DnaEntropyGraph.Core.Cloud;

/// <summary>
/// The zone ladder, and the ONLY owner of the in-flight create state: creates this instance has started and not seen
/// finish, by task. A create the caller gave up on (deadline, cancel) can still land afterwards; the cancel path
/// (<see cref="VmCanceller"/>) and the ladder wait for these through <see cref="SettleInflightCreatesAsync"/> before they
/// record a terminal state. Nothing else touches the dictionary.
/// </summary>
internal sealed class VmProvisioner(IComputeGateway compute, GatewayCalls calls, CloudRunSettings settings, VmTerminator terminator, Func<string, Task<bool>>? jobIsFinished = null)
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Task, string> _inflightCreates = new();

    /// <summary>The GPU machine families (G2 = L4, A2 = A100, A3 = H100); anything else is a CPU smoke VM.</summary>
    public static bool ExpectsGpu(string machineType)
        => machineType.StartsWith("g2-", StringComparison.Ordinal)
           || machineType.StartsWith("a2-", StringComparison.Ordinal)
           || machineType.StartsWith("a3-", StringComparison.Ordinal);

    /// <summary>
    /// Issue #261: the spec to create, with the startup script and the
    /// <c>deg-*</c> attributes attached once the bucket name is known. A
    /// request with no worker image creates the VM without them (nothing
    /// would run the job; the caller owns supplying an image). An invalid
    /// value throws here, before any VM exists, and <see cref="CloudJobRunner.RunAsync"/>
    /// records the run as Failed.
    /// </summary>
    public static VmSpec WithStartupMetadata(CloudJobRequest request, string bucket)
    {
        if (request.WorkerImage is null)
        {
            return request.Spec;
        }

        var metadata = StartupMetadata.Build(
            bucket,
            request.JobId,
            request.WorkerImage,
            ExpectsGpu(request.Spec.MachineType),
            request.AfterTask,
            request.Spec.MaxRunDuration);
        return request.Spec with { Metadata = metadata };
    }

    /// <summary>
    /// Issue #257's reconciler: find an existing VM for this job id first,
    /// across every zone, and adopt it - never attempt a new zone until
    /// that lookup comes back empty. Only once nothing is found does this
    /// walk <see cref="CloudJobRequest.Zones"/> in order, using
    /// <see cref="OperationPoller"/> to turn each attempt's thrown
    /// <see cref="CloudOperationException"/> (today, from
    /// <see cref="DnaEntropyGraph.Cloud.FakeGcp"/>'s synchronous throw; a
    /// real gateway's LRO would instead report "not done yet" across
    /// several polls before resolving) into a classified outcome and apply
    /// docs/cloud_design.md section 5's abort-vs-continue rule.
    /// </summary>
    public async Task<ProvisionResult> ProvisionAsync(CloudJobRequest request, VmSpec spec, RunProgress progress, CancellationToken cancellationToken)
        => await ProvisionAsync(request, _ => Task.FromResult(spec), progress, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// As above, but the spec is made by <paramref name="completeSpec"/> only once the adoption lookup has found nothing, so
    /// whatever it needs (issue #606: the worker identity, an IAM call that can fail) is never asked of a run that adopts
    /// a VM, and a failure there can never end a healthy VM. It runs once, before the first create.
    /// </summary>
    public async Task<ProvisionResult> ProvisionAsync(CloudJobRequest request, Func<CancellationToken, Task<VmSpec>> completeSpec, RunProgress progress, CancellationToken cancellationToken)
    {
        var existing = await calls.CallAsync(token => compute.FindByJobIdAsync(request.JobId, token), cancellationToken).ConfigureAwait(false);
        if (existing.Count > 0)
        {
            progress.VmMayExist = true;
            progress.VmName = existing[0].Name;
            return ProvisionResult.Ok(existing[0].Zone, existing[0].Name);
        }

        var spec = await completeSpec(cancellationToken).ConfigureAwait(false);

        // Issue #94: a stopped VM this installation left behind is started for this job before a new one is created.
        if (await TryReuseAsync(request, spec, progress, cancellationToken).ConfigureAwait(false) is { } reuse)
        {
            return reuse;
        }

        // One value for both the create's own timeout and the poller's deadline, so they cannot drift apart: the create's
        // timeout must be the one that fires first (it records createTimedOut), and the poller's deadline is only the
        // backstop at the same instant.
        var createTimeout = settings.CreateTimeout;

        foreach (var zone in request.Zones)
        {
            // The kind the gateway (or the resilience pipeline in front of
            // it) already decided. Re-deriving it from the error text would
            // turn the pipeline's Network (retries exhausted, breaker open)
            // into Other, walk on to the next zone and end as a Stockout.
            CloudErrorKind? thrownKind = null;
            var createTimedOut = false;
            Task<VmDescriptor>? createTask = null;
            progress.VmMayExist = true;
            var outcome = await OperationPoller.PollAsync<VmDescriptor>(
                async _ =>
                {
                    try
                    {
                        // The create is bounded here by its own timeout (WaitAsync), which must stay the one that fires
                        // first and records createTimedOut, so it takes the caller's token, not the poller's deadline
                        // token (the same instant, so the poller's would win and hide the abandoned create).
                        createTask = compute.CreateVmAsync(spec, zone, cancellationToken);
                        TrackInflightCreate(request.JobId, createTask);
                        var vm = await createTask.WaitAsync(createTimeout, settings.TimeProvider, cancellationToken).ConfigureAwait(false);
                        return new OperationPoll<VmDescriptor>(true, vm, null);
                    }
                    catch (TimeoutException)
                    {
                        createTimedOut = true;
                        thrownKind = CloudErrorKind.Network;
                        return new OperationPoll<VmDescriptor>(true, null, new CloudError(OperationPoller.TimeoutCode, null, "Creating the VM timed out."));
                    }
                    catch (CloudOperationException ex)
                    {
                        thrownKind = ex.Kind;
                        return new OperationPoll<VmDescriptor>(true, null, ex.Error);
                    }
                },
                createTimeout,
                cancellationToken,
                time: settings.TimeProvider).ConfigureAwait(false);

            if (createTimedOut)
            {
                // The request may have been accepted even though no answer came, and can still land. Wait for it to
                // settle (bounded) and delete whatever it made BEFORE the run is allowed to end or try another zone;
                // if it cannot be confirmed the run says so (vm_end_unconfirmed). The VM's own maxRunDuration
                // (Hard Rule 10) is the backstop.
                var settleNote = await SettleAbandonedCreateAsync(request, createTask!).ConfigureAwait(false);
                if (settleNote is not null)
                {
                    return ProvisionResult.Failed(CloudErrorKind.Network, outcome.Error!.Message, settleNote);
                }
            }

            if (outcome.Success)
            {
                return ProvisionResult.Ok(zone);
            }

            var kind = thrownKind ?? CloudErrorClassifier.Classify(outcome.Error!);

            if (kind == CloudErrorKind.AlreadyExists)
            {
                // The exact resource this attempt would have created is
                // already there under someone else's create - adopt it
                // rather than treat this zone as failed.
                var vm = await calls.CallAsync(token => compute.GetVmAsync(request.Spec.VmName, zone, token), cancellationToken).ConfigureAwait(false);
                if (vm is not null)
                {
                    return ProvisionResult.Ok(zone);
                }
            }

            // A create can fail AFTER the server accepted it (the ResilientGateways retry, a response lost on the wire, an
            // internal error answered late): look by label and delete whatever landed before the ladder moves on or the
            // run is recorded, or a zone's VM is left billing beside the next zone's. A timed-out create was settled above.
            if (!createTimedOut && kind is CloudErrorKind.Network or CloudErrorKind.Other or CloudErrorKind.Stockout)
            {
                var sweepNote = await terminator.EndVmAfterFailureAsync(request, null, forceDelete: true).ConfigureAwait(false);
                if (sweepNote is not null)
                {
                    return ProvisionResult.Failed(kind, outcome.Error!.Message, sweepNote, RunErrorCodes.For(outcome.Error, kind));
                }
            }

            if (VmFacts.IsProjectWide(kind) || kind == CloudErrorKind.Network)
            {
                // docs/cloud_design.md section 5: billing/API/permission/org
                // policy abort the WHOLE ladder immediately - retrying a
                // different zone cannot fix a project-wide problem.
                return ProvisionResult.Failed(kind, outcome.Error!.Message, code: RunErrorCodes.For(outcome.Error, kind));
            }

            // Quota / stockout / other: per-zone or per-region, worth trying
            // the next zone (CLAUDE.md: "quota is not stockout", but neither
            // one aborts the ladder). Network is not in this list: another
            // zone cannot fix an unreachable API, and reporting it as a
            // stockout after trying them all is the bug this guards against.
        }

        return ProvisionResult.Failed(CloudErrorKind.Stockout, $"No zone in [{string.Join(", ", request.Zones)}] could provision '{request.Spec.VmName}'.");
    }

    /// <summary>
    /// Issue #94. Null means "provision a new VM": nothing eligible, or the start failed in a way a new VM can fix. A VM is eligible when
    /// it is this installation's (the listing is by label, Hard Rule 9), stopped, of the same model label and machine type, and the run
    /// that last used it has ended (a stopped VM of a run still underway is that run's). A start that fails is never a failed run by
    /// itself: the VM, which was claimed for this job by then, is ended by label so nothing is left billing, and the ladder takes over.
    /// A failure of a class a new VM cannot fix (billing off, the API off, a missing permission, an unreachable API) aborts like the ladder's.
    /// </summary>
    private async Task<ProvisionResult?> TryReuseAsync(CloudJobRequest request, VmSpec spec, RunProgress progress, CancellationToken cancellationToken)
    {
        if (jobIsFinished is null)
        {
            return null;
        }

        IReadOnlyList<VmDescriptor> listed;
        try
        {
            listed = await calls.CallAsync(token => compute.ListByInstallationAsync(spec.InstallationId, token), cancellationToken).ConfigureAwait(false);
        }
        catch (CloudOperationException)
        {
            // Not being able to look is not a reason to fail: the create path reports a project-wide problem itself.
            return null;
        }

        var model = spec.ToLabels()["model"];
        VmDescriptor? chosen = null;
        foreach (var vm in listed.OrderByDescending(v => v.StoppedAt ?? DateTimeOffset.MinValue))
        {
            if (!VmFacts.IsStopped(vm) || vm.Labels is null || vm.MachineType != spec.MachineType
                || !vm.Labels.TryGetValue("app", out var app) || app != VmSpec.AppLabelValue
                || !vm.Labels.TryGetValue("installation-id", out var owner) || owner != spec.InstallationId
                || !vm.Labels.TryGetValue("model", out var vmModel) || vmModel != model
                || !vm.Labels.TryGetValue("job-id", out var oldJob) || oldJob == request.JobId)
            {
                continue;
            }

            if (await jobIsFinished(oldJob).ConfigureAwait(false))
            {
                chosen = vm;
                break;
            }
        }

        if (chosen is null)
        {
            return null;
        }

        progress.VmMayExist = true;
        try
        {
            var started = await calls.CallAsync(token => compute.ReuseVmAsync(spec, chosen.Name, chosen.Zone, token), cancellationToken, settings.CreateTimeout).ConfigureAwait(false);
            progress.VmName = started.Name;
            return ProvisionResult.Ok(started.Zone, started.Name);
        }
        catch (CloudOperationException ex)
        {
            // The claim may already have been made: whatever carries this job's label is ended before anything else is tried.
            var note = await terminator.EndVmAfterFailureAsync(request, null, forceDelete: true).ConfigureAwait(false);
            if (note is not null || VmFacts.IsProjectWide(ex.Kind) || ex.Kind == CloudErrorKind.Network)
            {
                return ProvisionResult.Failed(ex.Kind, ex.Error.Message, note, RunErrorCodes.For(ex.Error, ex.Kind));
            }

            return null;
        }
    }

    /// <summary>Waits, bounded by <see cref="CloudRunSettings.CreateSettleTimeout"/>, for every create this instance started for the job to land or fail. False when one is still in flight.</summary>
    public async Task<bool> SettleInflightCreatesAsync(string jobId)
    {
        var pending = _inflightCreates.Where(kv => kv.Value == jobId).Select(kv => kv.Key).ToList();
        if (pending.Count == 0)
        {
            return true;
        }

        var all = Task.WhenAll(pending);
        var winner = await Task.WhenAny(all, Task.Delay(settings.CreateSettleTimeout, settings.TimeProvider)).ConfigureAwait(false);
        if (winner != all)
        {
            return false;
        }

        try
        {
            await all.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // A create that failed made no VM; nothing to wait for or to report.
        }

        return true;
    }

    private void TrackInflightCreate(string jobId, Task createTask)
    {
        _inflightCreates[createTask] = jobId;
        _ = createTask.ContinueWith(
            t =>
            {
                _ = t.Exception;
                _inflightCreates.TryRemove(t, out _);
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    /// <summary>
    /// After a create timed out: wait for it to settle, then delete whatever landed (a VM that never ran has no disk
    /// worth keeping). Returns null when nothing is left, else a note that the VM end is not confirmed.
    /// </summary>
    private async Task<string?> SettleAbandonedCreateAsync(CloudJobRequest request, Task createTask)
    {
        var winner = await Task.WhenAny(createTask, Task.Delay(settings.CreateSettleTimeout, settings.TimeProvider)).ConfigureAwait(false);
        if (winner != createTask)
        {
            return $" (VM end not confirmed: a create request was still in flight after {settings.CreateSettleTimeout.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture)} s and could not be cancelled)";
        }

        return await terminator.EndVmAfterFailureAsync(request, null, forceDelete: true).ConfigureAwait(false);
    }
}

internal readonly struct ProvisionResult
{
    private ProvisionResult(bool success, string? zone, CloudErrorKind? failureKind, string? failureMessage, string? vmEndNote, string? failureCode, string? vmName = null)
    {
        VmName = vmName;
        FailureCode = failureCode;
        Success = success;
        Zone = zone;
        FailureKind = failureKind;
        FailureMessage = failureMessage;
        VmEndNote = vmEndNote;
    }

    /// <summary>Set when a create the runner gave up on could not be confirmed ended.</summary>
    public string? VmEndNote { get; }

    public bool Success { get; }

    public string? Zone { get; }

    public CloudErrorKind? FailureKind { get; }

    public string? FailureMessage { get; }

    /// <summary>The code the run row records (issue #604): the error's own specific code (PERMISSION_ACTAS) when it has one, else the code of <see cref="FailureKind"/>.</summary>
    public string? FailureCode { get; }

    /// <summary>The name of the VM when it is not the one computed from the job id (a reused or adopted VM, #94); null when the caller knows it already.</summary>
    public string? VmName { get; }

    public static ProvisionResult Ok(string zone, string? vmName = null) => new(true, zone, null, null, null, null, vmName);

    public static ProvisionResult Failed(CloudErrorKind kind, string message, string? vmEndNote = null, string? code = null) => new(false, null, kind, message, vmEndNote, code ?? RunErrorCodes.For(kind));
}
