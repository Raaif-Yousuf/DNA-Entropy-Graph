using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Contract;

namespace DnaEntropyGraph.Core.Cloud;

/// <summary>
/// Everything one cloud run needs that is not itself a gateway call: the
/// job id, the VM to request, the candidate zones to try in order (a
/// walking-skeleton zone list, not the full escalating-parallelism ladder -
/// that is issue #86), the staged input files to upload (issue #460), the
/// folder the results are downloaded into, and what to do with
/// the VM once the run finishes (Hard Rule 11 default: <see cref="AfterTaskAction.Stop"/>).
/// <c>Options</c> is what the manifest is written from; the output files are not
/// listed here because only the worker's <c>result.json</c> knows what it produced.
/// <c>WorkerImage</c> is the digest-pinned worker image reference; when set,
/// the runner attaches the startup script and its attributes to the VM
/// (<see cref="StartupMetadata"/>, issue #261).
/// </summary>
public sealed record CloudJobRequest(
    string JobId,
    VmSpec Spec,
    IReadOnlyList<string> Zones,
    RunOptions Options,
    IReadOnlyList<StagedInput> Inputs,
    string OutputFolder,
    AfterTaskAction AfterTask = AfterTaskAction.Stop,
    string? WorkerImage = null);

/// <summary>The phase a run ended in, plus - when it did not reach a happy terminal phase - which class of error stopped it and why.</summary>
/// <param name="FailureKind">Which class of error stopped the run.</param>
/// <param name="FailureMessage">Raw diagnostic text for logs and the run row's detail field. Never shown to a user: the user sees the <see cref="RunErrorCodes"/> message for <paramref name="FailureCode"/> (Hard Rule 13).</param>
/// <param name="FailureCode">A <see cref="RunErrorCodes"/> code; the UI maps it to a resource string that names one action.</param>
public sealed record CloudJobResult(JobPhase FinalPhase, CloudErrorKind? FailureKind = null, string? FailureMessage = null, string? FailureCode = null);

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
/// runner instance for the same job id, and <c>VmProvisioner.ProvisionAsync</c>
/// always calls <see cref="IComputeGateway.FindByJobIdAsync"/> BEFORE
/// attempting any zone (issue #257) - so calling <see cref="RunAsync"/>
/// again for the same job id, on a brand-new <see cref="CloudJobRunner"/>
/// sharing the same gateways and repository, is the reconciler this
/// issue's observable describes: "constructing a new reconciler resumes the
/// same job to Completed in the fake."
/// </summary>
public sealed class CloudJobRunner
{
    private readonly CloudRunSettings _settings = new();
    private readonly RunRowStore _rows;
    private readonly GatewayCalls _calls;
    private readonly IComputeGateway _compute;
    private readonly IStorageGateway _storage;
    private readonly PreflightChecks _preflight;
    private readonly RunTransfer _transfer;
    private readonly VmTerminator _terminator;
    private readonly VmProvisioner _provisioner;
    private readonly VmCanceller _canceller;
    private readonly ResultWaiter _waiter;
    private readonly RunOutcomeRecorder _outcome;

    /// <summary>How long one zone's create attempt may take to answer before it is abandoned.</summary>
    public TimeSpan CreateTimeout { get => _settings.CreateTimeout; init => _settings.CreateTimeout = value; }

    /// <summary>How long to wait for a create the runner gave up on to settle (land or fail) before the VM end is recorded unconfirmed.</summary>
    public TimeSpan CreateSettleTimeout { get => _settings.CreateSettleTimeout; init => _settings.CreateSettleTimeout = value; }

    /// <summary>One upload's deadline. Null means a floor of 2 minutes plus 1 second per 128 KiB of the object (a slow 1 Mbit/s link still finishes).</summary>
    public TimeSpan? UploadTimeout { get => _settings.UploadTimeout; init => _settings.UploadTimeout = value; }

    /// <summary>How long a new VM may stay PROVISIONING or STAGING before the run gives up on it (job_contract.md section 5: booting 8 min).</summary>
    public TimeSpan BootTimeout { get => _settings.BootTimeout; init => _settings.BootTimeout = value; }

    /// <summary>How long to wait between looks for the worker's <c>result.json</c>.</summary>
    public TimeSpan ResultPollInterval { get => _settings.ResultPollInterval; init => _settings.ResultPollInterval = value; }

