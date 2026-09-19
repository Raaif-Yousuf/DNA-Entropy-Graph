using DnaEntropyGraph.Core.Abstractions;

namespace DnaEntropyGraph.Core.Cloud;

/// <summary>
/// Everything one cloud run needs that is not itself a gateway call: the
/// job id, the VM to request, the candidate zones to try in order (a
/// walking-skeleton zone list, not the full escalating-parallelism ladder -
/// that is issue #86), the input/output object keys, and what to do with
/// the VM once the run finishes (Hard Rule 11 default: <see cref="AfterTaskAction.Stop"/>).
/// </summary>
public sealed record CloudJobRequest(
    string JobId,
    VmSpec Spec,
    IReadOnlyList<string> Zones,
    IReadOnlyList<string> InputObjectKeys,
    IReadOnlyList<string> OutputObjectKeys,
    AfterTaskAction AfterTask = AfterTaskAction.Stop);

/// <summary>The phase a run ended in, plus - when it did not reach a happy terminal phase - which class of error stopped it and why.</summary>
public sealed record CloudJobResult(JobPhase FinalPhase, CloudErrorKind? FailureKind = null, string? FailureMessage = null);

/// <summary>
/// Issue #58: turns a <see cref="CloudJobRequest"/> into a finished run over
/// <see cref="IComputeGateway"/>/<see cref="IStorageGateway"/>/<see cref="IProjectSetupGateway"/>/
/// <see cref="IQuotaGateway"/> (real or - today, since no real gateway
/// exists yet - <see cref="DnaEntropyGraph.Cloud.FakeGcp"/> implementing
/// all four), driven by <see cref="JobStateMachine"/>'s legal-transition
/// table and the abort-vs-continue rule from docs/cloud_design.md section 5
/// (<see cref="CloudErrorClassifier"/>'s first production caller: nothing
/// consumed it before this).
///
/// Every phase change is written to <see cref="IRunRepository"/> BEFORE
/// <paramref name="onPhaseChanged"/> is invoked (Hard Rule 16-adjacent
/// discipline from issue #58's own Done-when: "every phase change committed
/// to SQLite before the UI is told").
///
/// <b>Crash-and-resume, without a separate "resume" method.</b> Every phase
/// transition first asks <see cref="JobStateMachine.HasAlreadyPassed"/> and
/// silently skips a step already recorded by an earlier, now-discarded
/// runner instance for the same job id, and <see cref="ProvisionAsync"/>
/// always calls <see cref="IComputeGateway.FindByJobIdAsync"/> BEFORE
/// attempting any zone (issue #257) - so calling <see cref="RunAsync"/>
/// again for the same job id, on a brand-new <see cref="CloudJobRunner"/>
/// sharing the same gateways and repository, is the reconciler this
/// issue's observable describes: "constructing a new reconciler resumes the
/// same job to Completed in the fake."
/// </summary>
public sealed class CloudJobRunner
{
    /// <summary>How long <see cref="ProvisionAsync"/> gives one zone's create attempt to resolve, via <see cref="OperationPoller"/>.</summary>
    private static readonly TimeSpan CreateVmDeadline = TimeSpan.FromSeconds(30);

    private readonly IComputeGateway _compute;
    private readonly IStorageGateway _storage;
    private readonly IProjectSetupGateway _projectSetup;
    private readonly IQuotaGateway _quota;
    private readonly IRunRepository _runs;
    private readonly Action<string, JobPhase>? _onPhaseChanged;

    public CloudJobRunner(
        IComputeGateway computeGateway,
        IStorageGateway storageGateway,
        IProjectSetupGateway projectSetupGateway,
        IQuotaGateway quotaGateway,
        IRunRepository runRepository,
        Action<string, JobPhase>? onPhaseChanged = null)
    {
        _compute = computeGateway;
        _storage = storageGateway;
        _projectSetup = projectSetupGateway;
        _quota = quotaGateway;
        _runs = runRepository;
        _onPhaseChanged = onPhaseChanged;
    }

