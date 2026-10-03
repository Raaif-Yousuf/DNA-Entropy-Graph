using System.Collections.Concurrent;
using System.Reflection;
using CommunityToolkit.Mvvm.Messaging;
using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Cloud;
using DnaEntropyGraph.Core.Contract;
using DnaEntropyGraph.Core.Inputs;
using DnaEntropyGraph.Core.Runs;
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
/// reference back to this class. A local-engine run is <c>LocalJobRunner</c>'s
/// own issue (#174) and fails visibly here until it lands.
/// </summary>
public sealed class JobEngine : IJobEngine, IRunVmActions
{

    private readonly ConcurrentDictionary<string, ActiveRun> _activeRuns = new();
    private readonly IMessenger _messenger;
    private readonly CloudJobRunner _runner;
    private readonly IGcpAccount _account;
    private readonly ISettingsStore _settings;
    private readonly IRunRepository _runs;
    private readonly IRunInputStore _inputs;
    private readonly IWorkerImageProvider _images;
    private readonly Func<string?> _downloadsFolder;

    public JobEngine(
        IMessenger messenger,
        CloudJobRunner runner,
        IGcpAccount account,
        ISettingsStore settings,
        IRunRepository runs,
        IRunInputStore inputs,
        IWorkerImageProvider images,
        Func<string?>? downloadsFolder = null)
    {
        _downloadsFolder = downloadsFolder ?? Services.KnownFolders.Downloads;
        _messenger = messenger;
        _runner = runner;
        _account = account;
        _settings = settings;
        _runs = runs;
        _inputs = inputs;
        _images = images;
    }

    public IReadOnlyList<WorkerResult> CompletedRuns { get; } = Array.Empty<WorkerResult>();

    public async Task<string> StartRunAsync(RunOptions options, CancellationToken cancellationToken)
    {
        var jobId = JobId.NewId(DateTimeOffset.UtcNow, Random.Shared);

        if (!IsCloudTarget(options.RunTarget))
        {
            await FailBeforeStartAsync(jobId, options, RunErrorCodes.TargetNotSupported, cancellationToken).ConfigureAwait(false);
            return jobId;
        }

        var projectId = _account.SelectedProjectId;
        if (string.IsNullOrWhiteSpace(projectId))
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
        var staged = await StageInputAsync(jobId, options, cancellationToken).ConfigureAwait(false);
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

        var installationId = InstallationId.GetOrCreate(_settings);
        var request = CloudJobRequestFactory.Create(
            options,
            jobId,
            projectId,
            installationId,
            AppVersion(),
            image.Reference,
            [staged],
            string.IsNullOrWhiteSpace(options.OutputFolder) ? RunOutputFolders.DefaultParent(_downloadsFolder) : options.OutputFolder);

        // Write-ahead (docs/architecture.md section 6): the row exists
        // before the first network call, so a crash right after Run still
        // shows the run on relaunch, and the runner's later phase updates
        // keep these columns.
        await _runs.UpsertAsync(
            new RunRecord(
                jobId,
                JobPhase.Draft,
                DateTimeOffset.UtcNow,
                Target: "cloud",
                StartedAt: DateTimeOffset.UtcNow,
                OptionsJson: RunOptionsJson.Serialize(options),
                ProjectId: projectId,
                VmName: request.Spec.VmName,
                MachineType: request.Spec.MachineType,
                AppVersion: request.Spec.AppVersion,
                InstallationId: installationId),
            cancellationToken).ConfigureAwait(false);

        var cts = new CancellationTokenSource();

        // The run task is created first and only released once it is
        // registered, so a cancel can never find an entry whose Task is still
        // null (two writers on the row), and the task's own cleanup cannot
        // run before the entry exists.
        var registered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = new ActiveRun(cts, Task.Run(async () =>
        {
            await registered.Task.ConfigureAwait(false);
            try
            {
                await _runner.RunAsync(request, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                // CancelRunAsync owns the Cancelling/Cancelled records. Any
                // other cancellation is a timeout, which the runner records as
                // Failed itself and never throws.
            }
            finally
            {
                _activeRuns.TryRemove(jobId, out _);
                cts.Dispose();
            }
        }));
        _activeRuns[jobId] = run;
        registered.SetResult();

        return jobId;
    }

    public async Task CancelRunAsync(string jobId, CancellationToken cancellationToken)
    {
        if (_activeRuns.TryGetValue(jobId, out var run))
        {
            try
            {
                await run.Cts.CancelAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                // The run finished and disposed its source between the lookup and here.
            }

            // Let the runner stop writing phases before CancelAsync writes
            // its own, so there is one writer at a time.
            await run.Task.ConfigureAwait(false);
        }

        await _runner.CancelAsync(jobId, cancellationToken).ConfigureAwait(false);
    }

    public Task StopVmAsync(string jobId, CancellationToken cancellationToken) => _runner.StopVmAsync(jobId, cancellationToken);

    public Task DeleteVmAsync(string jobId, CancellationToken cancellationToken) => _runner.DeleteVmAsync(jobId, cancellationToken);

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

    private static bool IsCloudTarget(string target)
        => string.Equals(target, "Cloud", StringComparison.OrdinalIgnoreCase)
           || string.Equals(target, "Auto", StringComparison.OrdinalIgnoreCase);

    private static string AppVersion()
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

    private sealed class ActiveRun
    {
        public ActiveRun(CancellationTokenSource cts, Task task)
        {
            Cts = cts;
            Task = task;
        }

        public CancellationTokenSource Cts { get; }

        public Task Task { get; }
    }
}