    /// <summary>
    /// How long to wait for <c>result.json</c> after the VM is running. Null means <see cref="ResultWaitLimit"/>: the VM's own
    /// <c>maxRunDuration</c> less a short margin, measured from the VM's creation (its <c>maxRunDuration</c> clock), not from Running.
    /// </summary>
    public TimeSpan? ResultTimeout { get => _settings.ResultTimeout; init => _settings.ResultTimeout = value; }

    /// <summary>The clock the VM's age is read from (its <see cref="VmDescriptor.CreatedAt"/> is on the platform's clock).</summary>
    public TimeProvider TimeProvider { get => _settings.TimeProvider; init => _settings.TimeProvider = value; }

    /// <summary>The longest one gateway call may take before the runner treats it as a transient failure and looks again.</summary>
    public TimeSpan CallTimeout { get => _settings.CallTimeout; init => _settings.CallTimeout = value; }

    /// <summary>How long to wait for a stop or delete to land (verifying the VM's terminal state) before recording it unverified.</summary>
    public TimeSpan LifecycleTimeout { get => _settings.LifecycleTimeout; init => _settings.LifecycleTimeout = value; }

    /// <summary>How long to wait between looks at a VM that is stopping or being deleted.</summary>
    public TimeSpan LifecyclePollInterval { get => _settings.LifecyclePollInterval; init => _settings.LifecyclePollInterval = value; }

    /// <summary>The default wait for result.json for a VM with this <c>maxRunDuration</c>: the VM's own limit minus a short margin (see <c>ResultWaiter</c>).</summary>
    public static TimeSpan ResultWaitLimit(TimeSpan maxRunDuration) => ResultWaiter.ResultWaitLimit(maxRunDuration);

    /// <summary>The GPU machine families (G2 = L4, A2 = A100, A3 = H100); anything else is a CPU smoke VM.</summary>
    public static bool ExpectsGpu(string machineType) => VmProvisioner.ExpectsGpu(machineType);

    public CloudJobRunner(
        IComputeGateway computeGateway,
        IStorageGateway storageGateway,
        IProjectSetupGateway projectSetupGateway,
        IQuotaGateway quotaGateway,
        IRunRepository runRepository)
        : this(computeGateway, storageGateway, projectSetupGateway, quotaGateway, runRepository, null)
    {
    }

    // An overload rather than a default value, so the container resolves one fixed signature.
    public CloudJobRunner(
        IComputeGateway computeGateway,
        IStorageGateway storageGateway,
        IProjectSetupGateway projectSetupGateway,
        IQuotaGateway quotaGateway,
        IRunRepository runRepository,
        Action<string, JobPhase>? onPhaseChanged)
    {
        // The collaborators share one settings object, so a timeout set by an object initializer after this constructor is
        // what every one of them reads. The in-flight create state has exactly one owner: _provisioner.
        _compute = computeGateway;
        _storage = storageGateway;
        _rows = new RunRowStore(runRepository, onPhaseChanged);
        _calls = new GatewayCalls(_settings, storageGateway);
        _preflight = new PreflightChecks(projectSetupGateway, quotaGateway, _calls);
        _transfer = new RunTransfer(storageGateway, _calls, _settings, _rows);
        _terminator = new VmTerminator(computeGateway, _calls, _settings);
        _provisioner = new VmProvisioner(computeGateway, _calls, _settings, _terminator);
        _canceller = new VmCanceller(_rows, _provisioner, _terminator);
        _waiter = new ResultWaiter(computeGateway, _calls, _settings, _terminator);
        _outcome = new RunOutcomeRecorder(_rows, _transfer, _terminator);
    }

    public async Task<JobPhase> GetPhaseAsync(string jobId, CancellationToken cancellationToken)
    {
        var latest = await _rows.LatestRecordAsync(jobId, cancellationToken).ConfigureAwait(false);
        return latest?.Phase ?? JobPhase.Draft;
    }

