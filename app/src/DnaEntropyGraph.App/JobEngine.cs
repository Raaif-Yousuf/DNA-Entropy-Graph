using System.Reflection;
using CommunityToolkit.Mvvm.Messaging;
using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Cloud;
using DnaEntropyGraph.Core.Contract;
using DnaEntropyGraph.Core.Cost;
using DnaEntropyGraph.Core.Inputs;
using DnaEntropyGraph.Core.LocalEngine;
using DnaEntropyGraph.Core.Runs;
using DnaEntropyGraph.Persistence;
using DnaEntropyGraph.Presentation.Messaging;
using DnaEntropyGraph.Presentation.Services;

namespace DnaEntropyGraph.App;

/// <summary>
/// Owns the active runs and is the one place the UI's "Run", "Cancel",
/// "Stop VM now" and "Delete VM now" reach the cloud (issue #428). A cloud run
/// is written to <see cref="IRunRepository"/> up front, then handed to
/// <see cref="CloudJobRunner"/> on a background task; the runner records
/// every phase and its phase callback (wired in <c>ServiceRegistration</c>)
/// publishes <see cref="RunPhaseChangedMessage"/> over <see cref="IMessenger"/>
/// (docs/architecture.md section 3), so any page can react with no direct
/// reference back to this class. A This PC run
/// (issues #173 and #174) is handed to <see cref="LocalRunDriver"/> and <c>LocalJobRunner</c>; <see cref="IRunTargetResolver"/> decides
/// where Auto goes, and an explicit This PC is never quietly switched to the cloud.
/// </summary>
public sealed class JobEngine : IJobEngine, IRunVmActions
{
    private readonly ActiveRuns _activeRuns;
    private readonly IMessenger _messenger;
    private readonly CloudJobRunner _runner;
    private readonly IGcpAccount _account;
    private readonly ISettingsStore _settings;
    private readonly IRunRepository _runs;
    private readonly IProjectSettingsStore _projects;
    private readonly IRunInputStore _inputs;
    private readonly IWorkerImageProvider _images;
    private readonly IAccessionLookup _accessionLookup;
    private readonly Func<string?> _downloadsFolder;
    private readonly ICostEstimateService? _estimates;
    private readonly IRunTargetResolver _targets;
    private readonly LocalRunDriver _local;

    public JobEngine(
        IMessenger messenger,
        CloudJobRunner runner,
        IGcpAccount account,
        ISettingsStore settings,
        IRunRepository runs,
        IProjectSettingsStore projects,
        IRunInputStore inputs,
        IWorkerImageProvider images,
        IAccessionLookup accessionLookup,
        ActiveRuns activeRuns,
        IRunTargetResolver targets,
        LocalRunDriver local,
        Func<string?>? downloadsFolder = null,
        AppDataRoot? dataRoot = null,
        ICostEstimateService? estimates = null)
    {
        _estimates = estimates;
        _targets = targets;
        _local = local;
        _activeRuns = activeRuns;
        _downloadsFolder = downloadsFolder ?? (dataRoot ?? AppDataRoot.Default()).DefaultOutputSource(Services.KnownFolders.Downloads);
        _messenger = messenger;
        _runner = runner;
        _account = account;
        _settings = settings;
        _runs = runs;
        _projects = projects;
        _inputs = inputs;
        _images = images;
        _accessionLookup = accessionLookup;
    }

    public IReadOnlyList<WorkerResult> CompletedRuns { get; } = Array.Empty<WorkerResult>();