    public async Task<JobPhase> GetPhaseAsync(string jobId, CancellationToken cancellationToken)
    {
        var runs = await _runs.GetAllAsync(cancellationToken).ConfigureAwait(false);
        return LatestPhase(runs, jobId);
    }

    /// <summary>
    /// Runs (or resumes) <paramref name="request"/> to a terminal phase.
    /// Safe to call more than once for the same job id - see this class's
    /// own doc comment on crash-and-resume.
    /// </summary>
    public async Task<CloudJobResult> RunAsync(CloudJobRequest request, CancellationToken cancellationToken)
    {
        var current = await GetPhaseAsync(request.JobId, cancellationToken).ConfigureAwait(false);
        if (JobStateMachine.IsTerminal(current))
        {
            return new CloudJobResult(current);
        }

        var preflight = await PreflightAsync(request, cancellationToken).ConfigureAwait(false);
        if (preflight is not null)
        {
            await SetPhaseAsync(request.JobId, JobPhase.Failed, cancellationToken).ConfigureAwait(false);
            return new CloudJobResult(JobPhase.Failed, preflight.Value.Kind, preflight.Value.Message);
        }

        await SetPhaseAsync(request.JobId, JobPhase.Validating, cancellationToken).ConfigureAwait(false);
        await SetPhaseAsync(request.JobId, JobPhase.Uploading, cancellationToken).ConfigureAwait(false);
        await UploadInputsAsync(request, cancellationToken).ConfigureAwait(false);

        await SetPhaseAsync(request.JobId, JobPhase.Provisioning, cancellationToken).ConfigureAwait(false);
        var provisioned = await ProvisionAsync(request, cancellationToken).ConfigureAwait(false);
        if (!provisioned.Success)
        {
            await SetPhaseAsync(request.JobId, JobPhase.Failed, cancellationToken).ConfigureAwait(false);
            return new CloudJobResult(JobPhase.Failed, provisioned.FailureKind, provisioned.FailureMessage);
        }

        var zone = provisioned.Zone!;

        await SetPhaseAsync(request.JobId, JobPhase.Preparing, cancellationToken).ConfigureAwait(false);
        var afterCreate = await _compute.GetVmAsync(request.Spec.VmName, zone, cancellationToken).ConfigureAwait(false);
        if (!IsHealthyRunning(afterCreate, out var unhealthyReason))
        {
            await SetPhaseAsync(request.JobId, JobPhase.Failed, cancellationToken).ConfigureAwait(false);
            return new CloudJobResult(JobPhase.Failed, CloudErrorKind.Other, unhealthyReason);
        }

        await SetPhaseAsync(request.JobId, JobPhase.Running, cancellationToken).ConfigureAwait(false);
        // RUNNING is not working (CLAUDE.md Critical Pitfalls): the only
        // real health signal is the worker's own heartbeat in status.json,
        // which is worker-owned and out of this issue's scope. This second
        // poll is the compute-level signal this runner CAN check on its
        // own: has the VM itself been preempted since the last look.
        var duringRun = await _compute.GetVmAsync(request.Spec.VmName, zone, cancellationToken).ConfigureAwait(false);
        if (!IsHealthyRunning(duringRun, out var midRunReason))
        {
            await SetPhaseAsync(request.JobId, JobPhase.Failed, cancellationToken).ConfigureAwait(false);
            return new CloudJobResult(JobPhase.Failed, CloudErrorKind.Other, midRunReason);
        }

        await SetPhaseAsync(request.JobId, JobPhase.Finalizing, cancellationToken).ConfigureAwait(false);
        var afterTaskError = await AfterTaskAsync(request, zone, cancellationToken).ConfigureAwait(false);
        if (afterTaskError is not null)
        {
            await SetPhaseAsync(request.JobId, JobPhase.Failed, cancellationToken).ConfigureAwait(false);
            return new CloudJobResult(JobPhase.Failed, CloudErrorKind.Other, afterTaskError);
        }

        await SetPhaseAsync(request.JobId, JobPhase.Downloading, cancellationToken).ConfigureAwait(false);
        await DownloadOutputsAsync(request, cancellationToken).ConfigureAwait(false);

        await SetPhaseAsync(request.JobId, JobPhase.Completed, cancellationToken).ConfigureAwait(false);
        return new CloudJobResult(JobPhase.Completed);
    }

