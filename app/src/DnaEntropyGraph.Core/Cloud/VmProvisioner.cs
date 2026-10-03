using System.Globalization;

namespace DnaEntropyGraph.Core.Cloud;

/// <summary>
/// The zone ladder, and the ONLY owner of the in-flight create state: creates this instance has started and not seen
/// finish, by task. A create the caller gave up on (deadline, cancel) can still land afterwards; the cancel path
/// (<see cref="VmCanceller"/>) and the ladder wait for these through <see cref="SettleInflightCreatesAsync"/> before they
/// record a terminal state. Nothing else touches the dictionary.
/// </summary>
internal sealed class VmProvisioner(IComputeGateway compute, GatewayCalls calls, CloudRunSettings settings, VmTerminator terminator)
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
    {
        var existing = await calls.CallAsync(token => compute.FindByJobIdAsync(request.JobId, token), cancellationToken).ConfigureAwait(false);
        if (existing.Count > 0)
        {
            progress.VmMayExist = true;
            return ProvisionResult.Ok(existing[0].Zone);
        }

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
                async pollToken =>
                {
                    try
                    {
                        // WaitAsync: the poller only checks its deadline between polls, so a create that never answers
                        // would otherwise hang the run forever.
                        createTask = compute.CreateVmAsync(spec, zone, pollToken);
                        TrackInflightCreate(request.JobId, createTask);
                        var vm = await createTask.WaitAsync(settings.CreateTimeout, pollToken).ConfigureAwait(false);
                        return new OperationPoll<VmDescriptor>(true, vm, null);
                    }
                    catch (TimeoutException)
                    {
                        createTimedOut = true;
                        thrownKind = CloudErrorKind.Network;
                        return new OperationPoll<VmDescriptor>(true, null, new CloudError("OPERATION_POLL_TIMEOUT", null, "Creating the VM timed out."));
                    }
                    catch (CloudOperationException ex)
                    {
                        thrownKind = ex.Kind;
                        return new OperationPoll<VmDescriptor>(true, null, ex.Error);
                    }
                },
                settings.CreateTimeout,
                cancellationToken).ConfigureAwait(false);

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
                    return ProvisionResult.Failed(kind, outcome.Error!.Message, sweepNote);
                }
            }

            if (VmFacts.IsProjectWide(kind) || kind == CloudErrorKind.Network)
            {
                // docs/cloud_design.md section 5: billing/API/permission/org
                // policy abort the WHOLE ladder immediately - retrying a
                // different zone cannot fix a project-wide problem.
                return ProvisionResult.Failed(kind, outcome.Error!.Message);
            }

            // Quota / stockout / other: per-zone or per-region, worth trying
            // the next zone (CLAUDE.md: "quota is not stockout", but neither
            // one aborts the ladder). Network is not in this list: another
            // zone cannot fix an unreachable API, and reporting it as a
            // stockout after trying them all is the bug this guards against.
        }

        return ProvisionResult.Failed(CloudErrorKind.Stockout, $"No zone in [{string.Join(", ", request.Zones)}] could provision '{request.Spec.VmName}'.");
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
        var winner = await Task.WhenAny(all, Task.Delay(settings.CreateSettleTimeout)).ConfigureAwait(false);
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
        var winner = await Task.WhenAny(createTask, Task.Delay(settings.CreateSettleTimeout)).ConfigureAwait(false);
        if (winner != createTask)
        {
            return $" (VM end not confirmed: a create request was still in flight after {settings.CreateSettleTimeout.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture)} s and could not be cancelled)";
        }

        return await terminator.EndVmAfterFailureAsync(request, null, forceDelete: true).ConfigureAwait(false);
    }
}

internal readonly struct ProvisionResult
{
    private ProvisionResult(bool success, string? zone, CloudErrorKind? failureKind, string? failureMessage, string? vmEndNote)
    {
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

    public static ProvisionResult Ok(string zone) => new(true, zone, null, null, null);

    public static ProvisionResult Failed(CloudErrorKind kind, string message, string? vmEndNote = null) => new(false, null, kind, message, vmEndNote);
}