    public async Task<string> StartRunAsync(RunOptions options, CancellationToken cancellationToken)
    {
        var jobId = JobId.NewId(DateTimeOffset.UtcNow, Random.Shared);

        var decision = await _targets.ResolveAsync(options, cancellationToken).ConfigureAwait(false);
        if (RunTargets.IsThisPc(decision.Target))
        {
            await _local.StartAsync(jobId, options, AppVersion(), cancellationToken, decision.Readiness).ConfigureAwait(false);
            return jobId;
        }

        if (!IsCloudTarget(decision.Target))
        {
            await FailBeforeStartAsync(jobId, options, RunErrorCodes.TargetNotSupported, cancellationToken).ConfigureAwait(false);
            return jobId;
        }

        // Issue #830: the project AND account the run is made in, read together ONCE here. The request carries it, so the run (and every later stop, delete or cancel of it)
        // acts in this scope whatever is selected when those happen.
        var scope = _account.SelectedScope();
        var projectId = scope?.ProjectId;
        if (scope is null || string.IsNullOrWhiteSpace(projectId))
        {
            await FailBeforeStartAsync(jobId, options, RunErrorCodes.NoProject, cancellationToken).ConfigureAwait(false);
            return jobId;
        }

        // Issue #458: no image, no VM. A VM created without the startup script would boot, run
        // nothing, and sit until maxRunDuration, so the run fails here instead.
        var gpu = CloudJobRunner.ExpectsGpu(CloudJobRequestFactory.MachineTypeFor(options.GpuTier));
        var image = _images.Resolve(AppVersion(), gpu);
        if (image.Status != WorkerImageStatus.Available || image.Reference is null)
        {
            var code = image.Status == WorkerImageStatus.OverrideRefused ? RunErrorCodes.WorkerImageRefused : RunErrorCodes.WorkerImageUnavailable;
            await FailBeforeStartAsync(jobId, options, code, cancellationToken).ConfigureAwait(false);
            return jobId;
        }

        // Issue #460 / Hard Rule 14: keep our own copy of the input under app data before anything
        // is created in the cloud; the copy is what is uploaded, the user's file is never touched.
        // Issue #684: an NCBI accession is no file. It is checked here with the worker's own pattern (Hard Rule 2), nothing is copied or
        // uploaded, and the manifest names it; the worker fetches the record on the VM.
        StagedInput? staged;
        long? bases = null;
        var uploads = new List<StagedInput>();
        if (options.Accession is not null)
        {
            var accession = AccessionInput.Check(options.Accession, options.AccessionRange);
            if (accession.Problem is { } accessionProblem)
            {
                await FailBeforeStartAsync(jobId, options, AccessionErrorCodes.For(accessionProblem), cancellationToken, AccessionErrorCodes.DetailFor(accessionProblem)).ConfigureAwait(false);
                return jobId;
            }

            // Hard Rule 2: a well-formed accession can still name no record, or one over the worker's size limit. One small NCBI request from
            // this PC finds that out before any bucket object or VM exists; the worker fetches the record itself on the VM.
            var lookup = await _accessionLookup.LookupAsync(accession.Accession, accession.Range, cancellationToken).ConfigureAwait(false);
            if (lookup.Problem is { } lookupProblem)
            {
                await FailBeforeStartAsync(jobId, options, AccessionErrorCodes.For(lookupProblem), cancellationToken, AccessionErrorCodes.DetailFor(lookupProblem)).ConfigureAwait(false);
                return jobId;
            }

            bases = lookup.Length;
            staged = StagedInput.ForAccession(accession.Accession, accession.Range);
            uploads.Add(staged);
        }
        else
        {
            staged = await StageInputAsync(jobId, options, cancellationToken).ConfigureAwait(false);
            if (staged is null)
            {
                await FailBeforeStartAsync(jobId, options, RunErrorCodes.InputMissing, cancellationToken).ConfigureAwait(false);
                return jobId;
            }

            // Hard Rule 2 / #479: validate the staged copy with this run's own options before any bucket
            // object or VM exists. Nobody pays for a VM to learn their file has an X in it.
            var check = InputFileValidator.Validate(staged.LocalPath, options.Format, options.AmbiguityPolicy, options.TreatAsRna);
            if (check.Problem is { } problem)
            {
                await FailBeforeStartAsync(jobId, options, InputProblemErrorCodes.For(problem.Code), cancellationToken, InputProblemErrorCodes.DetailFor(problem)).ConfigureAwait(false);
                return jobId;
            }

            bases = check.TotalLength;
            uploads.Add(staged);
        }

        // Issue #686 / Hard Rule 2: a paired GFF3 is copied under the run's own folder too, and checked against the staged FASTA
        // with this run's topology before anything exists in the cloud. The FASTA stays first in the list (the output folder is named from it).
        // An accession has no FASTA to pair with: the worker refuses the combination, so the path is never taken.
        if (!staged.IsAccession && !string.IsNullOrWhiteSpace(options.AnnotationPath))
        {
            var annotation = await StageAnnotationAsync(jobId, options.AnnotationPath, cancellationToken).ConfigureAwait(false);
            if (annotation is null)
            {
                await FailBeforeStartAsync(jobId, options, RunErrorCodes.InputMissing, cancellationToken).ConfigureAwait(false);
                return jobId;
            }

            var pair = AnnotationPairValidator.Check(staged.LocalPath, annotation.LocalPath, options.Topology);
            if (pair.Problem is { } annotationProblem)
            {
                // The code only: the problem's own text can hold a sequence name from the user's file.
                await FailBeforeStartAsync(jobId, options, RunErrorCodes.InputInvalid, cancellationToken, "Annotation " + annotationProblem.Code).ConfigureAwait(false);
                return jobId;
            }

            uploads.Add(annotation);
        }

        // #558: the id lives in its own write-once file. If it cannot be read or is unusable we
        // fail the run here, before any cloud resource exists, rather than mint a new id (that
        // would orphan every labelled resource, Hard Rules 9 and 10) or crash the UI thread.
        string installationId;
        try
        {
            installationId = InstallationId.GetOrCreate(_settings);
        }
        catch (InstallationIdUnusableException ex)
        {
            // Names a true action (restore the set-aside copy or contact support); full recovery UX is DECISION #404.
            await FailBeforeStartAsync(jobId, options, RunErrorCodes.InstallationIdUnusable, cancellationToken, ex.GetType().Name).ConfigureAwait(false);
            return jobId;
        }
        catch (SettingsUnavailableException ex)
        {
            await FailBeforeStartAsync(jobId, options, RunErrorCodes.Other, cancellationToken, ex.GetType().Name).ConfigureAwait(false);
            return jobId;
        }

        // Issue #109: the hard stop refuses before any bucket object or VM exists. It asks the same shared path the New run page's card asks
        // (RunSpendCheck: the run's own estimate plus this month's spend against the cap), so the card and the engine cannot disagree. A
        // warning never stops a run, and a ledger or price list that cannot be read does not block the user.
        if (await IsBlockedBySpendCapAsync(options, bases, cancellationToken).ConfigureAwait(false))
        {
            await FailBeforeStartAsync(jobId, options, RunErrorCodes.SpendCap, cancellationToken).ConfigureAwait(false);
            return jobId;
        }

        var request = CloudJobRequestFactory.Create(
            options,
            jobId,
            scope,
            installationId,
            AppVersion(),
            image.Reference,
            uploads,
            string.IsNullOrWhiteSpace(options.OutputFolder) ? RunOutputFolders.DefaultParent(_downloadsFolder) : options.OutputFolder);

        // Write-ahead (docs/architecture.md section 6): the row exists
        // before the first network call, so a crash right after Run still
        // shows the run on relaunch, and the runner's later phase updates
        // keep these columns. Runs.ProjectId references Projects with foreign keys on (issue #532),
        // and every path that starts a run comes through here, so the project row is ensured here
        // (idempotent, never overwrites a richer row) rather than at each place a project is selected.
        await _projects.EnsureAsync(projectId, scope.AccountSub, cancellationToken).ConfigureAwait(false);
        await _runs.UpsertAsync(
            new RunRecord(
                jobId,
                JobPhase.Draft,
                DateTimeOffset.UtcNow,
                Target: RunTargets.CloudRecord,
                StartedAt: DateTimeOffset.UtcNow,
                OptionsJson: RunOptionsJson.Serialize(options),
                ProjectId: projectId,
                // The planned name: it keeps the diagnostics redaction key for a run that fails before any VM exists. The runner overwrites it with the VM the run actually uses (a reused VM keeps its old name, issue #679).
                VmName: request.Spec.VmName,
                MachineType: request.Spec.MachineType,
                AppVersion: request.Spec.AppVersion,
                InstallationId: installationId,
                AccountSub: scope.AccountSub),
            cancellationToken).ConfigureAwait(false);

        // Registered before the body runs (see ActiveRuns), so a cancel can never find a run that is not there yet. The runner
        // records every phase itself and never throws for a failure; a cancel the caller asked for ends the task quietly.
        _ = _activeRuns.TryStart(jobId, token => RunAndRememberAsync(request, jobId, projectId, token));

        return jobId;
    }

