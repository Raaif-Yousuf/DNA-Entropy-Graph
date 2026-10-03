using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Contract;

namespace DnaEntropyGraph.Core.Cloud;

/// <summary>What the reconciler did with one run row.</summary>
public enum ReattachAction
{
    /// <summary>The run was handed back to <see cref="CloudJobRunner"/>, which carried it to the phase in <see cref="ReattachOutcome.FinalPhase"/>.</summary>
    Resumed,

    /// <summary>The run was mid-cancel; the cancel was finished.</summary>
    CancelFinished,

    /// <summary>The VM is gone and no <c>result.json</c> exists: recorded Failed. A lost VM is never silently replaced.</summary>
    FailedVmMissing,

    /// <summary>The request could not be rebuilt from the row (no project, unreadable options, no input copy, no worker image): recorded Failed with the code that names the action.</summary>
    FailedUnrecoverable,

    /// <summary>The cloud could not be asked (no connection, offline). The row is untouched and the next launch looks again.</summary>
    Deferred,

    /// <summary>Not a cloud run (a local-engine run has no cloud state to reattach).</summary>
    Skipped,
}

/// <summary>One row's outcome. <paramref name="FinalPhase"/> and <paramref name="ErrorCode"/> are what the row ended as, when it ended.</summary>
public sealed record ReattachOutcome(string JobId, ReattachAction Action, JobPhase? FinalPhase = null, string? ErrorCode = null);

/// <summary>
/// Issue #59 (docs/architecture.md section 6, "Crash-safe resumption"): on launch, every run a killed or closed app left in a
/// non-terminal phase is reattached. This class only DECIDES; the walking is the runner's. A run is handed back to
/// <see cref="CloudJobRunner.RunAsync"/>, which already resumes from the phase recorded in <see cref="IRunRepository"/>, so there
/// is exactly one resume path.
///
/// The decision table (VM state is read by job-id label, Hard Rule 9; the prototype's STOPPED means "start it" does not carry
/// over, because a stopped VM with no result is a dead job, not an idle one):
/// <list type="table">
/// <item><term>Cancelling</term><description>finish the cancel.</description></item>
/// <item><term>Draft, Validating, Uploading</term><description>no VM can exist yet: restart from the app's own copy of the input.</description></item>
/// <item><term>Provisioning, no VM, no result</term><description>the create may never have been sent: provision now.</description></item>
/// <item><term>Preparing to Downloading, VM found (any state)</term><description>the runner reads <c>result.json</c> first (done: download), else judges the VM: up and booting keeps polling, stopped or terminated without a result fails and is ended.</description></item>
/// <item><term>Preparing to Downloading, <c>result.json</c> present, no VM</term><description>download (the worker ended its own VM).</description></item>
/// <item><term>Preparing to Downloading, no VM, no result</term><description><see cref="ReattachAction.FailedVmMissing"/>.</description></item>
/// </list>
/// Only rows created before this reconciler was built are considered: a run the user starts after launch is owned by the engine.
/// Retroactive lifecycle enforcement (a VM that should be deleted but is only stopped, keep-alive expiry, idle stopped VMs) and the
/// reconnect trigger are the follow-up issue's; see docs/architecture.md section 6.
/// </summary>
public sealed class JobReconciler
{
    /// <summary>The longest one look at the cloud (find the VM, read result.json) may take before the run is deferred to the next launch.</summary>
    private static readonly TimeSpan LookupTimeout = TimeSpan.FromSeconds(30);

    private readonly CloudJobRunner _runner;
    private readonly IComputeGateway _compute;
    private readonly IStorageGateway _storage;
    private readonly IRunRepository _runs;
    private readonly IRunInputStore _inputs;
    private readonly IWorkerImageProvider _images;
    private readonly RunRowStore _rows;
    private readonly Func<string?> _downloadsFolder;
    private readonly ActiveRuns _active;
    private readonly DateTimeOffset _startedAt;