    /// <summary>
    /// Runs (or resumes) <paramref name="request"/> to a terminal phase.
    /// Safe to call more than once for the same job id - see this class's
    /// own doc comment on crash-and-resume.
    /// </summary>
    public async Task<CloudJobResult> RunAsync(CloudJobRequest request, CancellationToken cancellationToken)
    {
        try
        {
            return await RunCoreAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The CALLER cancelled (CancelAsync records Cancelling/Cancelled);
            // not a failure to record here. An OperationCanceledException
            // while the token is live (an HttpClient or gRPC deadline) is
            // not the caller cancelling and falls through to the handler
            // below, so the run still ends in a terminal state.
            throw;
        }
        catch (Exception ex)
        {
            // Hard Rule 11: every run ends in a recorded terminal state. An
            // exception nothing classified (a gateway 403 outside the
            // create ladder, an illegal transition, a bad spec) must not
            // leave the row in whatever phase it last reached.
            var (kind, code) = ex switch
            {
                RunFailureException failure => (failure.Kind, failure.Code),
                CloudOperationException { Error.Code: RunErrorCodes.NotConnectedGatewayCode } cloud => (cloud.Kind, RunErrorCodes.CloudNotConnected),
                CloudOperationException cloud => (cloud.Kind, RunErrorCodes.For(cloud.Kind)),
                OperationCanceledException => (CloudErrorKind.Network, RunErrorCodes.For(CloudErrorKind.Network)),
                _ => (CloudErrorKind.Other, RunErrorCodes.For(CloudErrorKind.Other)),
            };
            try
            {
                await _rows.SetPhaseAsync(request.JobId, JobPhase.Failed, CancellationToken.None, code, ex.Message).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Already terminal, or the repository itself is failing. When the row is already in a different terminal
                // state (Completed, Cancelled) the result must say that, never Failed: the result agrees with the row.
                var settled = await _rows.TryLatestRecordAsync(request.JobId).ConfigureAwait(false);
                if (settled is not null && JobStateMachine.IsTerminal(settled.Phase) && settled.Phase != JobPhase.Failed)
                {
                    return RunRowStore.ResultFromRow(settled);
                }
            }

            await _transfer.RemoveEmptyRunFolderAsync(request.JobId).ConfigureAwait(false);
            return new CloudJobResult(JobPhase.Failed, kind, ex.Message, code);
        }
    }

    private async Task<CloudJobResult> RunCoreAsync(CloudJobRequest request, CancellationToken cancellationToken)
    {
        var latest = await _rows.LatestRecordAsync(request.JobId, cancellationToken).ConfigureAwait(false);
        var current = latest?.Phase ?? JobPhase.Draft;
        if (JobStateMachine.IsTerminal(current))
        {
            // The row's own result, error code included: a second RunAsync must say what the first one recorded.
            return RunRowStore.ResultFromRow(latest!);
        }

        if (current == JobPhase.Cancelling)
        {
            // The app died halfway through a cancel: finishing it is the only thing a resume may do. Walking on from
            // Cancelling would be an illegal transition, and would leave the VM the cancel was deleting alive.
            try
            {
                await CancelAsync(request.JobId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException && cancellationToken.IsCancellationRequested))
            {
                // CancelAsync failed before its own delete logic (a busy repository read). Never Failed/other with the VM
                // unlooked-at: end it here (best effort, by label, never throws) and record what that found. A repository
                // that is still failing leaves the row Cancelling, which is not terminal, so the next resume retries.
                var endNote = await _terminator.EndVmAfterFailureAsync(request, null, forceDelete: true).ConfigureAwait(false);
                await _canceller.RecordCancelOutcomeAsync(request.JobId, endNote is null ? null : $"{ex.Message}{endNote}").ConfigureAwait(false);
            }

            var after = await _rows.TryLatestRecordAsync(request.JobId).ConfigureAwait(false);
            return after is null ? new CloudJobResult(JobPhase.Cancelling) : RunRowStore.ResultFromRow(after);
        }

        // A run resumed from Provisioning on may already own a VM (a create can have been accepted before the app died).
        var progress = new RunProgress { VmMayExist = current == JobPhase.Provisioning || JobStateMachine.HasAlreadyPassed(current, JobPhase.Provisioning) };
        try
        {
            return await RunStepsAsync(request, current, progress, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && cancellationToken.IsCancellationRequested) && progress.VmMayExist && !VmEndNotes.AlreadyAttempted(ex))
        {
            // Hard Rule 11: whatever fails once a VM may exist (a failed call, a bad transition, a failing repository),
            // the VM must not be left billing under a terminal Failed record nothing will ever look at again. The VM is
            // found by the zone when known and by its job-id label when not. Only the CALLER cancelling is not this (CancelAsync owns that
            // delete): an OperationCanceledException with the caller's token live is a gateway's own deadline, a failure like any other.
            var note = await _terminator.EndVmAfterFailureAsync(request, progress.Zone).ConfigureAwait(false);
            throw VmEndNotes.Apply(ex, note);
        }
    }