    /// <summary>
    /// Runs the job, then remembers the results bucket it used under the run's OWN project (issue #260): the project selected by now may be a different one,
    /// and the bucket belongs to the project the run started in.
    /// </summary>
    private async Task RunAndRememberAsync(CloudJobRequest request, string jobId, string projectId, CancellationToken cancellationToken)
    {
        try
        {
            await _runner.RunAsync(request, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await ProjectBucketMemory.RememberAsync(_runs, _projects, jobId, projectId).ConfigureAwait(false);
        }
    }

    public async Task CancelRunAsync(string jobId, CancellationToken cancellationToken)
    {
        if (await IsLocalRunAsync(jobId, cancellationToken).ConfigureAwait(false))
        {
            await _local.CancelAsync(jobId).ConfigureAwait(false);
            return;
        }

        // Stop and await whatever drives this run (one the engine started or one the reconciler reattached) before the
        // cancel writes its own phase, so there is one writer at a time.
        // Issue #830: the run's own project and account, from its row, never the current selection.
        var scope = await ScopeOfRunAsync(jobId, cancellationToken).ConfigureAwait(false);
        await _activeRuns.CancelAsync(jobId, () => _runner.CancelAsync(scope, jobId, cancellationToken)).ConfigureAwait(false);

        // Review F1: a cancel whose run's account is signed out does not throw: it records Failed/account_signed_out, whose copy points at a Sign in button. Raise the same signal the reconciler does,
        // so the shell bar offers it too (the run page reads the same row and shows its own prompt).
        var rows = await _runs.GetAllAsync(cancellationToken).ConfigureAwait(false);
        if (rows.LastOrDefault(r => r.JobId == jobId) is { Phase: JobPhase.Failed, ErrorCode: RunErrorCodes.AccountSignedOut } ended)
        {
            _messenger.Send(new AccountSignInNeededMessage(ended.AccountSub));
        }
    }

    /// <summary>Issue #830: the scope a run's resources live in, from its row. No row is a <see cref="RunNotFoundException"/> (review M5): the app cannot say where the run's computer is, and returning silently would leave the person thinking it was stopped.</summary>
    private async Task<CloudScope> ScopeOfRunAsync(string jobId, CancellationToken cancellationToken)
    {
        var rows = await _runs.GetAllAsync(cancellationToken).ConfigureAwait(false);
        return rows.LastOrDefault(r => r.JobId == jobId) is { } row ? RunScope.Of(row) : throw new RunNotFoundException(jobId);
    }

    private async Task<bool> IsLocalRunAsync(string jobId, CancellationToken cancellationToken)
    {
        var rows = await _runs.GetAllAsync(cancellationToken).ConfigureAwait(false);
        return RunTargets.IsLocalRecord(rows.LastOrDefault(r => r.JobId == jobId)?.Target);
    }

    public async Task StopVmAsync(string jobId, CancellationToken cancellationToken)
    {
        var scope = await ScopeOfRunAsync(jobId, cancellationToken).ConfigureAwait(false);
        await _runner.StopVmAsync(scope, jobId, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteVmAsync(string jobId, CancellationToken cancellationToken)
    {
        var scope = await ScopeOfRunAsync(jobId, cancellationToken).ConfigureAwait(false);
        await _runner.DeleteVmAsync(scope, jobId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The staged copy of the chosen input, or null when there is no input or it cannot be read (the caller records <see cref="RunErrorCodes.InputMissing"/>).</summary>
    private async Task<StagedInput?> StageInputAsync(string jobId, RunOptions options, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.InputPath))
        {
            return null;
        }

        try
        {
            return await _inputs.StageAsync(jobId, options.InputPath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>The staged copy of the paired GFF3, or null when it cannot be read (the caller records <see cref="RunErrorCodes.InputMissing"/>).</summary>
    private async Task<StagedInput?> StageAnnotationAsync(string jobId, string annotationPath, CancellationToken cancellationToken)
    {
        try
        {
            return await _inputs.StageAnnotationAsync(jobId, annotationPath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private async Task<bool> IsBlockedBySpendCapAsync(RunOptions options, long? bases, CancellationToken cancellationToken)
    {
        if (_estimates is null)
        {
            return false;
        }

        try
        {
            var (_, cap) = await _estimates.CheckAsync(CloudJobRequestFactory.MachineTypeFor(options.GpuTier), options.SpotVm, bases, options.BootDiskGb, cancellationToken).ConfigureAwait(false);
            return cap?.Level == SpendCapLevel.Blocked;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }

    private static bool IsCloudTarget(string target)
        => string.Equals(target, "Cloud", StringComparison.OrdinalIgnoreCase)
           || string.Equals(target, "Auto", StringComparison.OrdinalIgnoreCase);

    internal static string AppVersion()
        => Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

    private async Task FailBeforeStartAsync(string jobId, RunOptions options, string errorCode, CancellationToken cancellationToken, string? errorDetail = null)
    {
        var now = DateTimeOffset.UtcNow;
        await _runs.UpsertAsync(
            new RunRecord(
                jobId,
                JobPhase.Failed,
                now,
                ErrorCode: errorCode,
                ErrorDetail: errorDetail,
                StartedAt: now,
                FinishedAt: now,
                OptionsJson: RunOptionsJson.Serialize(options)),
            cancellationToken).ConfigureAwait(false);
        _messenger.Send(new RunPhaseChangedMessage(jobId, JobPhase.Failed));
    }
}
