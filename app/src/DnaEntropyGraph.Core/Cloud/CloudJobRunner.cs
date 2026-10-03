using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
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
    /// <summary>How long one zone's create attempt may take to answer before it is abandoned.</summary>
    public TimeSpan CreateTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How long to wait for a create the runner gave up on to settle (land or fail) before the VM end is recorded unconfirmed.</summary>
    public TimeSpan CreateSettleTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>One upload's deadline. Null means a floor of 2 minutes plus 1 second per 128 KiB of the object (a slow 1 Mbit/s link still finishes).</summary>
    public TimeSpan? UploadTimeout { get; init; }

    /// <summary>How long a new VM may stay PROVISIONING or STAGING before the run gives up on it (job_contract.md section 5: booting 8 min).</summary>
    public TimeSpan BootTimeout { get; init; } = TimeSpan.FromMinutes(8);

    /// <summary>How long to wait between looks for the worker's <c>result.json</c>.</summary>
    public TimeSpan ResultPollInterval { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How long to wait for <c>result.json</c> after the VM is running. Null means
    /// <see cref="ResultWaitLimit"/>: the VM's own <c>maxRunDuration</c> less a short margin, measured from the VM's
    /// creation (its <c>maxRunDuration</c> clock), not from Running.
    /// </summary>
    public TimeSpan? ResultTimeout { get; init; }

    /// <summary>The clock the VM's age is read from (its <see cref="VmDescriptor.CreatedAt"/> is on the platform's clock).</summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    /// <summary>How long before the VM's own <c>maxRunDuration</c> the default <see cref="ResultTimeout"/> ends.</summary>
    private static readonly TimeSpan ResultMargin = TimeSpan.FromMinutes(3);

    /// <summary>The longest one gateway call may take before the runner treats it as a transient failure and looks again.</summary>
    public TimeSpan CallTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>How long to wait for a stop or delete to land (verifying the VM's terminal state) before recording it unverified.</summary>
    public TimeSpan LifecycleTimeout { get; init; } = TimeSpan.FromMinutes(3);

    /// <summary>How long to wait between looks at a VM that is stopping or being deleted.</summary>
    public TimeSpan LifecyclePollInterval { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>
    /// The default wait for result.json, measured from Running, for a VM with this <c>maxRunDuration</c>:
    /// the VM's own limit minus <see cref="ResultMargin"/>. Compute Engine deletes the VM at
    /// <c>maxRunDuration</c> (Hard Rule 10), so a wait that runs past it only ever finds a lost VM and
    /// <c>result_timeout</c> could never be recorded. Stopping a little earlier lets the runner give up on
    /// its own terms: one last look for the result, then end the VM per the user's choice and verify it.
    /// The worker's own limit is the same <c>maxRunDuration</c>, so a worker still uploading in the final
    /// margin loses that tail and the run fails as a timeout, which is the honest outcome.
    /// </summary>
    public static TimeSpan ResultWaitLimit(TimeSpan maxRunDuration)
        => maxRunDuration - TimeSpan.FromTicks(Math.Min(ResultMargin.Ticks, maxRunDuration.Ticks / 2));

    /// <summary>Largest <c>result.json</c> the app will read; a real one is a few KiB.</summary>
    private const int MaxResultChars = 4 * 1024 * 1024;

    private readonly IComputeGateway _compute;
    private readonly IStorageGateway _storage;
    private readonly IProjectSetupGateway _projectSetup;
    private readonly IQuotaGateway _quota;
    private readonly IRunRepository _runs;
    private readonly Action<string, JobPhase>? _onPhaseChanged;

    // Creates this instance has started and not seen finish, by task. A create the caller gave up on (deadline, cancel)
    // can still land afterwards; CancelAsync and the create ladder wait for these before they record a terminal state.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Task, string> _inflightCreates = new();

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
        _compute = computeGateway;
        _storage = storageGateway;
        _projectSetup = projectSetupGateway;
        _quota = quotaGateway;
        _runs = runRepository;
        _onPhaseChanged = onPhaseChanged;
    }

    public async Task<JobPhase> GetPhaseAsync(string jobId, CancellationToken cancellationToken)
    {
        var latest = await LatestRecordAsync(jobId, cancellationToken).ConfigureAwait(false);
        return latest?.Phase ?? JobPhase.Draft;
    }

    private async Task<RunRecord?> LatestRecordAsync(string jobId, CancellationToken cancellationToken)
    {
        var runs = await _runs.GetAllAsync(cancellationToken).ConfigureAwait(false);
        return LatestRecord(runs, jobId);
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
                await SetPhaseAsync(request.JobId, JobPhase.Failed, CancellationToken.None, code, ex.Message).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Already terminal, or the repository itself is failing. When the row is already in a different terminal
                // state (Completed, Cancelled) the result must say that, never Failed: the result agrees with the row.
                var settled = await TryLatestRecordAsync(request.JobId).ConfigureAwait(false);
                if (settled is not null && JobStateMachine.IsTerminal(settled.Phase) && settled.Phase != JobPhase.Failed)
                {
                    return ResultFromRow(settled);
                }
            }

            await RemoveEmptyRunFolderAsync(request.JobId).ConfigureAwait(false);
            return new CloudJobResult(JobPhase.Failed, kind, ex.Message, code);
        }
    }

    private async Task<CloudJobResult> RunCoreAsync(CloudJobRequest request, CancellationToken cancellationToken)
    {
        var latest = await LatestRecordAsync(request.JobId, cancellationToken).ConfigureAwait(false);
        var current = latest?.Phase ?? JobPhase.Draft;
        if (JobStateMachine.IsTerminal(current))
        {
            // The row's own result, error code included: a second RunAsync must say what the first one recorded.
            return ResultFromRow(latest!);
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
                var endNote = await EndVmAfterFailureAsync(request, null, forceDelete: true).ConfigureAwait(false);
                await RecordCancelOutcomeAsync(request.JobId, endNote is null ? null : $"{ex.Message}{endNote}").ConfigureAwait(false);
            }

            var after = await TryLatestRecordAsync(request.JobId).ConfigureAwait(false);
            return after is null ? new CloudJobResult(JobPhase.Cancelling) : ResultFromRow(after);
        }

        // A run resumed from Provisioning on may already own a VM (a create can have been accepted before the app died).
        var progress = new RunProgress { VmMayExist = current == JobPhase.Provisioning || JobStateMachine.HasAlreadyPassed(current, JobPhase.Provisioning) };
        try
        {
            return await RunStepsAsync(request, current, progress, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && cancellationToken.IsCancellationRequested) && progress.VmMayExist && !VmEndAlreadyAttempted(ex))
        {
            // Hard Rule 11: whatever fails once a VM may exist (a failed call, a bad transition, a failing repository),
            // the VM must not be left billing under a terminal Failed record nothing will ever look at again. The VM is
            // found by the zone when known and by its job-id label when not. Only the CALLER cancelling is not this (CancelAsync owns that
            // delete): an OperationCanceledException with the caller's token live is a gateway's own deadline, a failure like any other.
            var note = await EndVmAfterFailureAsync(request, progress.Zone).ConfigureAwait(false);
            throw ApplyVmNote(ex, note);
        }
    }

    /// <summary>The result a persisted terminal row stands for.</summary>
    private static CloudJobResult ResultFromRow(RunRecord row)
        => new(row.Phase, row.ErrorCode is null ? null : CloudErrorKind.Other, row.ErrorDetail, row.ErrorCode);

    private async Task<RunRecord?> TryLatestRecordAsync(string jobId)
    {
        try
        {
            return await LatestRecordAsync(jobId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>What the guard in <see cref="RunCoreAsync"/> needs to know about the VM, written by the steps as they learn it.</summary>
    private sealed class RunProgress
    {
        public bool VmMayExist { get; set; }

        public string? Zone { get; set; }
    }

    private async Task<CloudJobResult> RunStepsAsync(CloudJobRequest request, JobPhase current, RunProgress progress, CancellationToken cancellationToken)
    {
        var preflight = await PreflightAsync(request, cancellationToken).ConfigureAwait(false);
        if (preflight is not null)
        {
            var preflightNote = progress.VmMayExist ? await EndVmAfterFailureAsync(request, null).ConfigureAwait(false) : null;
            return await FailAsync(request.JobId, preflight.Value.Kind, RunErrorCodes.For(preflight.Value.Kind), preflight.Value.Message, preflightNote).ConfigureAwait(false);
        }

        await SetPhaseAsync(request.JobId, JobPhase.Validating, cancellationToken).ConfigureAwait(false);

        // Hard Rule 2's spirit: nobody pays for a VM to learn the output folder is unusable. The run's own folder is
        // made and proven writable before the bucket or any VM exists; the download reuses it.
        await PrepareRunFolderAsync(request, cancellationToken).ConfigureAwait(false);

        // Step 5 of the preflight order (docs/cloud_design.md section 2): the bucket exists.
        var bucket = await CallAsync(token => _storage.EnsureBucketAsync(request.Spec.ProjectId, token), cancellationToken).ConfigureAwait(false);

        await SetPhaseAsync(request.JobId, JobPhase.Uploading, cancellationToken).ConfigureAwait(false);

        // docs/job_contract.md section 3: the manifest is immutable once the worker may be reading
        // it, so a run resumed from a phase after Uploading never uploads again.
        if (!JobStateMachine.HasAlreadyPassed(current, JobPhase.Uploading))
        {
            await UploadAsync(request, bucket, cancellationToken).ConfigureAwait(false);
        }

        // A run resumed once a VM may exist may find the worker already finished. result.json is the worker's terminal
        // signal, so go straight to the download: never provision, never create a second billed VM for a job that is done.
        // A transient failure of this look is "not finished as far as we can tell": the run carries on and adopts the VM.
        if (progress.VmMayExist)
        {
            string? finished = null;
            try
            {
                finished = await TryReadTextAsync(bucket, WorkerManifestBuilder.JobPrefix(request.JobId) + "result.json", cancellationToken).ConfigureAwait(false);
            }
            catch (CloudOperationException ex) when (!IsProjectWide(ex.Kind))
            {
                // Carried on below.
            }

            if (finished is not null)
            {
                await SetPhaseAsync(request.JobId, JobPhase.Provisioning, cancellationToken).ConfigureAwait(false);
                await SetPhaseAsync(request.JobId, JobPhase.Preparing, cancellationToken).ConfigureAwait(false);
                await SetPhaseAsync(request.JobId, JobPhase.Running, cancellationToken).ConfigureAwait(false);
                var owned = await CallAsync(token => _compute.FindByJobIdAsync(request.JobId, token), cancellationToken).ConfigureAwait(false);
                progress.Zone = owned.Count > 0 ? owned[0].Zone : null;
                return await FinishAsync(request, bucket, progress.Zone, finished, cancellationToken).ConfigureAwait(false);
            }
        }

        await SetPhaseAsync(request.JobId, JobPhase.Provisioning, cancellationToken).ConfigureAwait(false);
        var spec = WithStartupMetadata(request, bucket);
        var provisioned = await ProvisionAsync(request, spec, progress, cancellationToken).ConfigureAwait(false);
        if (!provisioned.Success)
        {
            return await FailAsync(request.JobId, provisioned.FailureKind!.Value, RunErrorCodes.For(provisioned.FailureKind.Value), provisioned.FailureMessage, provisioned.VmEndNote).ConfigureAwait(false);
        }

        progress.Zone = provisioned.Zone!;
        return await RunOnVmAsync(request, bucket, progress.Zone, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Everything from the moment a VM exists. <see cref="RunCoreAsync"/> ends the VM for any exception that leaves here without having done so.</summary>
    private async Task<CloudJobResult> RunOnVmAsync(CloudJobRequest request, string bucket, string zone, CancellationToken cancellationToken)
    {
        await SetPhaseAsync(request.JobId, JobPhase.Preparing, cancellationToken).ConfigureAwait(false);

        var (boot, bootReason) = await WaitForBootAsync(request, zone, cancellationToken).ConfigureAwait(false);
        if (boot == BootOutcome.TimedOut)
        {
            // THEORY (unverified): instances.stop on an instance that is not RUNNING is rejected, and a VM that never
            // booted has no disk worth keeping, so it is deleted whatever the user chose for after a run.
            var note = await EndVmAfterFailureAsync(request, zone, forceDelete: true).ConfigureAwait(false);
            return await FailAsync(request.JobId, CloudErrorKind.Other, RunErrorCodes.VmUnhealthy, bootReason, note).ConfigureAwait(false);
        }

        if (boot == BootOutcome.Lost)
        {
            // A fast worker can finish and stop its VM between two looks, and a boot failure ends the VM without a
            // result.json: judge it exactly like a VM lost while waiting.
            var done = await TryReadTextAsync(bucket, WorkerManifestBuilder.JobPrefix(request.JobId) + "result.json", cancellationToken).ConfigureAwait(false);
            if (done is null)
            {
                throw await VmLostAsync(request, bucket, zone, bootReason, cancellationToken).ConfigureAwait(false);
            }

            await SetPhaseAsync(request.JobId, JobPhase.Running, cancellationToken).ConfigureAwait(false);
            return await FinishAsync(request, bucket, zone, done, cancellationToken).ConfigureAwait(false);
        }

        await SetPhaseAsync(request.JobId, JobPhase.Running, cancellationToken).ConfigureAwait(false);

        // RUNNING is not working (CLAUDE.md Critical Pitfalls): the VM being up says nothing about the
        // job. result.json is the worker's own terminal signal (docs/job_contract.md section 7), so wait
        // for it, checking on the VM between looks so a lost VM ends the run instead of waiting it out.
        var resultText = await AwaitResultAsync(request, bucket, zone, cancellationToken).ConfigureAwait(false);
        return await FinishAsync(request, bucket, zone, resultText, cancellationToken).ConfigureAwait(false);
    }

    private enum BootOutcome
    {
        Running,
        TimedOut,
        Lost,
    }

    /// <summary>
    /// Waits for a freshly created VM to read RUNNING. THEORY (unverified): a real instance reads PROVISIONING and then
    /// STAGING for a short while right after insert, so those are "still booting", polled until
    /// <see cref="BootTimeout"/>, never "lost". A transient failure of the look is retried the same way; only a failure
    /// of a class that aborts (billing, API off, permission, org policy) leaves here as an exception. A VM that is
    /// already stopped, gone or terminated is <see cref="BootOutcome.Lost"/>: the caller reads result.json and status.json.
    /// </summary>
    private async Task<(BootOutcome Outcome, string? Reason)> WaitForBootAsync(CloudJobRequest request, string zone, CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        string? lastTransient = null;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var vm = await CallAsync(token => _compute.GetVmAsync(request.Spec.VmName, zone, token), cancellationToken).ConfigureAwait(false);
                if (vm is { Status: "RUNNING" })
                {
                    return (BootOutcome.Running, null);
                }

                if (!IsBooting(vm))
                {
                    IsHealthyRunning(vm, out var reason);
                    return (BootOutcome.Lost, reason);
                }

                lastTransient = null;
            }
            catch (CloudOperationException ex) when (!IsProjectWide(ex.Kind))
            {
                lastTransient = ex.Message;
            }

            if (clock.Elapsed >= BootTimeout)
            {
                return (BootOutcome.TimedOut, $"The VM did not finish starting within {BootTimeout.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture)} s.{(lastTransient is null ? string.Empty : " The last look failed: " + lastTransient + ".")}");
            }

            await Task.Delay(ResultPollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>PROVISIONING and STAGING: the VM exists and is still starting. THEORY (unverified) for a real instance; see <see cref="WaitForBootAsync"/>.</summary>
    private static bool IsBooting(VmDescriptor? vm) => vm?.Status is "PROVISIONING" or "STAGING";

    /// <summary>The exception already went through an attempt to end the VM, so ending it again would only wait out the lifecycle deadline twice.</summary>
    private static bool VmEndAlreadyAttempted(Exception ex) => ex.Data.Contains(VmEndAttemptedKey);

    private const string VmEndAttemptedKey = "deg.vmEndAttempted";

    private static Exception MarkVmEndAttempted(Exception ex)
    {
        ex.Data[VmEndAttemptedKey] = true;
        return ex;
    }

    /// <summary>
    /// The exception to throw once an attempt to end the VM was made. When that end was not confirmed
    /// (<paramref name="note"/> is not null) the run is recorded under <see cref="RunErrorCodes.VmEndUnconfirmed"/>, whose copy
    /// sends the user to the Cloud page to delete the computer so it stops billing; the original code stays in the detail.
    /// </summary>
    private static Exception ApplyVmNote(Exception ex, string? note)
    {
        if (note is null)
        {
            return MarkVmEndAttempted(ex);
        }

        var (kind, code) = ex switch
        {
            RunFailureException failure => (failure.Kind, failure.Code),
            CloudOperationException cloud => (cloud.Kind, RunErrorCodes.For(cloud.Kind)),
            _ => (CloudErrorKind.Other, RunErrorCodes.Other),
        };
        return MarkVmEndAttempted(new RunFailureException(kind, RunErrorCodes.VmEndUnconfirmed, $"{ex.Message} Original code: {code}.{note}"));
    }

    /// <summary>
    /// From a <c>result.json</c> in hand: judge it, download what it lists, THEN end and verify the VM.
    /// The bucket outlives the VM, so the download never waits on the lifecycle (the worker has usually
    /// stopped or deleted its own VM by now), and a lifecycle check that fails after a good download
    /// records that on the finished run instead of discarding the results (Hard Rules 11 and 14).
    /// </summary>
    private async Task<CloudJobResult> FinishAsync(CloudJobRequest request, string bucket, string? zone, string resultText, CancellationToken cancellationToken)
    {
        WorkerResultDocument result;
        try
        {
            result = WorkerResultReader.Parse(resultText);
        }
        catch (InvalidDataException ex)
        {
            var note = await EndVmAfterFailureAsync(request, zone).ConfigureAwait(false);
            return await FailAsync(request.JobId, CloudErrorKind.Other, RunErrorCodes.WorkerFailed, "result.json could not be read: " + ex.Message, note).ConfigureAwait(false);
        }

        // A finished input must have a track: one that lists no files is not finished, whatever it says.
        var doneInputs = result.Inputs.Count(i => i.Status == "done" && i.Files.Count > 0);
        if (result.Status != "done" || doneInputs == 0)
        {
            // Partial results are always kept, never discarded (docs/job_contract.md section 6): a failed or cancelled
            // input lists the partial files it uploaded, so they are fetched before the run is recorded Failed.
            var keptPartial = false;
            if (result.Inputs.Any(i => i.Files.Count > 0))
            {
                try
                {
                    await DownloadAsync(request, bucket, result, cancellationToken).ConfigureAwait(false);
                    keptPartial = true;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // The failure itself is the run's story; the partial files stay in the bucket.
                }
            }

            var note = await EndVmAfterFailureAsync(request, zone).ConfigureAwait(false);
            var workerCode = result.ErrorCode ?? result.Inputs.FirstOrDefault(i => i.Status != "done")?.ErrorCode;
            return await FailAsync(
                request.JobId,
                CloudErrorKind.Other,
                RunErrorCodes.ForWorkerStatusCode(workerCode) ?? RunErrorCodes.WorkerFailed,
                DescribeWorkerFailure(result) + (keptPartial ? " Partial files were kept in the output folder." : string.Empty),
                note).ConfigureAwait(false);
        }

        await SetPhaseAsync(request.JobId, JobPhase.Finalizing, cancellationToken).ConfigureAwait(false);
        await SetPhaseAsync(request.JobId, JobPhase.Downloading, cancellationToken).ConfigureAwait(false);
        try
        {
            await DownloadAsync(request, bucket, result, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The results are still in the bucket; the VM must not keep billing while the user sorts out the folder.
            var downloadNote = await EndVmAfterFailureAsync(request, zone).ConfigureAwait(false);
            throw ApplyVmNote(ex, downloadNote);
        }

        var lifecycleError = request.AfterTask == AfterTaskAction.KeepAlive
            ? null
            : await EnsureVmEndedAsync(request, zone, request.AfterTask, cancellationToken).ConfigureAwait(false);

        var finalPhase = doneInputs == result.Inputs.Count ? JobPhase.Completed : JobPhase.PartiallyCompleted;
        if (lifecycleError is not null)
        {
            // A good result with an unconfirmed VM: finished, with the code that tells the user to check the Cloud page.
            await SetPhaseAsync(request.JobId, finalPhase, CancellationToken.None, RunErrorCodes.LifecycleUnverified, lifecycleError).ConfigureAwait(false);
            return new CloudJobResult(finalPhase, CloudErrorKind.Other, lifecycleError, RunErrorCodes.LifecycleUnverified);
        }

        await SetPhaseAsync(request.JobId, finalPhase, cancellationToken).ConfigureAwait(false);
        return new CloudJobResult(finalPhase);
    }

    /// <summary>
    /// Cancels a run in progress: <see cref="JobPhase.Cancelling"/>, delete
    /// every VM this job's id currently owns (by label lookup, not a
    /// remembered zone - the runner that requested cancellation may not be
    /// the one that provisioned it), then <see cref="JobPhase.Cancelled"/>.
    /// </summary>
    public async Task CancelAsync(string jobId, CancellationToken cancellationToken)
    {
        var existing = await LatestRecordAsync(jobId, cancellationToken).ConfigureAwait(false);
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
            await SetPhaseAsync(jobId, JobPhase.Cancelled, cancellationToken).ConfigureAwait(false);
            return;
        }

        await SetPhaseAsync(jobId, JobPhase.Cancelling, CancellationToken.None).ConfigureAwait(false);

        // A create the caller gave up on can still land. Wait for it (bounded) before looking, or the VM appears
        // after the run is recorded Cancelled and nothing ever deletes it.
        var settled = await SettleInflightCreatesAsync(jobId).ConfigureAwait(false);
        string? failure;
        try
        {
            failure = await DeleteOwnedVmsAndVerifyAsync(jobId, settled, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            failure = ex.Message;
        }
        catch (OperationCanceledException)
        {
            // The caller gave up on the cancel, but the run must not stay in Cancelling with a VM that may be billing: one
            // last attempt that the caller's token cannot cut, then the terminal state is recorded from what it finds.
            try
            {
                failure = await DeleteOwnedVmsAndVerifyAsync(jobId, settled, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                failure = ex.Message;
            }
        }

        // Hard Rule 11: a cancel ends in exactly one recorded terminal state. Failed names the VM to delete by hand.
        await RecordCancelOutcomeAsync(jobId, failure).ConfigureAwait(false);
    }

    /// <summary>
    /// Deletes every VM this job's label finds (a 404 means it is already gone: the worker or the platform got there first),
    /// then looks again and waits up to <see cref="LifecycleTimeout"/> for none to remain. Returns null when the VMs are
    /// confirmed gone, else why not.
    /// </summary>
    private async Task<string?> DeleteOwnedVmsAndVerifyAsync(string jobId, bool createsSettled, CancellationToken cancellationToken)
    {
        var owned = await CallAsync(token => _compute.FindByJobIdAsync(jobId, token), cancellationToken).ConfigureAwait(false);
        string? deleteError = null;
        foreach (var vm in owned)
        {
            // Every VM is attempted even when an earlier delete failed: the first failure is what is reported.
            try
            {
                await CallAsync(
                    async token =>
                    {
                        try
                        {
                            await _compute.DeleteVmAsync(vm.Name, vm.Zone, token).ConfigureAwait(false);
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

        var clock = Stopwatch.StartNew();
        while (true)
        {
            var remaining = await CallAsync(token => _compute.FindByJobIdAsync(jobId, token), cancellationToken).ConfigureAwait(false);
            if (remaining.Count == 0)
            {
                break;
            }

            if (clock.Elapsed >= LifecycleTimeout)
            {
                return $"{remaining.Count} VM(s) for this job were still there {LifecycleTimeout.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture)} s after Delete.";
            }

            await Task.Delay(LifecyclePollInterval, cancellationToken).ConfigureAwait(false);
        }

        return createsSettled
            ? null
            : $"A create request was still in flight after {CreateSettleTimeout.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture)} s, so a VM may still appear.";
    }

    /// <summary>
    /// Records how a cancel ended: Cancelled when the VMs are confirmed gone, else Failed under <see cref="RunErrorCodes.CancelFailed"/>.
    /// A row that is already terminal (a second cancel that raced the first) is left exactly as it is, and nothing here throws.
    /// </summary>
    private async Task RecordCancelOutcomeAsync(string jobId, string? failure)
    {
        try
        {
            var row = await LatestRecordAsync(jobId, CancellationToken.None).ConfigureAwait(false);
            if (row is not null && JobStateMachine.IsTerminal(row.Phase))
            {
                return;
            }

            if (failure is null)
            {
                await SetPhaseAsync(jobId, JobPhase.Cancelled, CancellationToken.None).ConfigureAwait(false);
            }
            else
            {
                await SetPhaseAsync(jobId, JobPhase.Failed, CancellationToken.None, RunErrorCodes.CancelFailed, failure).ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            // The repository itself is failing: nothing more can be recorded, and a throw here would only hide the run's state.
        }
    }

    /// <summary>
    /// The run page's "Stop VM now": stops every VM this job id owns, found
    /// by label lookup rather than a remembered zone (Hard Rule 9). A job
    /// with no VM is a no-op. The run's own phase is not changed here: the
    /// next health check in <see cref="RunAsync"/> sees the stopped VM and
    /// records the failure, which keeps one writer per phase transition.
    /// </summary>
    public async Task StopVmAsync(string jobId, CancellationToken cancellationToken)
    {
        var owned = await CallAsync(token => _compute.FindByJobIdAsync(jobId, token), cancellationToken).ConfigureAwait(false);
        foreach (var vm in owned)
        {
            await CallAsync(
                async token =>
                {
                    await _compute.StopVmAsync(vm.Name, vm.Zone, token).ConfigureAwait(false);
                    return true;
                },
                cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>The run page's "Delete VM now"; same lookup and no-op rules as <see cref="StopVmAsync"/>.</summary>
    public async Task DeleteVmAsync(string jobId, CancellationToken cancellationToken)
    {
        var owned = await CallAsync(token => _compute.FindByJobIdAsync(jobId, token), cancellationToken).ConfigureAwait(false);
        foreach (var vm in owned)
        {
            await CallAsync(
                async token =>
                {
                    await _compute.DeleteVmAsync(vm.Name, vm.Zone, token).ConfigureAwait(false);
                    return true;
                },
                cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Uploads the staged inputs and then <c>manifest.json</c> to <c>jobs/&lt;jobId&gt;/</c>. The manifest goes
    /// last so its presence means every input is already there. Safe to call again for the same job
    /// until a VM exists; <see cref="RunAsync"/> never calls it for a run past Uploading.
    /// </summary>
    public async Task UploadInputsAsync(CloudJobRequest request, CancellationToken cancellationToken)
    {
        var bucket = await CallAsync(token => _storage.EnsureBucketAsync(request.Spec.ProjectId, token), cancellationToken).ConfigureAwait(false);
        await UploadAsync(request, bucket, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// One upload's deadline: <see cref="UploadTimeout"/> when set, else a floor of 2 minutes plus 1 second per 128 KiB
    /// (a link as slow as 1 Mbit/s still finishes). A flat call deadline would cut every large input short.
    /// </summary>
    private TimeSpan UploadTimeoutFor(long bytes)
        => UploadTimeout ?? TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(bytes / (128.0 * 1024.0));

    private async Task UploadAsync(CloudJobRequest request, string bucket, CancellationToken cancellationToken)
    {
        var prefix = WorkerManifestBuilder.JobPrefix(request.JobId);
        for (var index = 0; index < request.Inputs.Count; index++)
        {
            var input = request.Inputs[index];
            FileStream content;
            try
            {
                // Seekable on purpose: the retry pipeline (issue #258) rewinds and replays it. Only the
                // OPEN is guarded; a failure inside the upload is a cloud error, not a missing input.
                content = new FileStream(input.LocalPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new RunFailureException(CloudErrorKind.Other, RunErrorCodes.InputMissing, $"The staged copy of input {index + 1} of {request.Inputs.Count} could not be opened: {ex.GetType().Name}.");
            }

            await using (content.ConfigureAwait(false))
            {
                await CallAsync(
                    async token =>
                    {
                        await _storage.UploadAsync(bucket, prefix + input.ObjectName, content, token).ConfigureAwait(false);
                        return true;
                    },
                    cancellationToken,
                    UploadTimeoutFor(content.Length)).ConfigureAwait(false);
            }
        }

        var manifest = WorkerManifestBuilder.Build(
            request.Options,
            request.JobId,
            request.Spec.InstallationId,
            request.Spec.AppVersion,
            request.WorkerImage,
            bucket,
            request.Inputs,
            DateTimeOffset.UtcNow);
        using (var manifestStream = new MemoryStream(Encoding.UTF8.GetBytes(manifest)))
        {
            await CallAsync(
                async token =>
                {
                    await _storage.UploadAsync(bucket, prefix + "manifest.json", manifestStream, token).ConfigureAwait(false);
                    return true;
                },
                cancellationToken,
                UploadTimeoutFor(manifestStream.Length)).ConfigureAwait(false);
        }

        // The row keeps where the job lives so the results can be fetched again while the objects exist.
        await UpdateRowAsync(
            request.JobId,
            row => row with { Bucket = bucket, JobPrefix = prefix, ManifestJson = manifest, WorkerImageDigest = request.WorkerImage },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Polls for <c>jobs/&lt;jobId&gt;/result.json</c> and returns its text. A transient failure (the network,
    /// a 5xx, an open breaker, a call that hung past <see cref="CallTimeout"/>) never ends the run: the VM
    /// and the worker carry on without the app, so it keeps looking until the deadline. Only the classes
    /// CLAUDE.md says abort (billing, API off, permission, org policy) end it early. Every way out but
    /// success ends the run through <see cref="RunFailureException"/>, with the VM ended per the user's
    /// choice first and the outcome of that recorded (Hard Rule 11).
    /// </summary>
    private async Task<string> AwaitResultAsync(CloudJobRequest request, string bucket, string zone, CancellationToken cancellationToken)
    {
        var key = WorkerManifestBuilder.JobPrefix(request.JobId) + "result.json";
        var limit = ResultTimeout ?? ResultWaitLimit(request.Spec.MaxRunDuration);
        var clock = Stopwatch.StartNew();
        string? lastTransient = null;

        // The VM's maxRunDuration started when Compute Engine created it, not when it reached Running: a boot of several
        // minutes, or a resume, has already used part of it. With the default limit the clock is moved forward by the VM's
        // age at the first look that reports a creation time (a gateway that reports none leaves it at "from Running").
        var elapsedBeforeClock = TimeSpan.Zero;
        var ageKnown = ResultTimeout is not null;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var text = await TryReadTextAsync(bucket, key, cancellationToken).ConfigureAwait(false);
                if (text is not null)
                {
                    return text;
                }

                var vm = await CallAsync(token => _compute.GetVmAsync(request.Spec.VmName, zone, token), cancellationToken).ConfigureAwait(false);
                lastTransient = null;
                if (!ageKnown && vm?.CreatedAt is { } createdAt)
                {
                    ageKnown = true;
                    var age = TimeProvider.GetUtcNow() - createdAt - clock.Elapsed;
                    elapsedBeforeClock = age > TimeSpan.Zero ? age : TimeSpan.Zero;
                }

                if (!IsHealthyRunning(vm, out var reason) && !IsBooting(vm))
                {
                    // A worker writes result.json BEFORE it stops its own VM, so look once more before
                    // calling a stopped VM lost.
                    text = await TryReadTextAsync(bucket, key, cancellationToken).ConfigureAwait(false);
                    if (text is not null)
                    {
                        return text;
                    }

                    throw await VmLostAsync(request, bucket, zone, reason, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (CloudOperationException ex) when (IsProjectWide(ex.Kind))
            {
                var abortNote = await EndVmAfterFailureAsync(request, zone).ConfigureAwait(false);
                throw ApplyVmNote(ex, abortNote);
            }
            catch (CloudOperationException ex)
            {
                lastTransient = ex.Message;
            }

            if (clock.Elapsed + elapsedBeforeClock >= limit)
            {
                var note = await EndVmAfterFailureAsync(request, zone).ConfigureAwait(false);
                var minutes = limit.TotalMinutes.ToString("0.#", CultureInfo.InvariantCulture);
                var last = lastTransient is null ? string.Empty : $" The last look failed: {lastTransient}.";
                throw ApplyVmNote(new RunFailureException(CloudErrorKind.Other, RunErrorCodes.ResultTimeout, $"No result.json after {minutes} minutes.{last}"), note);
            }

            await Task.Delay(ResultPollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The VM is gone or no longer running and no result.json exists. Boot-time failures
    /// (<c>worker/vm/startup.sh</c>: GPU_NOT_VISIBLE, IMAGE_PULL_FAILED, MANIFEST_INVALID, WORKER_CRASH) write
    /// <c>status.json</c> and end the VM without ever writing result.json, so the cause is read from there
    /// rather than reported as a lost computer.
    /// </summary>
    private async Task<Exception> VmLostAsync(CloudJobRequest request, string bucket, string zone, string? reason, CancellationToken cancellationToken)
    {
        var workerCode = await TryReadWorkerErrorCodeAsync(bucket, WorkerManifestBuilder.JobPrefix(request.JobId) + "status.json", cancellationToken).ConfigureAwait(false);
        var note = await EndVmAfterFailureAsync(request, zone).ConfigureAwait(false);
        var mapped = RunErrorCodes.ForWorkerStatusCode(workerCode);
        return ApplyVmNote(
            mapped is not null
                ? new RunFailureException(CloudErrorKind.Other, mapped, $"The worker reported {workerCode} in status.json and no result.json followed. {reason}")
                : new RunFailureException(CloudErrorKind.Other, RunErrorCodes.VmUnhealthy, (reason ?? "The VM is no longer running.") + (workerCode is null ? string.Empty : $" status.json reported {workerCode}.")),
            note);
    }

    /// <summary>The <c>error.code</c> in <c>status.json</c>, or null when it is absent, unreadable or the look itself failed (a diagnostic, never worth failing the run over).</summary>
    private async Task<string?> TryReadWorkerErrorCodeAsync(string bucket, string key, CancellationToken cancellationToken)
    {
        try
        {
            var text = await TryReadTextAsync(bucket, key, cancellationToken).ConfigureAwait(false);
            return text is null ? null : WorkerResultReader.TryReadStatusErrorCode(text);
        }
        catch (CloudOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// One gateway call with its own deadline (<see cref="CallTimeout"/>), so a call that never answers cannot
    /// hang the run. A deadline that passes while the caller is still waiting is reported the way the
    /// resilience pipeline reports one: a network failure. The caller cancelling still cancels.
    /// </summary>
    private async Task<T> CallAsync<T>(Func<CancellationToken, Task<T>> call, CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        var limit = timeout ?? CallTimeout;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(limit);
        Task<T>? task = null;
        try
        {
            task = call(deadline.Token);

            // WaitAsync, not just a token the callee may ignore: a call that never looks at its token (a stuck
            // socket under a library that does not check) must still be cut at the deadline.
            return await task.WaitAsync(limit, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            await deadline.CancelAsync().ConfigureAwait(false);
            if (task is not null)
            {
                // The abandoned call may still fault later; nobody is waiting to see it.
                _ = task.ContinueWith(static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }

            throw new CloudOperationException(new CloudError("TIMEOUT", null, "request timed out"), CloudErrorKind.Network);
        }
    }

    /// <summary>Reads a small object as text; null means it is not there. A body past <see cref="MaxResultChars"/> comes back cut at the limit, which no strict reader will accept as a result.</summary>
    private Task<string?> TryReadTextAsync(string bucket, string key, CancellationToken cancellationToken)
        => CallAsync(
            async token =>
            {
                var stream = await _storage.TryDownloadAsync(bucket, key, token).ConfigureAwait(false);
                if (stream is null)
                {
                    return null;
                }

                await using (stream.ConfigureAwait(false))
                {
                    using var reader = new StreamReader(stream, Encoding.UTF8);
                    var buffer = new char[MaxResultChars + 1];
                    var read = await reader.ReadBlockAsync(buffer.AsMemory(), token).ConfigureAwait(false);
                    return new string(buffer, 0, Math.Min(read, MaxResultChars));
                }
            },
            cancellationToken);

    /// <summary>
    /// Best effort, never throws: a run that failed or gave up must still end its VM (Hard Rule 11), by
    /// delete when the user chose delete and by stop otherwise. Returns null when the VM was confirmed
    /// ended, else a sentence for the run's detail saying it was not, so no message claims a stop nobody checked.
    /// </summary>
    private async Task<string?> EndVmAfterFailureAsync(CloudJobRequest request, string? zone, bool forceDelete = false)
    {
        var action = forceDelete ? AfterTaskAction.Delete : FailureEndAction(request);
        var error = zone is not null
            ? await EnsureVmEndedAsync(request, zone, action, CancellationToken.None).ConfigureAwait(false)
            : await EnsureVmsEndedByLabelAsync(request, action).ConfigureAwait(false);
        return error is null ? null : " (VM end not confirmed: " + error + ")";
    }

    /// <summary>Ends whatever VMs carry this job's label (Hard Rule 9), for a run that does not know the zone: a resume, or an insert that may have landed anywhere.</summary>
    private async Task<string?> EnsureVmsEndedByLabelAsync(CloudJobRequest request, AfterTaskAction action)
    {
        IReadOnlyList<VmDescriptor> owned;
        try
        {
            owned = await CallAsync(token => _compute.FindByJobIdAsync(request.JobId, token), CancellationToken.None).ConfigureAwait(false);
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
    /// What a failed run does to its VM: delete when the user chose delete, or chose keep-alive with delete afterwards
    /// (startup.sh applies afterKeepAlive at once for a run that did not succeed), otherwise stop.
    /// </summary>
    private static AfterTaskAction FailureEndAction(CloudJobRequest request) => request.AfterTask switch
    {
        AfterTaskAction.Delete => AfterTaskAction.Delete,
        AfterTaskAction.KeepAlive when request.Options.AfterKeepAlive == AfterKeepAliveAction.Delete => AfterTaskAction.Delete,
        _ => AfterTaskAction.Stop,
    };

    /// <summary>
    /// Hard Rule 11: end the VM per <paramref name="action"/>, then verify it independently, never trusting a
    /// call that returned. Tolerates a worker that already acted: a VM already gone is a delete done (and a stop
    /// done), a delete that finds nothing (404) is success, a VM <c>STOPPING</c> is polled to
    /// <see cref="LifecycleTimeout"/>, and both <c>STOPPED</c> and <c>TERMINATED</c> count as stopped.
    /// Returns null when the terminal state is confirmed, else why it is not. Never throws for a cloud
    /// failure: the caller decides what an unconfirmed VM means for the run.
    /// </summary>
    private async Task<string?> EnsureVmEndedAsync(CloudJobRequest request, string? zone, AfterTaskAction action, CancellationToken cancellationToken, string? vmName = null)
    {
        if (zone is null)
        {
            return null;
        }

        // A VM found by label is ended under its own name and zone, never the spec's.
        var name = vmName ?? request.Spec.VmName;
        try
        {
            var vm = await CallAsync(token => _compute.GetVmAsync(name, zone, token), cancellationToken).ConfigureAwait(false);
            if (vm is null)
            {
                return null;
            }

            if (action != AfterTaskAction.Delete && IsBooting(vm))
            {
                // THEORY (unverified): instances.stop on an instance that is not RUNNING is rejected, and a VM that never
                // reached RUNNING has no disk worth keeping: a failure that lands while it boots deletes it, as the boot
                // deadline does, whatever the user chose for after a run.
                action = AfterTaskAction.Delete;
            }

            if (action == AfterTaskAction.Delete)
            {
                await CallAsync(
                    async token =>
                    {
                        try
                        {
                            await _compute.DeleteVmAsync(name, zone, token).ConfigureAwait(false);
                        }
                        catch (CloudOperationException ex) when (ex.Error.HttpStatus == 404)
                        {
                            // Already deleted, by the worker or by the platform: the state we wanted.
                        }

                        return true;
                    },
                    cancellationToken).ConfigureAwait(false);
            }
            else if (!IsStopped(vm) && vm.Status != "STOPPING")
            {
                await CallAsync(
                    async token =>
                    {
                        try
                        {
                            await _compute.StopVmAsync(name, zone, token).ConfigureAwait(false);
                        }
                        catch (CloudOperationException ex) when (ex.Error.HttpStatus == 404)
                        {
                        }

                        return true;
                    },
                    cancellationToken).ConfigureAwait(false);
            }

            var clock = Stopwatch.StartNew();
            while (true)
            {
                vm = await CallAsync(token => _compute.GetVmAsync(name, zone, token), cancellationToken).ConfigureAwait(false);
                if (vm is null || (action != AfterTaskAction.Delete && IsStopped(vm)))
                {
                    return null;
                }

                if (clock.Elapsed >= LifecycleTimeout)
                {
                    return action == AfterTaskAction.Delete
                        ? $"VM '{name}' in zone '{zone}' was still '{vm.Status}' {LifecycleTimeout.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture)} s after Delete."
                        : $"VM '{name}' in zone '{zone}' was still '{vm.Status}' {LifecycleTimeout.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture)} s after Stop (Hard Rule 11).";
                }

                await Task.Delay(LifecyclePollInterval, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && cancellationToken.IsCancellationRequested))
        {
            return $"VM '{name}' in zone '{zone}' could not be confirmed {(action == AfterTaskAction.Delete ? "deleted" : "stopped")}: {ex.Message}";
        }
    }

    /// <summary>
    /// A stopped instance. THEORY (unverified): Compute Engine reports a stopped instance as <c>TERMINATED</c>
    /// (the API's name for it) and <c>STOPPED</c> is what this app's fake says; nothing here has measured which a
    /// real VM shows, so both count.
    /// </summary>
    private static bool IsStopped(VmDescriptor vm) => vm.Status is "STOPPED" or "TERMINATED";

    private static string DescribeWorkerFailure(WorkerResultDocument result)
    {
        var failed = result.Inputs.FirstOrDefault(i => i.Status != "done");
        var code = result.ErrorCode ?? failed?.ErrorCode ?? "unknown";

        // The worker's own message is free text that can carry a record name from the user's file; the code is enough
        // (the run record goes to SQLite and the diagnostics zip, CLAUDE.md "Logs").
        var emptyDone = result.Inputs.Any(i => i.Status == "done" && i.Files.Count == 0);
        return $"The worker reported status '{result.Status}' ({code}).{(emptyDone ? " A finished input listed no result files." : string.Empty)}";
    }

    /// <summary>
    /// Downloads every file the worker listed for an input that finished, into a fresh folder under
    /// the chosen output folder (Hard Rule 14), verifying each against the checksum <c>result.json</c>
    /// gives. A file lands under its final name only once complete and verified.
    /// </summary>
    private async Task DownloadAsync(CloudJobRequest request, string bucket, WorkerResultDocument result, CancellationToken cancellationToken)
    {
        var runFolder = await ResolveRunFolderAsync(request, cancellationToken).ConfigureAwait(false);
        var runFolderFull = Path.GetFullPath(runFolder);
        var prefix = WorkerManifestBuilder.JobPrefix(request.JobId);

        // Every input that listed files, whatever its status: a failed or cancelled input's partial files are kept
        // (docs/job_contract.md sections 6 and 7). Messages name the input id and file number, never the path, because
        // a path carries the user's file name and these messages are stored (CLAUDE.md "Logs").
        foreach (var input in result.Inputs.Where(i => i.Files.Count > 0))
        {
            for (var n = 0; n < input.Files.Count; n++)
            {
                var file = input.Files[n];
                var label = $"Result file {n + 1} of input {input.Id}";
                var relative = SafeRelativeOutputPath(file.Path, label);
                var destination = Path.GetFullPath(Path.Combine(runFolderFull, relative));
                if (!destination.StartsWith(runFolderFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    throw new RunFailureException(CloudErrorKind.Other, RunErrorCodes.WorkerFailed, $"{label} leaves the output folder.");
                }

                try
                {
                    await DownloadFileAsync(bucket, prefix + file.Path, destination, file, label, cancellationToken).ConfigureAwait(false);
                }
                catch (CloudOperationException ex) when (!IsProjectWide(ex.Kind))
                {
                    // The results are still in the bucket: this is "download again", not "start again".
                    throw new RunFailureException(ex.Kind, RunErrorCodes.DownloadFailed, $"{label} could not be fetched (HTTP status {ex.Error.HttpStatus?.ToString(CultureInfo.InvariantCulture) ?? "none"}).");
                }
            }
        }
    }

    private async Task DownloadFileAsync(string bucket, string objectKey, string destination, WorkerResultFile file, string label, CancellationToken cancellationToken)
    {
        var stream = await CallAsync(token => _storage.TryDownloadAsync(bucket, objectKey, token), cancellationToken).ConfigureAwait(false)
                     ?? throw new RunFailureException(CloudErrorKind.Other, RunErrorCodes.WorkerFailed, $"{label} is listed by the worker but is not in the bucket.");

        var partial = destination + ".part";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long written = 0;
            await using (stream.ConfigureAwait(false))
            await using (var target = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await CallAsync(token => stream.ReadAsync(buffer, token).AsTask(), cancellationToken).ConfigureAwait(false)) > 0)
                {
                    hash.AppendData(buffer, 0, read);
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    written += read;
                }
            }

            if (file.Bytes is { } expectedBytes && expectedBytes != written)
            {
                throw new RunFailureException(CloudErrorKind.Other, RunErrorCodes.DownloadCorrupt, $"{label} is {written} bytes, the worker said {expectedBytes}.");
            }

            if (file.Sha256 is { } expectedHash && !string.Equals(Convert.ToHexString(hash.GetHashAndReset()), expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new RunFailureException(CloudErrorKind.Other, RunErrorCodes.DownloadCorrupt, $"{label} does not match the checksum the worker reported.");
            }

            File.Move(partial, destination, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new RunFailureException(CloudErrorKind.Other, RunErrorCodes.DownloadFailed, $"{label} could not be written: {ex.GetType().Name}.");
        }
        finally
        {
            try
            {
                File.Delete(partial);
            }
            catch (Exception)
            {
                // Nothing more to do for a leftover partial; it never carries the final name.
            }
        }
    }

    /// <summary>
    /// A result path must be <c>output/&lt;relative path&gt;</c> with no empty, <c>.</c> or <c>..</c> segment
    /// and no drive or backslash, the same rule the worker's own blobstore enforces; the text comes from
    /// an object in a bucket and is never trusted as a local path.
    /// </summary>
    private static string SafeRelativeOutputPath(string path, string label)
    {
        const string Root = "output/";
        var invalid = Path.GetInvalidFileNameChars();
        if (!path.StartsWith(Root, StringComparison.Ordinal) || path.Contains('\\', StringComparison.Ordinal) || path.Contains(':', StringComparison.Ordinal))
        {
            throw new RunFailureException(CloudErrorKind.Other, RunErrorCodes.WorkerFailed, $"{label} has a path that is not an output path.");
        }

        var segments = path[Root.Length..].Split('/');
        if (segments.Any(seg => seg.Length == 0 || seg is "." or ".." || seg.IndexOfAny(invalid) >= 0))
        {
            throw new RunFailureException(CloudErrorKind.Other, RunErrorCodes.WorkerFailed, $"{label} has a path that is not a plain relative path.");
        }

        return string.Join(Path.DirectorySeparatorChar, segments);
    }

    /// <summary>
    /// The run's folder: the one already recorded on the row if it still exists (a resumed run), else a fresh one named
    /// after the first input. It is made, and proven writable, before anything is created in the cloud
    /// (<see cref="PrepareRunFolderAsync"/>); the download then finds it recorded and reuses it.
    /// </summary>
    private async Task<string> ResolveRunFolderAsync(CloudJobRequest request, CancellationToken cancellationToken)
    {
        var existing = (await LatestRecordAsync(request.JobId, cancellationToken).ConfigureAwait(false))?.OutputDir;
        if (!string.IsNullOrWhiteSpace(existing) && Directory.Exists(existing))
        {
            return existing;
        }

        string folder;
        try
        {
            folder = RunOutputFolders.CreateUnique(request.OutputFolder, Path.GetFileNameWithoutExtension(request.Inputs[0].FileName));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new RunFailureException(CloudErrorKind.Other, RunErrorCodes.OutputFolderUnusable, $"The output folder could not be created: {ex.GetType().Name}.");
        }

        await UpdateRowAsync(request.JobId, row => row with { OutputDir = folder }, cancellationToken).ConfigureAwait(false);
        return folder;
    }

    /// <summary>Creates the run folder and writes then deletes a probe file in it, so an unwritable folder fails here, before a VM is billed.</summary>
    private async Task PrepareRunFolderAsync(CloudJobRequest request, CancellationToken cancellationToken)
    {
        var folder = await ResolveRunFolderAsync(request, cancellationToken).ConfigureAwait(false);
        var probe = Path.Combine(folder, ".deg-write-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            await File.WriteAllBytesAsync(probe, [0], cancellationToken).ConfigureAwait(false);
            File.Delete(probe);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new RunFailureException(CloudErrorKind.Other, RunErrorCodes.OutputFolderUnusable, $"The output folder is not writable: {ex.GetType().Name}.");
        }
    }

    /// <summary>
    /// A run that ends Failed with nothing downloaded leaves no empty folder in the user's output folder. Only an EMPTY
    /// folder is ever removed (never a file, Hard Rule 14), and the row stops pointing at it.
    /// </summary>
    private async Task RemoveEmptyRunFolderAsync(string jobId)
    {
        try
        {
            var folder = (await LatestRecordAsync(jobId, CancellationToken.None).ConfigureAwait(false))?.OutputDir;
            if (!string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any())
            {
                Directory.Delete(folder, recursive: false);
                await UpdateRowAsync(jobId, row => row with { OutputDir = null }, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            // Clutter, not a correctness problem: the run's own failure is what gets recorded.
        }
    }

    /// <summary>Rewrites the latest row with <paramref name="change"/> applied, phase untouched. A job with no row yet has nothing to annotate.</summary>
    private async Task UpdateRowAsync(string jobId, Func<RunRecord, RunRecord> change, CancellationToken cancellationToken)
    {
        var existing = await LatestRecordAsync(jobId, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            await _runs.UpsertAsync(change(existing), cancellationToken).ConfigureAwait(false);
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

        var state = await CallAsync(token => _projectSetup.GetProjectStateAsync(projectId, token), cancellationToken).ConfigureAwait(false);
        if (state != ProjectLifecycleState.Active)
        {
            return (CloudErrorKind.Permission, $"Project '{projectId}' is not ACTIVE (state: {state}).");
        }

        if (!await CallAsync(token => _projectSetup.IsBillingEnabledAsync(projectId, token), cancellationToken).ConfigureAwait(false))
        {
            return (CloudErrorKind.Billing, $"Billing is not enabled on project '{projectId}'.");
        }

        if (!await CallAsync(token => _projectSetup.IsComputeApiEnabledAsync(projectId, token), cancellationToken).ConfigureAwait(false))
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
        var quota = await CallAsync(token => _quota.GetGpuQuotaAsync(projectId, RegionOf(request.Zones[0]), PlaceholderAcceleratorType, token), cancellationToken).ConfigureAwait(false);
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
    private async Task<ProvisionResult> ProvisionAsync(CloudJobRequest request, VmSpec spec, RunProgress progress, CancellationToken cancellationToken)
    {
        var existing = await CallAsync(token => _compute.FindByJobIdAsync(request.JobId, token), cancellationToken).ConfigureAwait(false);
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
                        createTask = _compute.CreateVmAsync(spec, zone, pollToken);
                        TrackInflightCreate(request.JobId, createTask);
                        var vm = await createTask.WaitAsync(CreateTimeout, pollToken).ConfigureAwait(false);
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
                CreateTimeout,
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
                var vm = await CallAsync(token => _compute.GetVmAsync(request.Spec.VmName, zone, token), cancellationToken).ConfigureAwait(false);
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
                var sweepNote = await EndVmAfterFailureAsync(request, null, forceDelete: true).ConfigureAwait(false);
                if (sweepNote is not null)
                {
                    return ProvisionResult.Failed(kind, outcome.Error!.Message, sweepNote);
                }
            }

            if (IsProjectWide(kind) || kind == CloudErrorKind.Network)
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

    /// <summary>Waits, bounded by <see cref="CreateSettleTimeout"/>, for every create this instance started for the job to land or fail. False when one is still in flight.</summary>
    private async Task<bool> SettleInflightCreatesAsync(string jobId)
    {
        var pending = _inflightCreates.Where(kv => kv.Value == jobId).Select(kv => kv.Key).ToList();
        if (pending.Count == 0)
        {
            return true;
        }

        var all = Task.WhenAll(pending);
        var winner = await Task.WhenAny(all, Task.Delay(CreateSettleTimeout)).ConfigureAwait(false);
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

    /// <summary>
    /// After a create timed out: wait for it to settle, then delete whatever landed (a VM that never ran has no disk
    /// worth keeping). Returns null when nothing is left, else a note that the VM end is not confirmed.
    /// </summary>
    private async Task<string?> SettleAbandonedCreateAsync(CloudJobRequest request, Task createTask)
    {
        var winner = await Task.WhenAny(createTask, Task.Delay(CreateSettleTimeout)).ConfigureAwait(false);
        if (winner != createTask)
        {
            return $" (VM end not confirmed: a create request was still in flight after {CreateSettleTimeout.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture)} s and could not be cancelled)";
        }

        return await EndVmAfterFailureAsync(request, null, forceDelete: true).ConfigureAwait(false);
    }

    /// <summary>
    /// Issue #261: the spec to create, with the startup script and the
    /// <c>deg-*</c> attributes attached once the bucket name is known. A
    /// request with no worker image creates the VM without them (nothing
    /// would run the job; the caller owns supplying an image). An invalid
    /// value throws here, before any VM exists, and <see cref="RunAsync"/>
    /// records the run as Failed.
    /// </summary>
    private static VmSpec WithStartupMetadata(CloudJobRequest request, string bucket)
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

    /// <summary>The GPU machine families (G2 = L4, A2 = A100, A3 = H100); anything else is a CPU smoke VM.</summary>
    public static bool ExpectsGpu(string machineType)
        => machineType.StartsWith("g2-", StringComparison.Ordinal)
           || machineType.StartsWith("a2-", StringComparison.Ordinal)
           || machineType.StartsWith("a3-", StringComparison.Ordinal);

    private async Task<CloudJobResult> FailAsync(string jobId, CloudErrorKind kind, string code, string? detail, string? vmEndNote = null)
    {
        if (vmEndNote is not null)
        {
            // The VM may still be billing: that is what the user must hear, the original cause stays in the detail.
            detail = $"{detail} Original code: {code}.{vmEndNote}";
            code = RunErrorCodes.VmEndUnconfirmed;
        }

        // CancellationToken.None: recording the terminal state must not itself be cancellable.
        await SetPhaseAsync(jobId, JobPhase.Failed, CancellationToken.None, code, detail).ConfigureAwait(false);
        await RemoveEmptyRunFolderAsync(jobId).ConfigureAwait(false);
        return new CloudJobResult(JobPhase.Failed, kind, detail, code);
    }

    private async Task SetPhaseAsync(string jobId, JobPhase phase, CancellationToken cancellationToken, string? errorCode = null, string? errorDetail = null)
    {
        if (phase is not (JobPhase.Failed or JobPhase.Cancelled or JobPhase.Cancelling))
        {
            // A cancelled run must not walk on to the next happy-path phase
            // just because a call that ignores its token happened to return.
            cancellationToken.ThrowIfCancellationRequested();
        }

        var existing = await LatestRecordAsync(jobId, cancellationToken).ConfigureAwait(false);
        var current = existing?.Phase ?? JobPhase.Draft;
        if (current == phase || JobStateMachine.HasAlreadyPassed(current, phase))
        {
            return;
        }

        JobStateMachine.EnsureLegalTransition(current, phase);

        // The repository replaces every column on upsert, so start from the
        // row the engine wrote first (name, options, project, installation
        // id...) and change only the phase; a fresh blank record would wipe
        // them on the first phase change.
        var now = DateTimeOffset.UtcNow;
        var next = existing is null
            ? new RunRecord(jobId, phase, now)
            : existing with { Phase = phase };
        if (JobStateMachine.IsTerminal(phase))
        {
            next = next with { FinishedAt = now };
        }

        if (errorCode is not null)
        {
            next = next with { ErrorCode = errorCode, ErrorDetail = errorDetail };
        }

        // Hard Rule 16 discipline named in issue #58's own Done-when:
        // committed to the repository BEFORE the UI (onPhaseChanged) is told.
        await _runs.UpsertAsync(next, cancellationToken).ConfigureAwait(false);
        try
        {
            _onPhaseChanged?.Invoke(jobId, phase);
        }
        catch (Exception)
        {
            // The row is committed. A notification is not part of the run: a callback that throws (a window already
            // closed) must never fail the run, end a kept-alive VM, or make the result disagree with the row.
        }
    }

    private static RunRecord? LatestRecord(IReadOnlyList<RunRecord> runs, string jobId)
    {
        RunRecord? latest = null;
        foreach (var run in runs)
        {
            if (run.JobId == jobId && (latest is null || run.CreatedUtc >= latest.CreatedUtc))
            {
                latest = run;
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

    /// <summary>A failure this runner already classified; <see cref="RunAsync"/> records it as the run's terminal state with exactly this code.</summary>
    private sealed class RunFailureException(CloudErrorKind kind, string code, string message) : Exception(message)
    {
        public CloudErrorKind Kind { get; } = kind;

        public string Code { get; } = code;
    }

    private readonly struct ProvisionResult
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
}