    /// <param name="onPhaseChanged">Told after the reconciler itself commits a phase (a run it fails); a run it resumes reports through the runner's own callback.</param>
    /// <param name="downloadsFolder">The default output parent for a run whose row names none.</param>
    public JobReconciler(
        CloudJobRunner runner,
        IComputeGateway compute,
        IStorageGateway storage,
        IRunRepository runs,
        IRunInputStore inputs,
        IWorkerImageProvider images,
        Action<string, JobPhase>? onPhaseChanged = null,
        Func<string?>? downloadsFolder = null,
        TimeProvider? timeProvider = null,
        ActiveRuns? activeRuns = null)
    {
        _active = activeRuns ?? new ActiveRuns();
        _runner = runner;
        _compute = compute;
        _storage = storage;
        _runs = runs;
        _inputs = inputs;
        _images = images;
        _rows = new RunRowStore(runs, onPhaseChanged);
        _downloadsFolder = downloadsFolder ?? (() => null);
        _startedAt = (timeProvider ?? TimeProvider.System).GetUtcNow();
    }

    /// <summary>Reattaches every non-terminal cloud run created before this reconciler existed, concurrently, and returns what happened to each once they have all ended.</summary>
    public async Task<IReadOnlyList<ReattachOutcome>> ReattachAsync(CancellationToken cancellationToken)
    {
        var all = await _runs.GetAllAsync(cancellationToken).ConfigureAwait(false);
        var candidates = all
            .Select(r => r.JobId)
            .Distinct()
            .Select(id => RunRowStore.LatestRecord(all, id)!)
            .Where(r => !JobStateMachine.IsTerminal(r.Phase) && r.CreatedUtc < _startedAt)
            .ToList();
        return await Task.WhenAll(candidates.Select(row => ReattachOneAsync(row, cancellationToken))).ConfigureAwait(false);
    }

    private async Task<ReattachOutcome> ReattachOneAsync(RunRecord row, CancellationToken cancellationToken)
    {
        try
        {
            return await DecideAndActAsync(row, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && cancellationToken.IsCancellationRequested))
        {
            // A failing repository or an unforeseen error must not stop the other runs from being reattached; the row is
            // left as it is and the next launch tries again.
            return new ReattachOutcome(row.JobId, ReattachAction.Deferred, null, ex.GetType().Name);
        }
    }

