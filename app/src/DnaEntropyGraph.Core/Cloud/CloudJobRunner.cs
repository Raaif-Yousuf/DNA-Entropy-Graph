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
    string? WorkerImage = null,
    CloudScope? Scope = null)
{
    /// <summary>
    /// Issue #830: the project and Google account every call of this run is made in. <see cref="JobEngine"/> captures it ONCE when the run
    /// is created; a request built without one (a test, or a legacy caller) acts in the spec's project as the current account (null sub).
    /// </summary>
    public CloudScope ResolvedScope => Scope ?? new CloudScope(Spec.ProjectId);
}

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
    private readonly IWorkerIdentityGateway _workerIdentity;
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

    /// <summary>Issue #91: the wait between two looks for a recorded Spot preemption (three looks).</summary>
    public TimeSpan PreemptionLookupInterval { get => _settings.PreemptionLookupInterval; init => _settings.PreemptionLookupInterval = value; }

    /// <summary>
    /// How long to wait for <c>result.json</c> after the VM is running. Null means <see cref="ResultWaitLimit"/>: the VM's own
    /// <c>maxRunDuration</c> less a short margin, measured from the VM's creation (its <c>maxRunDuration</c> clock), not from Running.
    /// </summary>
    public TimeSpan? ResultTimeout { get => _settings.ResultTimeout; init => _settings.ResultTimeout = value; }

    /// <summary>The clock the VM's age is read from (its <see cref="VmDescriptor.CreatedAt"/> is on the platform's clock).</summary>
    public TimeProvider TimeProvider { get => _settings.TimeProvider; init => _settings.TimeProvider = value; }

    /// <summary>Replaces the sleep between polls (tests only); see <see cref="CloudRunSettings.PollDelay"/>.</summary>
    public Func<TimeSpan, CancellationToken, Task>? PollDelay { get => _settings.PollDelay; init => _settings.PollDelay = value; }

    /// <summary>How long the worker's first heartbeat may take once the wait for the result begins (default 25 minutes: boot plus image pull).</summary>
    public TimeSpan FirstHeartbeatTimeout { get => _settings.FirstHeartbeatTimeout; init => _settings.FirstHeartbeatTimeout = value; }

    /// <summary>How long a worker heartbeat may stay unchanged before the run fails. Null means 20 times the manifest's heartbeat interval (600 s).</summary>
    public TimeSpan? HeartbeatStaleTimeout { get => _settings.HeartbeatStaleTimeout; init => _settings.HeartbeatStaleTimeout = value; }

    /// <summary>How long the worker's progress fields may stay unchanged while its heartbeat ticks in the running stage before the run fails as <c>worker_progress_stalled</c> (default 20 minutes, issue #723).</summary>
    public TimeSpan ProgressStallTimeout { get => _settings.ProgressStallTimeout; init => _settings.ProgressStallTimeout = value; }

    /// <summary>Per-stage deadlines replacing the defaults for the stages named (docs/job_contract.md section 5); null means the defaults.</summary>
    public IReadOnlyDictionary<string, TimeSpan>? StageDeadlines { get => _settings.StageDeadlines; init => _settings.StageDeadlines = value; }

    /// <summary>Issue #675: told when a stage is taking longer than usual (75 percent of its deadline) and when a stall question opens or is withdrawn. Called on the runner's own task: a UI must marshal. A handler that throws is ignored.</summary>
    public Action<RunNotice>? OnNotice { get => _settings.OnNotice; init => _settings.OnNotice = value; }

    /// <summary>Issue #938: how long after a cancel gave up on an in-flight create the app keeps watching to delete the VM that create makes when it lands (default 15 minutes).</summary>
    public TimeSpan LateCreateReapWindow { get => _settings.LateCreateReapWindow; init => _settings.LateCreateReapWindow = value; }

    /// <summary>Issue #938: told how the clean-up of a create that landed after a cancel ended. Called on a background task: a UI must marshal. A handler that throws is ignored.</summary>
    public Action<LateCreateOutcome>? OnLateCreate { get => _settings.OnLateCreate; init => _settings.OnLateCreate = value; }

    /// <summary>Issue #675: asked to choose Stop, Delete or Keep waiting when the heartbeat watch fires, instead of the VM being ended at once; see <see cref="StallDecision"/>. Null keeps the old behaviour.</summary>
    public StallDecision? OnStall { get => _settings.OnStall; init => _settings.OnStall = value; }

    /// <summary>Issue #675 review: how long a stall question may stay unanswered before it counts as no answer; see <see cref="CloudRunSettings.StallAnswerTimeout"/>.</summary>
    public TimeSpan? StallAnswerTimeout { get => _settings.StallAnswerTimeout; init => _settings.StallAnswerTimeout = value; }

    /// <summary>How long a cancel waits for the worker to show a sign of life after <c>control/cancel</c> is written (default 60 s, docs/job_contract.md section 6); zero turns the graceful step off.</summary>
    public TimeSpan CancelAckTimeout { get => _settings.CancelAckTimeout; init => _settings.CancelAckTimeout = value; }

    /// <summary>After the worker acknowledged a cancel, how long to wait for it to finish before the VM is deleted anyway (default 2 minutes).</summary>
    public TimeSpan CancelFinishTimeout { get => _settings.CancelFinishTimeout; init => _settings.CancelFinishTimeout = value; }

    /// <summary>The longest one gateway call may take before the runner treats it as a transient failure and looks again.</summary>
    public TimeSpan CallTimeout { get => _settings.CallTimeout; init => _settings.CallTimeout = value; }

    /// <summary>The longest <c>EnsureBucketAsync</c> may take: it is a sequence of requests, so it gets more than <see cref="CallTimeout"/> (see <see cref="CloudRunSettings.EnsureBucketTimeout"/>).</summary>
    public TimeSpan EnsureBucketTimeout { get => _settings.EnsureBucketTimeout; init => _settings.EnsureBucketTimeout = value; }

    /// <summary>How long to wait for a stop or delete to land (verifying the VM's terminal state) before recording it unverified.</summary>
    public TimeSpan LifecycleTimeout { get => _settings.LifecycleTimeout; init => _settings.LifecycleTimeout = value; }

    /// <summary>The budget for the whole worker-identity step (#606), which is many calls in one gateway method; see <see cref="CloudRunSettings.WorkerIdentityTimeout"/>.</summary>
    public TimeSpan WorkerIdentityTimeout { get => _settings.WorkerIdentityTimeout; init => _settings.WorkerIdentityTimeout = value; }

    /// <summary>The budget for the whole quota read (#88), which is several requests in one gateway method; see <see cref="CloudRunSettings.QuotaReadTimeout"/>.</summary>
    public TimeSpan QuotaReadTimeout { get => _settings.QuotaReadTimeout; init => _settings.QuotaReadTimeout = value; }

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
        IWorkerIdentityGateway workerIdentityGateway,
        IRunRepository runRepository)
        : this(computeGateway, storageGateway, projectSetupGateway, quotaGateway, workerIdentityGateway, runRepository, null)
    {
    }

    // An overload rather than a default value, so the container resolves one fixed signature.
    public CloudJobRunner(
        IComputeGateway computeGateway,
        IStorageGateway storageGateway,
        IProjectSetupGateway projectSetupGateway,
        IQuotaGateway quotaGateway,
        IWorkerIdentityGateway workerIdentityGateway,
        IRunRepository runRepository,
        Action<string, JobPhase>? onPhaseChanged)
    {
        _workerIdentity = workerIdentityGateway;
        // The collaborators share one settings object, so a timeout set by an object initializer after this constructor is
        // what every one of them reads. The in-flight create state has exactly one owner: _provisioner.
        _compute = computeGateway;
        _storage = storageGateway;
        _rows = new RunRowStore(runRepository, onPhaseChanged);
        _calls = new GatewayCalls(_settings, storageGateway);
        _preflight = new PreflightChecks(projectSetupGateway, quotaGateway, _calls, _settings);
        _transfer = new RunTransfer(storageGateway, _calls, _settings, _rows);
        _terminator = new VmTerminator(computeGateway, _calls, _settings);
        _provisioner = new VmProvisioner(computeGateway, _calls, _settings, _terminator, JobIsFinishedAsync, quotaGateway, BucketLocationAsync, ClaimZoneAsync);
        _canceller = new VmCanceller(_rows, _provisioner, _terminator, new GracefulCancel(computeGateway, storageGateway, _calls, _settings), _settings);
        _waiter = new ResultWaiter(computeGateway, _calls, _settings, _terminator);
        _outcome = new RunOutcomeRecorder(_rows, _transfer, _terminator);
    }

    /// <summary>
    /// Issue #94: whether the run that last used a stopped VM has ended (its row is terminal, or this history has no row for it, as the
    /// idle-VM sweep reads it). A history that cannot be read answers false: a VM that may belong to a run underway is never reused.
    /// </summary>
    private async Task<bool> JobIsFinishedAsync(string jobId)
    {
        try
        {
            var row = await _rows.LatestRecordAsync(jobId, CancellationToken.None).ConfigureAwait(false);
            return row is null || JobStateMachine.IsTerminal(row.Phase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>The location of the run's bucket (issue #670: the ladder prefers zones on its continent), or null when it is not known yet or cannot be read.</summary>
    private async Task<string?> BucketLocationAsync(RunProgress progress, CancellationToken cancellationToken)
    {
        if (progress.Bucket is not { } bucket || progress.Scope is not { } scope)
        {
            return null;
        }

        var buckets = await _calls.CallAsync(token => _storage.ListBucketsAsync(scope, token), cancellationToken).ConfigureAwait(false);
        return buckets.FirstOrDefault(b => string.Equals(b.Name, bucket, StringComparison.Ordinal))?.Location;
    }

    /// <summary>
    /// Issue #670: writes the zone this provision means the VM to be in (an empty zone clears the claim) to <c>jobs/&lt;jobId&gt;/control/zone</c>. A VM that boots in any other zone reads it (before
    /// it writes anything under the job prefix, <c>worker/vm/startup.sh</c>) and deletes itself, so an extra that outlives its deletion by a few
    /// seconds cannot overwrite the winner's status.json or log. A failure is the caller's to ignore: the extras are deleted by API regardless.
    /// </summary>
    private async Task ClaimZoneAsync(RunProgress progress, string zone, CancellationToken cancellationToken)
    {
        if (progress.Bucket is not { } bucket || progress.JobId is not { } jobId || progress.Scope is not { } scope)
        {
            return;
        }

        await _calls.CallAsync(
            async token =>
            {
                using var content = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(zone));
                await _storage.UploadAsync(scope, bucket, WorkerManifestBuilder.JobPrefix(jobId) + "control/zone", content, token).ConfigureAwait(false);
                return true;
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Tests: how many queued flex requests the runner still remembers.</summary>
    internal int PendingQueuedRequestCount => _provisioner.PendingFlexCount;

    /// <summary>Tests: how many per-job waiting-notice chains the runner still holds.</summary>
    internal int NoticeChainCount => _provisioner.NoticeChainCount;

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
            var done = await RunCoreAsync(request, cancellationToken).ConfigureAwait(false);
            if (JobStateMachine.IsTerminal(done.FinalPhase))
            {
                _provisioner.ForgetPendingRequests(request.JobId);
            }

            return done;
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
            _provisioner.ForgetPendingRequests(request.JobId);

            // Hard Rule 11: every run ends in a recorded terminal state. An
            // exception nothing classified (a gateway 403 outside the
            // create ladder, an illegal transition, a bad spec) must not
            // leave the row in whatever phase it last reached.
            var (kind, code) = ex switch
            {
                RunFailureException failure => (failure.Kind, failure.Code),
                CloudOperationException { Error.Code: RunErrorCodes.NotConnectedGatewayCode } cloud => (cloud.Kind, RunErrorCodes.CloudNotConnected),
                CloudOperationException cloud => (cloud.Kind, RunErrorCodes.For(cloud.Error, cloud.Kind)),
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
                await CancelAsync(request.ResolvedScope, request.JobId, cancellationToken).ConfigureAwait(false);
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
            // Issue #687: a cancel that arrived while this run was provisioning set the row to Cancelling; when the in-flight create or reuse
            // lands, this run's own next step is an illegal Cancelling -> Preparing transition. That is the cancel winning the race, not a
            // failure: the run stands down, CancelAsync (which waits for the in-flight create and deletes by label) owns the VM and records
            // the one terminal state, Cancelled.
            if (ex is InvalidOperationException && await _rows.TryLatestRecordAsync(request.JobId).ConfigureAwait(false) is { Phase: JobPhase.Cancelling or JobPhase.Cancelled } standDown)
            {
                return RunRowStore.ResultFromRow(standDown);
            }

            // Hard Rule 11: whatever fails once a VM may exist (a failed call, a bad transition, a failing repository),
            // the VM must not be left billing under a terminal Failed record nothing will ever look at again. The VM is
            // found by the zone when known and by its job-id label when not. Only the CALLER cancelling is not this (CancelAsync owns that
            // delete): an OperationCanceledException with the caller's token live is a gateway's own deadline, a failure like any other.
            var note = await _terminator.EndVmAfterFailureAsync(progress.Resolve(request), progress.Zone).ConfigureAwait(false);
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
        var bucket = await _calls.CallAsync(token => _storage.EnsureBucketAsync(request.ResolvedScope, token), cancellationToken, _settings.EnsureBucketTimeout).ConfigureAwait(false);

        progress.Bucket = bucket;
        progress.ProjectId = request.Spec.ProjectId;
        progress.Scope = request.ResolvedScope;
        progress.JobId = request.JobId;
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
                finished = await _calls.TryReadTextAsync(request.ResolvedScope, bucket, WorkerManifestBuilder.JobPrefix(request.JobId) + "result.json", cancellationToken).ConfigureAwait(false);
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
                var owned = await _calls.CallAsync(token => _compute.FindByJobIdAsync(request.ResolvedScope, request.JobId, token), cancellationToken).ConfigureAwait(false);
                if (owned.Count > 0)
                {
                    // A crash in the middle of a parallel batch can leave several: finish on one, delete and verify the rest (#670).
                    var (kept, extrasNote) = await _provisioner.KeepOneOwnedVmAsync(request, owned, cancellationToken).ConfigureAwait(false);
                    if (extrasNote is not null)
                    {
                        return await _outcome.FailAsync(request.JobId, CloudErrorKind.Other, RunErrorCodes.For(CloudErrorKind.Other), "More than one VM was found for this job and an extra could not be deleted.", extrasNote).ConfigureAwait(false);
                    }

                    owned = [kept];
                }

                progress.Zone = owned.Count > 0 ? owned[0].Zone : null;
                progress.VmName = owned.Count > 0 ? owned[0].Name : null;
                await RecordVmNameAsync(request.JobId, progress.VmName, cancellationToken).ConfigureAwait(false);
                return await _outcome.FinishAsync(progress.Resolve(request), bucket, progress.Zone, finished, cancellationToken).ConfigureAwait(false);
            }
        }

        // Issue #91: a Spot VM Google takes back is answered with exactly one automatic retry on an on-demand VM; the second attempt is never Spot, so it cannot loop.
        _terminator.ClearAppRequestedEnd(request.JobId);
        var attempt = request;
        while (true)
        {
            await _rows.SetPhaseAsync(request.JobId, JobPhase.Provisioning, cancellationToken).ConfigureAwait(false);
            var provisioned = await _provisioner.ProvisionAsync(attempt, token => CompleteSpecAsync(attempt, bucket, progress, token), progress, cancellationToken).ConfigureAwait(false);
            if (!provisioned.Success)
            {
                return await _outcome.FailAsync(request.JobId, provisioned.FailureKind!.Value, provisioned.FailureCode!, WorkerAccountDetail.Append(provisioned.FailureMessage, progress.WorkerAccountEmail), provisioned.VmEndNote).ConfigureAwait(false);
            }

            progress.Zone = provisioned.Zone!;
            progress.VmName = provisioned.VmName ?? progress.VmName;
            // No name from the provisioner or the progress means the run created the VM itself, under the planned name.
            await RecordVmNameAsync(request.JobId, progress.VmName ?? attempt.Spec.VmName, cancellationToken).ConfigureAwait(false);
            await RecordProvisioningModelAsync(request.JobId, attempt.Spec.Spot, cancellationToken).ConfigureAwait(false);
            try
            {
                return await RunOnVmAsync(progress.Resolve(attempt), bucket, progress.Zone, cancellationToken, progress.VmStartMark, progress.VmAdopted).ConfigureAwait(false);
            }
            catch (RunFailureException preempted) when (preempted.SpotPreempted && attempt.Spec.Spot && !_terminator.AppRequestedEnd(request.JobId))
            {
                // The lost VM was already deleted (and the delete confirmed: an unconfirmed end is never marked a preemption), so nothing bills and nothing is adopted below.
                // The preempted attempt's status and progress files go first: attempt 2 writes the same keys, and its watcher must never read attempt 1's heartbeat, stage or GPU line.
                var jobFolder = WorkerManifestBuilder.JobPrefix(request.JobId);
                foreach (var stale in new[] { "status.json", "progress.jsonl" })
                {
                    await _calls.CallAsync(
                        async token =>
                        {
                            await _storage.DeleteObjectAsync(request.ResolvedScope, bucket, jobFolder + stale, token).ConfigureAwait(false);
                            return true;
                        },
                        cancellationToken).ConfigureAwait(false);
                }

                await _rows.RestartProvisioningAsync(request.JobId, cancellationToken).ConfigureAwait(false);
                attempt = attempt with { Spec = attempt.Spec with { Spot = false, ExistingVmName = null } };
                progress.VmName = null;
                progress.Zone = null;
                progress.VmStartMark = null;
            }
        }
    }

    /// <summary>
    /// Issue #679: the run row names the VM the run actually used. A reused VM (#94) keeps the name of the job that made it, so the planned
    /// <c>deg-&lt;jobId&gt;</c> the row was written with is wrong; the diagnostics bundle and the Cloud page read this column. Only a name actually
    /// known is written: the planned one only for a VM this run created, never as a stand-in for an adopted VM or for none (a name nobody made). The write is a record, not a step:
    /// a failure here must not abandon a live VM, so it is swallowed and the run goes on to the wait and the VM's end.
    /// </summary>
    private async Task RecordVmNameAsync(string jobId, string? vmName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(vmName))
        {
            return;
        }

        try
        {
            await _rows.UpdateRowAsync(jobId, row => row with { VmName = vmName }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The row keeps the planned name; nothing about the VM changes.
        }
    }
    /// <summary>Issue #91: the run row says whether the VM this attempt is on is Spot (true, then false after the retry). A record, not a step: a failed write never abandons a live VM.</summary>
    private async Task RecordProvisioningModelAsync(string jobId, bool spot, CancellationToken cancellationToken)
    {
        try
        {
            await _rows.UpdateRowAsync(jobId, row => row with { IsSpot = spot }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The row keeps what it had; nothing about the VM changes.
        }
    }

    /// <summary>
    /// Issue #606: the spec to create. The provisioner asks for it only when no VM exists for the job, so a run that adopts a VM
    /// never calls IAM. The worker identity (the account the VM runs as, which may delete itself and write the bucket) is ensured
    /// here, once, before the first create; a refusal throws a classified CloudOperationException and no VM is made.
    /// </summary>
    private async Task<VmSpec> CompleteSpecAsync(CloudJobRequest request, string bucket, RunProgress progress, CancellationToken cancellationToken)
    {
        var identity = await _calls.CallAsync(token => _workerIdentity.EnsureWorkerIdentityAsync(request.ResolvedScope, bucket, token), cancellationToken, _settings.WorkerIdentityTimeout).ConfigureAwait(false);
        progress.WorkerAccountEmail = identity.ServiceAccountEmail;
        if (identity.NoteCode == SetupErrorCodes.WorkerDefaultAccount)
        {
            // A yellow note, not a failure: the run carries on as the project's default account and its row says so.
            await _rows.UpdateRowAsync(
                request.JobId,
                row => row with { ErrorCode = RunErrorCodes.WorkerDefaultAccount, ErrorDetail = SetupErrorCodes.WorkerDefaultAccount },
                cancellationToken).ConfigureAwait(false);
        }

        return VmProvisioner.WithStartupMetadata(request, bucket) with { ServiceAccountEmail = identity.ServiceAccountEmail };
    }

    /// <summary>Everything from the moment a VM exists. <see cref="RunCoreAsync"/> ends the VM for any exception that leaves here without having done so.</summary>
    private async Task<CloudJobResult> RunOnVmAsync(CloudJobRequest request, string bucket, string zone, CancellationToken cancellationToken, long? vmStartMark = null, bool adopted = false)
    {
        await _rows.SetPhaseAsync(request.JobId, JobPhase.Preparing, cancellationToken).ConfigureAwait(false);

        var (boot, bootReason) = await _waiter.WaitForBootAsync(request, zone, adopted, cancellationToken).ConfigureAwait(false);
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
            var done = await _calls.TryReadTextAsync(request.ResolvedScope, bucket, WorkerManifestBuilder.JobPrefix(request.JobId) + "result.json", cancellationToken).ConfigureAwait(false);
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
        var resultText = await _waiter.AwaitResultAsync(request, bucket, zone, cancellationToken, vmStartMark).ConfigureAwait(false);
        return await _outcome.FinishAsync(request, bucket, zone, resultText, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Cancels a run in progress: <see cref="JobPhase.Cancelling"/>, delete
    /// every VM this job's id currently owns (by label lookup, not a
    /// remembered zone - the runner that requested cancellation may not be
    /// the one that provisioned it), then <see cref="JobPhase.Cancelled"/>.
    /// </summary>
    public Task CancelAsync(CloudScope scope, string jobId, CancellationToken cancellationToken) => _canceller.CancelAsync(scope, jobId, cancellationToken);

    /// <summary>
    /// The run page's "Stop VM now": stops every VM this job id owns, found
    /// by label lookup rather than a remembered zone (Hard Rule 9). A job
    /// with no VM is a no-op. The run's own phase is not changed here: the
    /// next health check in <see cref="RunAsync"/> sees the stopped VM and
    /// records the failure, which keeps one writer per phase transition.
    /// </summary>
    public Task StopVmAsync(CloudScope scope, string jobId, CancellationToken cancellationToken) => _terminator.StopVmsAsync(scope, jobId, cancellationToken);

    /// <summary>The run page's "Delete VM now"; same lookup and no-op rules as <see cref="StopVmAsync"/>.</summary>
    public Task DeleteVmAsync(CloudScope scope, string jobId, CancellationToken cancellationToken) => _terminator.DeleteVmsAsync(scope, jobId, cancellationToken);

    /// <summary>
    /// Uploads the staged inputs and then <c>manifest.json</c> to <c>jobs/&lt;jobId&gt;/</c>. The manifest goes
    /// last so its presence means every input is already there. Safe to call again for the same job
    /// until a VM exists; <see cref="RunAsync"/> never calls it for a run past Uploading.
    /// </summary>
    public async Task UploadInputsAsync(CloudJobRequest request, CancellationToken cancellationToken)
    {
        var bucket = await _calls.CallAsync(token => _storage.EnsureBucketAsync(request.ResolvedScope, token), cancellationToken, _settings.EnsureBucketTimeout).ConfigureAwait(false);
        await _transfer.UploadAsync(request, bucket, cancellationToken).ConfigureAwait(false);
    }
}