    private async Task<CloudJobResult> RunStepsAsync(CloudJobRequest request, JobPhase current, RunProgress progress, CancellationToken cancellationToken)
    {
        var preflight = await _preflight.RunAsync(request, cancellationToken).ConfigureAwait(false);
        if (preflight is not null)
        {
            var preflightNote = progress.VmMayExist ? await _terminator.EndVmAfterFailureAsync(request, null).ConfigureAwait(false) : null;
            return await _outcome.FailAsync(request.JobId, preflight.Value.Kind, RunErrorCodes.For(preflight.Value.Kind), preflight.Value.Message, preflightNote).ConfigureAwait(false);
        }

        await _rows.SetPhaseAsync(request.JobId, JobPhase.Validating, cancellationToken).ConfigureAwait(false);

        // Hard Rule 2's spirit: nobody pays for a VM to learn the output folder is unusable. The run's own folder is
        // made and proven writable before the bucket or any VM exists; the download reuses it.
        await _transfer.PrepareRunFolderAsync(request, cancellationToken).ConfigureAwait(false);

        // Step 5 of the preflight order (docs/cloud_design.md section 2): the bucket exists.
        var bucket = await _calls.CallAsync(token => _storage.EnsureBucketAsync(request.Spec.ProjectId, token), cancellationToken).ConfigureAwait(false);

        await _rows.SetPhaseAsync(request.JobId, JobPhase.Uploading, cancellationToken).ConfigureAwait(false);

        // docs/job_contract.md section 3: the manifest is immutable once the worker may be reading
        // it, so a run resumed from a phase after Uploading never uploads again.
        if (!JobStateMachine.HasAlreadyPassed(current, JobPhase.Uploading))
        {
            await _transfer.UploadAsync(request, bucket, cancellationToken).ConfigureAwait(false);
        }

        // A run resumed once a VM may exist may find the worker already finished. result.json is the worker's terminal
        // signal, so go straight to the download: never provision, never create a second billed VM for a job that is done.
        // A transient failure of this look is "not finished as far as we can tell": the run carries on and adopts the VM.
        if (progress.VmMayExist)
        {
            string? finished = null;
            try
            {
                finished = await _calls.TryReadTextAsync(bucket, WorkerManifestBuilder.JobPrefix(request.JobId) + "result.json", cancellationToken).ConfigureAwait(false);
            }
            catch (CloudOperationException ex) when (!VmFacts.IsProjectWide(ex.Kind))
            {
                // Carried on below.
            }

            if (finished is not null)
            {
                await _rows.SetPhaseAsync(request.JobId, JobPhase.Provisioning, cancellationToken).ConfigureAwait(false);
                await _rows.SetPhaseAsync(request.JobId, JobPhase.Preparing, cancellationToken).ConfigureAwait(false);
                await _rows.SetPhaseAsync(request.JobId, JobPhase.Running, cancellationToken).ConfigureAwait(false);
                var owned = await _calls.CallAsync(token => _compute.FindByJobIdAsync(request.JobId, token), cancellationToken).ConfigureAwait(false);
                progress.Zone = owned.Count > 0 ? owned[0].Zone : null;
                return await _outcome.FinishAsync(request, bucket, progress.Zone, finished, cancellationToken).ConfigureAwait(false);
            }
        }

        await _rows.SetPhaseAsync(request.JobId, JobPhase.Provisioning, cancellationToken).ConfigureAwait(false);
        var spec = VmProvisioner.WithStartupMetadata(request, bucket);
        var provisioned = await _provisioner.ProvisionAsync(request, spec, progress, cancellationToken).ConfigureAwait(false);
        if (!provisioned.Success)
        {
            return await _outcome.FailAsync(request.JobId, provisioned.FailureKind!.Value, RunErrorCodes.For(provisioned.FailureKind.Value), provisioned.FailureMessage, provisioned.VmEndNote).ConfigureAwait(false);
        }

        progress.Zone = provisioned.Zone!;
        return await RunOnVmAsync(request, bucket, progress.Zone, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Everything from the moment a VM exists. <see cref="RunCoreAsync"/> ends the VM for any exception that leaves here without having done so.</summary>
    private async Task<CloudJobResult> RunOnVmAsync(CloudJobRequest request, string bucket, string zone, CancellationToken cancellationToken)
    {
        await _rows.SetPhaseAsync(request.JobId, JobPhase.Preparing, cancellationToken).ConfigureAwait(false);

        var (boot, bootReason) = await _waiter.WaitForBootAsync(request, zone, cancellationToken).ConfigureAwait(false);
        if (boot == BootOutcome.TimedOut)
        {
            // THEORY (unverified): instances.stop on an instance that is not RUNNING is rejected, and a VM that never
            // booted has no disk worth keeping, so it is deleted whatever the user chose for after a run.
            var note = await _terminator.EndVmAfterFailureAsync(request, zone, forceDelete: true).ConfigureAwait(false);
            return await _outcome.FailAsync(request.JobId, CloudErrorKind.Other, RunErrorCodes.VmUnhealthy, bootReason, note).ConfigureAwait(false);
        }

        if (boot == BootOutcome.Lost)
        {
            // A fast worker can finish and stop its VM between two looks, and a boot failure ends the VM without a
            // result.json: judge it exactly like a VM lost while waiting.
            var done = await _calls.TryReadTextAsync(bucket, WorkerManifestBuilder.JobPrefix(request.JobId) + "result.json", cancellationToken).ConfigureAwait(false);
            if (done is null)
            {
                throw await _waiter.VmLostAsync(request, bucket, zone, bootReason, cancellationToken).ConfigureAwait(false);
            }

            await _rows.SetPhaseAsync(request.JobId, JobPhase.Running, cancellationToken).ConfigureAwait(false);
            return await _outcome.FinishAsync(request, bucket, zone, done, cancellationToken).ConfigureAwait(false);
        }

        await _rows.SetPhaseAsync(request.JobId, JobPhase.Running, cancellationToken).ConfigureAwait(false);

        // RUNNING is not working (CLAUDE.md Critical Pitfalls): the VM being up says nothing about the
        // job. result.json is the worker's own terminal signal (docs/job_contract.md section 7), so wait
        // for it, checking on the VM between looks so a lost VM ends the run instead of waiting it out.
        var resultText = await _waiter.AwaitResultAsync(request, bucket, zone, cancellationToken).ConfigureAwait(false);
        return await _outcome.FinishAsync(request, bucket, zone, resultText, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Cancels a run in progress: <see cref="JobPhase.Cancelling"/>, delete
    /// every VM this job's id currently owns (by label lookup, not a
    /// remembered zone - the runner that requested cancellation may not be
    /// the one that provisioned it), then <see cref="JobPhase.Cancelled"/>.
    /// </summary>
    public Task CancelAsync(string jobId, CancellationToken cancellationToken) => _canceller.CancelAsync(jobId, cancellationToken);

    /// <summary>
    /// The run page's "Stop VM now": stops every VM this job id owns, found
    /// by label lookup rather than a remembered zone (Hard Rule 9). A job
    /// with no VM is a no-op. The run's own phase is not changed here: the
    /// next health check in <see cref="RunAsync"/> sees the stopped VM and
    /// records the failure, which keeps one writer per phase transition.
    /// </summary>
    public Task StopVmAsync(string jobId, CancellationToken cancellationToken) => _terminator.StopVmsAsync(jobId, cancellationToken);

    /// <summary>The run page's "Delete VM now"; same lookup and no-op rules as <see cref="StopVmAsync"/>.</summary>
    public Task DeleteVmAsync(string jobId, CancellationToken cancellationToken) => _terminator.DeleteVmsAsync(jobId, cancellationToken);

    /// <summary>
    /// Uploads the staged inputs and then <c>manifest.json</c> to <c>jobs/&lt;jobId&gt;/</c>. The manifest goes
    /// last so its presence means every input is already there. Safe to call again for the same job
    /// until a VM exists; <see cref="RunAsync"/> never calls it for a run past Uploading.
    /// </summary>
    public async Task UploadInputsAsync(CloudJobRequest request, CancellationToken cancellationToken)
    {
        var bucket = await _calls.CallAsync(token => _storage.EnsureBucketAsync(request.Spec.ProjectId, token), cancellationToken).ConfigureAwait(false);
        await _transfer.UploadAsync(request, bucket, cancellationToken).ConfigureAwait(false);
    }
}