    private async Task<ReattachOutcome> DecideAndActAsync(RunRecord row, CancellationToken cancellationToken)
    {
        if (!string.Equals(row.Target, "cloud", StringComparison.OrdinalIgnoreCase))
        {
            return new ReattachOutcome(row.JobId, ReattachAction.Skipped);
        }

        if (row.Phase == JobPhase.Cancelling)
        {
            return await FinishCancelAsync(row, cancellationToken).ConfigureAwait(false);
        }

        if (string.IsNullOrWhiteSpace(row.ProjectId))
        {
            return await FailAsync(row, ReattachAction.FailedUnrecoverable, RunErrorCodes.NoProject, "The run row names no project.", cancellationToken).ConfigureAwait(false);
        }

        var options = RunOptionsJson.TryDeserialize(row.OptionsJson);
        if (options is null || string.IsNullOrWhiteSpace(row.InstallationId))
        {
            return await FailAsync(row, ReattachAction.FailedUnrecoverable, RunErrorCodes.Other, "The run row does not hold the options or installation id needed to resume it.", cancellationToken).ConfigureAwait(false);
        }

        var vmMayExist = row.Phase == JobPhase.Provisioning || JobStateMachine.HasAlreadyPassed(row.Phase, JobPhase.Provisioning);
        var imageRequired = true; // every phase before Provisioning provisions
        if (vmMayExist)
        {
            Evidence evidence;
            try
            {
                evidence = await LookAsync(row, cancellationToken).ConfigureAwait(false);
            }
            catch (CloudOperationException ex) when (IsNoAnswer(ex))
            {
                return new ReattachOutcome(row.JobId, ReattachAction.Deferred, null, ex.Error.Code);
            }
            catch (TimeoutException)
            {
                return new ReattachOutcome(row.JobId, ReattachAction.Deferred, null, "TIMEOUT");
            }

            if (evidence == Evidence.Nothing && row.Phase != JobPhase.Provisioning)
            {
                return await FailAsync(row, ReattachAction.FailedVmMissing, RunErrorCodes.VmUnhealthy, "The VM no longer exists and the worker left no result.json.", cancellationToken).ConfigureAwait(false);
            }

            // Only a VM or a result actually SEEN means the run is not provisioned again, so the worker image (which only a new
            // VM's startup script needs) is not required, and an app update that dropped the old image must not strand it.
            // A look that failed is not evidence: a run that could still provision needs its image, or it would create a VM
            // with no startup script that bills until maxRunDuration.
            imageRequired = evidence != Evidence.Found && row.Phase == JobPhase.Provisioning;
        }

        var staged = await FindInputAsync(row, options, cancellationToken).ConfigureAwait(false);
        if (staged is null && !vmMayExist)
        {
            return await FailAsync(row, ReattachAction.FailedUnrecoverable, RunErrorCodes.InputMissing, "No copy of the input remains under app data and the original is gone.", cancellationToken).ConfigureAwait(false);
        }

        var appVersion = row.AppVersion ?? "0.0.0";
        var image = _images.Resolve(appVersion, VmProvisioner.ExpectsGpu(CloudJobRequestFactory.MachineTypeFor(options.GpuTier)));
        if (imageRequired && (image.Status != WorkerImageStatus.Available || image.Reference is null))
        {
            var code = image.Status == WorkerImageStatus.OverrideRefused ? RunErrorCodes.WorkerImageRefused : RunErrorCodes.WorkerImageUnavailable;
            return await FailAsync(row, ReattachAction.FailedUnrecoverable, code, "No pinned worker image is available for this run's app version.", cancellationToken).ConfigureAwait(false);
        }

        var request = CloudJobRequestFactory.Create(
            options,
            row.JobId,
            row.ProjectId,
            row.InstallationId,
            appVersion,
            image.Status == WorkerImageStatus.Available ? image.Reference : null,
            [staged ?? new StagedInput(options.InputPath ?? string.Empty, Path.GetFileName(options.InputPath) is { Length: > 0 } name ? name : "input")],
            OutputParent(row, options));

        // Driven through the registry the engine's Cancel looks in, so cancelling a reattached run stops and awaits it first.
        CloudJobResult? result = null;
        var driver = _active.TryStart(row.JobId, async token => result = await _runner.RunAsync(request, token).ConfigureAwait(false));
        if (driver is null)
        {
            return new ReattachOutcome(row.JobId, ReattachAction.Skipped);
        }

        await driver.ConfigureAwait(false);
        if (result is not null)
        {
            return new ReattachOutcome(row.JobId, ReattachAction.Resumed, result.FinalPhase, result.FailureCode);
        }

        // The user cancelled it while it was being driven: the row says how that ended.
        var settled = await _rows.TryLatestRecordAsync(row.JobId).ConfigureAwait(false);
        return new ReattachOutcome(row.JobId, ReattachAction.Resumed, settled?.Phase, settled?.ErrorCode);
    }

    private enum Evidence
    {
        /// <summary>No VM carries the job's label and no result.json exists.</summary>
        Nothing,

        /// <summary>A VM carries the job's label, or result.json exists (the run has something to adopt or download).</summary>
        Found,

        /// <summary>The look was refused (billing, permission, org policy): neither a VM nor a result was seen. The runner judges it.</summary>
        Unknown,
    }