    /// <summary>
    /// Cancels a run in progress: <see cref="JobPhase.Cancelling"/>, delete
    /// every VM this job's id currently owns (by label lookup, not a
    /// remembered zone - the runner that requested cancellation may not be
    /// the one that provisioned it), then <see cref="JobPhase.Cancelled"/>.
    /// </summary>
    public async Task CancelAsync(string jobId, CancellationToken cancellationToken)
    {
        var current = await GetPhaseAsync(jobId, cancellationToken).ConfigureAwait(false);
        if (JobStateMachine.IsTerminal(current))
        {
            return;
        }

        await SetPhaseAsync(jobId, JobPhase.Cancelling, cancellationToken).ConfigureAwait(false);

        var owned = await _compute.FindByJobIdAsync(jobId, cancellationToken).ConfigureAwait(false);
        foreach (var vm in owned)
        {
            await _compute.DeleteVmAsync(vm.Name, vm.Zone, cancellationToken).ConfigureAwait(false);
        }

        await SetPhaseAsync(jobId, JobPhase.Cancelled, cancellationToken).ConfigureAwait(false);
    }

    public async Task UploadInputsAsync(CloudJobRequest request, CancellationToken cancellationToken)
    {
        var bucket = await _storage.EnsureBucketAsync(request.Spec.ProjectId, cancellationToken).ConfigureAwait(false);
        foreach (var key in request.InputObjectKeys)
        {
            using var content = new MemoryStream();
            await _storage.UploadAsync(bucket, key, content, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task DownloadOutputsAsync(CloudJobRequest request, CancellationToken cancellationToken)
    {
        var bucket = await _storage.EnsureBucketAsync(request.Spec.ProjectId, cancellationToken).ConfigureAwait(false);
        foreach (var key in request.OutputObjectKeys)
        {
            var stream = await _storage.DownloadAsync(bucket, key, cancellationToken).ConfigureAwait(false);
            await stream.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// docs/cloud_design.md section 2's preflight order, steps 1-4 (step 5,
    /// bucket-exists, is <see cref="UploadInputsAsync"/>'s own
    /// <see cref="IStorageGateway.EnsureBucketAsync"/> call). Returns null on
    /// success; a project-wide failure here means the run never reaches
    /// Uploading at all, per the same rule <see cref="ProvisionAsync"/>
    /// enforces for a project-wide error discovered later, at create time.
    /// </summary>
    private async Task<(CloudErrorKind Kind, string Message)?> PreflightAsync(CloudJobRequest request, CancellationToken cancellationToken)
    {
        var projectId = request.Spec.ProjectId;

        var state = await _projectSetup.GetProjectStateAsync(projectId, cancellationToken).ConfigureAwait(false);
        if (state != ProjectLifecycleState.Active)
        {
            return (CloudErrorKind.Permission, $"Project '{projectId}' is not ACTIVE (state: {state}).");
        }

        if (!await _projectSetup.IsBillingEnabledAsync(projectId, cancellationToken).ConfigureAwait(false))
        {
            return (CloudErrorKind.Billing, $"Billing is not enabled on project '{projectId}'.");
        }

        if (!await _projectSetup.IsComputeApiEnabledAsync(projectId, cancellationToken).ConfigureAwait(false))
        {
            return (CloudErrorKind.ApiDisabled, $"The Compute Engine API is not enabled on project '{projectId}'.");
        }

        // THEORY (unverified) / known gap: VmSpec has no accelerator-type
        // field yet (only MachineType, e.g. "g2-standard-8"), so this passes
        // a placeholder rather than the real GPU type the zone ladder (#86)
        // will eventually resolve from RunOptions.GpuTier. FakeGcp's default
        // quota (1) makes this preflight step pass unless a test explicitly
        // scripts a quota for this exact placeholder string.
        const string PlaceholderAcceleratorType = "gpu";
        var quota = await _quota.GetGpuQuotaAsync(projectId, RegionOf(request.Zones[0]), PlaceholderAcceleratorType, cancellationToken).ConfigureAwait(false);
        if (quota <= 0)
        {
            return (CloudErrorKind.Quota, $"No GPU quota available in region '{RegionOf(request.Zones[0])}' for project '{projectId}'.");
        }

        return null;
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
    private async Task<ProvisionResult> ProvisionAsync(CloudJobRequest request, CancellationToken cancellationToken)
    {
        var existing = await _compute.FindByJobIdAsync(request.JobId, cancellationToken).ConfigureAwait(false);
        if (existing.Count > 0)
        {
            return ProvisionResult.Ok(existing[0].Zone);
        }

        foreach (var zone in request.Zones)
        {
            var outcome = await OperationPoller.PollAsync<VmDescriptor>(
                async pollToken =>
                {
                    try
                    {
                        var vm = await _compute.CreateVmAsync(request.Spec, zone, pollToken).ConfigureAwait(false);
                        return new OperationPoll<VmDescriptor>(true, vm, null);
                    }
                    catch (CloudOperationException ex)
                    {
                        return new OperationPoll<VmDescriptor>(true, null, ex.Error);
                    }
                },
                CreateVmDeadline,
                cancellationToken).ConfigureAwait(false);

            if (outcome.Success)
            {
                return ProvisionResult.Ok(zone);
            }

            var kind = CloudErrorClassifier.Classify(outcome.Error!);

            if (kind == CloudErrorKind.AlreadyExists)
            {
                // The exact resource this attempt would have created is
                // already there under someone else's create - adopt it
                // rather than treat this zone as failed.
                var vm = await _compute.GetVmAsync(request.Spec.VmName, zone, cancellationToken).ConfigureAwait(false);
                if (vm is not null)
                {
                    return ProvisionResult.Ok(zone);
                }
            }

            if (IsProjectWide(kind))
            {
                // docs/cloud_design.md section 5: billing/API/permission/org
                // policy abort the WHOLE ladder immediately - retrying a
                // different zone cannot fix a project-wide problem.
                return ProvisionResult.Failed(kind, outcome.Error!.Message);
            }

            // Quota / stockout / network / other: per-zone or per-region,
            // worth trying the next zone (CLAUDE.md: "quota is not stockout",
            // but neither one aborts the ladder).
        }

        return ProvisionResult.Failed(CloudErrorKind.Stockout, $"No zone in [{string.Join(", ", request.Zones)}] could provision '{request.Spec.VmName}'.");
    }

    /// <summary>Hard Rule 11: stop or delete per the request, then independently verify the VM actually reached that terminal state - never just trust the call succeeded.</summary>
    private async Task<string?> AfterTaskAsync(CloudJobRequest request, string zone, CancellationToken cancellationToken)
    {
        switch (request.AfterTask)
        {
            case AfterTaskAction.Delete:
                await _compute.DeleteVmAsync(request.Spec.VmName, zone, cancellationToken).ConfigureAwait(false);
                var afterDelete = await _compute.GetVmAsync(request.Spec.VmName, zone, cancellationToken).ConfigureAwait(false);
                return afterDelete is null
                    ? null
                    : $"VM '{request.Spec.VmName}' in zone '{zone}' was still present after Delete.";

            case AfterTaskAction.Stop:
                await _compute.StopVmAsync(request.Spec.VmName, zone, cancellationToken).ConfigureAwait(false);
                var afterStop = await _compute.GetVmAsync(request.Spec.VmName, zone, cancellationToken).ConfigureAwait(false);
                return afterStop is { Status: "STOPPED" }
                    ? null
                    : $"VM '{request.Spec.VmName}' in zone '{zone}' did not reach STOPPED after Stop (Hard Rule 11).";

            case AfterTaskAction.KeepAlive:
            default:
                // Hard Rule 11: "keep alive" always has an expiry, enforced
                // by whoever schedules the eventual stop/delete against
                // KeepAliveMinutes/AfterKeepAlive - not this method's job;
                // this runner does not leave a VM running with no expiry
                // recorded anywhere, it simply is not the thing that later
                // enforces that expiry.
                return null;
        }
    }

    private async Task SetPhaseAsync(string jobId, JobPhase phase, CancellationToken cancellationToken)
    {
        var current = await GetPhaseAsync(jobId, cancellationToken).ConfigureAwait(false);
        if (current == phase || JobStateMachine.HasAlreadyPassed(current, phase))
        {
            return;
        }

        JobStateMachine.EnsureLegalTransition(current, phase);

        // Hard Rule 16 discipline named in issue #58's own Done-when:
        // committed to the repository BEFORE the UI (onPhaseChanged) is told.
        await _runs.UpsertAsync(new RunRecord(jobId, phase, DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);
        _onPhaseChanged?.Invoke(jobId, phase);
    }

    private static JobPhase LatestPhase(IReadOnlyList<RunRecord> runs, string jobId)
    {
        var latest = JobPhase.Draft;
        var latestAt = DateTimeOffset.MinValue;
        foreach (var run in runs)
        {
            if (run.JobId == jobId && run.CreatedUtc >= latestAt)
            {
                latest = run.Phase;
                latestAt = run.CreatedUtc;
            }
        }

        return latest;
    }

    private static bool IsHealthyRunning(VmDescriptor? vm, out string? reason)
    {
        if (vm is null)
        {
            reason = "The VM could not be found.";
            return false;
        }

        if (vm.Status != "RUNNING")
        {
            reason = vm.StatusReason is { Length: > 0 }
                ? $"The VM is '{vm.Status}' ({vm.StatusReason})."
                : $"The VM is '{vm.Status}', not RUNNING.";
            return false;
        }

        reason = null;
        return true;
    }

    private static bool IsProjectWide(CloudErrorKind kind)
        => kind is CloudErrorKind.Billing or CloudErrorKind.ApiDisabled or CloudErrorKind.Permission or CloudErrorKind.OrgPolicy;

    /// <summary>
    /// A zone id's region is everything before its last <c>-&lt;letter&gt;</c>
    /// suffix (<c>us-central1-a</c> -&gt; <c>us-central1</c>). THEORY
    /// (unverified): this is the general Compute Engine zone-naming shape,
    /// not verified against a real zone list this session.
    /// </summary>
    private static string RegionOf(string zone)
    {
        var lastDash = zone.LastIndexOf('-');
        return lastDash > 0 ? zone[..lastDash] : zone;
    }

    private readonly struct ProvisionResult
    {
        private ProvisionResult(bool success, string? zone, CloudErrorKind? failureKind, string? failureMessage)
        {
            Success = success;
            Zone = zone;
            FailureKind = failureKind;
            FailureMessage = failureMessage;
        }

        public bool Success { get; }

        public string? Zone { get; }

        public CloudErrorKind? FailureKind { get; }

        public string? FailureMessage { get; }

        public static ProvisionResult Ok(string zone) => new(true, zone, null, null);

        public static ProvisionResult Failed(CloudErrorKind kind, string message) => new(false, null, kind, message);
    }
}