    /// <summary>
    /// Looks at the cloud once: a VM by job-id label, then (only when none) <c>result.json</c>. Throws a
    /// <see cref="CloudOperationException"/> the caller classifies, or <see cref="TimeoutException"/>. An error that is not "no answer"
    /// (billing, permission) is reported as <see cref="Evidence.Unknown"/> so the runner, which knows how to end a VM and name the
    /// failure, judges the run instead of this class guessing. A row with no recorded bucket never uploaded, so it has no result:
    /// nothing is created to look.
    /// </summary>
    private async Task<Evidence> LookAsync(RunRecord row, CancellationToken cancellationToken)
    {
        try
        {
            var vms = await WithDeadline(token => _compute.FindByJobIdAsync(row.JobId, token), cancellationToken).ConfigureAwait(false);
            if (vms.Count > 0)
            {
                return Evidence.Found;
            }

            if (string.IsNullOrWhiteSpace(row.Bucket))
            {
                return Evidence.Nothing;
            }

            var bucket = row.Bucket;
            var stream = await WithDeadline(token => _storage.TryDownloadAsync(bucket, WorkerManifestBuilder.JobPrefix(row.JobId) + "result.json", token), cancellationToken).ConfigureAwait(false);
            if (stream is null)
            {
                return Evidence.Nothing;
            }

            await stream.DisposeAsync().ConfigureAwait(false);
            return Evidence.Found;
        }
        catch (CloudOperationException ex) when (!IsNoAnswer(ex))
        {
            return Evidence.Unknown;
        }
    }

    private static async Task<T> WithDeadline<T>(Func<CancellationToken, Task<T>> call, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(LookupTimeout);
        try
        {
            return await call(deadline.Token).WaitAsync(LookupTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException();
        }
    }

    /// <summary>The cloud gave no answer about the run: nothing is connected, or the network is down. That says nothing about the run itself.</summary>
    private static bool IsNoAnswer(CloudOperationException ex)
        => ex.Error.Code == RunErrorCodes.NotConnectedGatewayCode || ex.Kind == CloudErrorKind.Network;

    private async Task<StagedInput?> FindInputAsync(RunRecord row, RunOptions options, CancellationToken cancellationToken)
    {
        try
        {
            var copy = await _inputs.FindAsync(row.JobId, cancellationToken).ConfigureAwait(false);
            if (copy is not null)
            {
                return copy;
            }

            // Hard Rule 14 keeps a copy of every input; a missing one falls back to the original, if it is still where it was.
            return string.IsNullOrWhiteSpace(options.InputPath) ? null : await _inputs.StageAsync(row.JobId, options.InputPath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private string OutputParent(RunRecord row, RunOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.OutputFolder))
        {
            return options.OutputFolder;
        }

        var existing = string.IsNullOrWhiteSpace(row.OutputDir) ? null : Path.GetDirectoryName(row.OutputDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return string.IsNullOrWhiteSpace(existing) ? RunOutputFolders.DefaultParent(_downloadsFolder) : existing;
    }

    private async Task<ReattachOutcome> FinishCancelAsync(RunRecord row, CancellationToken cancellationToken)
    {
        try
        {
            await _runner.CancelAsync(row.JobId, cancellationToken).ConfigureAwait(false);
        }
        catch (CloudOperationException ex) when (IsNoAnswer(ex))
        {
            return new ReattachOutcome(row.JobId, ReattachAction.Deferred, null, ex.Error.Code);
        }

        var after = await _rows.TryLatestRecordAsync(row.JobId).ConfigureAwait(false);
        return new ReattachOutcome(row.JobId, ReattachAction.CancelFinished, after?.Phase, after?.ErrorCode);
    }

    private async Task<ReattachOutcome> FailAsync(RunRecord row, ReattachAction action, string code, string detail, CancellationToken cancellationToken)
    {
        await _rows.SetPhaseAsync(row.JobId, JobPhase.Failed, cancellationToken, code, detail).ConfigureAwait(false);
        return new ReattachOutcome(row.JobId, action, JobPhase.Failed, code);
    }
}
