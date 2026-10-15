using CommunityToolkit.Mvvm.Messaging;
using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Cloud;
using DnaEntropyGraph.Core.Contract;
using DnaEntropyGraph.Core.Inputs;
using DnaEntropyGraph.Core.LocalEngine;
using DnaEntropyGraph.Core.Runs;
using DnaEntropyGraph.LocalEngine;
using DnaEntropyGraph.Presentation.Messaging;

namespace DnaEntropyGraph.App;

/// <summary>
/// The This PC half of <see cref="JobEngine"/> (issues #173 and #174): everything between "the user pressed Run with This PC" and the
/// hand-off to <see cref="LocalJobRunner"/>. It makes the same promises a cloud start does before anything costs anything: the input
/// is copied under app data (Hard Rule 14) and validated with this run's options (Hard Rule 2), and the row is written first
/// (docs/architecture.md section 6). Unlike the cloud it also checks that this PC can run the job at all, because the worker
/// needs a healthy engine and, for a real model, a GPU: a This PC run that cannot run fails here with a code that names one action.
/// </summary>
public sealed class LocalRunDriver
{
    private readonly IMessenger _messenger;
    private readonly LocalJobRunner _runner;
    private readonly ILocalReadinessSource _readiness;
    private readonly ISettingsStore _settings;
    private readonly IRunRepository _runs;
    private readonly IRunInputStore _inputs;
    private readonly ActiveRuns _activeRuns;
    private readonly Func<string?> _downloadsFolder;

    public LocalRunDriver(
        IMessenger messenger,
        LocalJobRunner runner,
        ILocalReadinessSource readiness,
        ISettingsStore settings,
        IRunRepository runs,
        IRunInputStore inputs,
        ActiveRuns activeRuns,
        Func<string?>? downloadsFolder = null)
    {
        _messenger = messenger;
        _runner = runner;
        _readiness = readiness;
        _settings = settings;
        _runs = runs;
        _inputs = inputs;
        _activeRuns = activeRuns;
        _downloadsFolder = downloadsFolder ?? Services.KnownFolders.Downloads;
    }

    /// <summary>Starts the run in the background. Every refusal is recorded on the run's own row and announced; nothing here throws for one.</summary>
    public async Task StartAsync(string jobId, RunOptions options, string appVersion, CancellationToken cancellationToken)
    {
        var mock = options.Predictor == PredictorSelection.MockTestPredictor;
        if (!mock && WorkerManifestBuilder.NeedsFp8(options.ModelId))
        {
            await FailBeforeStartAsync(jobId, options, RunErrorCodes.ModelNeedsHopper, cancellationToken).ConfigureAwait(false);
            return;
        }

        var readiness = await _readiness.CheckAsync(cancellationToken).ConfigureAwait(false);
        if (readiness.Engine.State != LocalEngineState.Healthy)
        {
            await FailBeforeStartAsync(jobId, options, RunErrorCodes.LocalEngineNotReady, cancellationToken, "engine " + readiness.Engine.State).ConfigureAwait(false);
            return;
        }

        if (!readiness.CanRun(needsGpu: !mock))
        {
            var blockers = readiness.Capability.Blockers.Count == 0 ? "no CUDA in the engine" : string.Join(',', readiness.Capability.Blockers);
            await FailBeforeStartAsync(jobId, options, RunErrorCodes.LocalGpuNotUsable, cancellationToken, blockers).ConfigureAwait(false);
            return;
        }

        var staged = await StageInputAsync(jobId, options, cancellationToken).ConfigureAwait(false);
        if (staged is null)
        {
            await FailBeforeStartAsync(jobId, options, RunErrorCodes.InputMissing, cancellationToken).ConfigureAwait(false);
            return;
        }

        var check = InputFileValidator.Validate(staged.LocalPath, options.Format, options.AmbiguityPolicy, options.TreatAsRna);
        if (check.Problem is { } problem)
        {
            await FailBeforeStartAsync(jobId, options, InputProblemErrorCodes.For(problem.Code), cancellationToken, InputProblemErrorCodes.DetailFor(problem)).ConfigureAwait(false);
            return;
        }

        string installationId;
        try
        {
            installationId = InstallationId.GetOrCreate(_settings);
        }
        catch (InstallationIdUnusableException ex)
        {
            await FailBeforeStartAsync(jobId, options, RunErrorCodes.InstallationIdUnusable, cancellationToken, ex.GetType().Name).ConfigureAwait(false);
            return;
        }
        catch (SettingsUnavailableException ex)
        {
            await FailBeforeStartAsync(jobId, options, RunErrorCodes.Other, cancellationToken, ex.GetType().Name).ConfigureAwait(false);
            return;
        }

        var request = new LocalJobRequest(
            jobId,
            options,
            staged,
            installationId,
            appVersion,
            string.IsNullOrWhiteSpace(options.OutputFolder) ? RunOutputFolders.DefaultParent(_downloadsFolder) : options.OutputFolder);

        var now = DateTimeOffset.UtcNow;
        await _runs.UpsertAsync(
            new RunRecord(
                jobId,
                JobPhase.Draft,
                now,
                Target: RunTargets.LocalRecord,
                StartedAt: now,
                OptionsJson: RunOptionsJson.Serialize(options),
                AppVersion: appVersion,
                InstallationId: installationId),
            cancellationToken).ConfigureAwait(false);

        // Registered before the body runs (see ActiveRuns), so a cancel can never find a run that is not there yet.
        _ = _activeRuns.TryStart(jobId, token => _runner.RunAsync(request, token));
    }

    /// <summary>A user cancel of a This PC run: the runner stops the worker (cooperatively, then by killing its tree), then the cancel's own phases are recorded.</summary>
    public Task CancelAsync(string jobId) => _activeRuns.CancelAsync(jobId, () => _runner.RecordCancelledAsync(jobId));

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

    private async Task FailBeforeStartAsync(string jobId, RunOptions options, string errorCode, CancellationToken cancellationToken, string? errorDetail = null)
    {
        var now = DateTimeOffset.UtcNow;
        await _runs.UpsertAsync(
            new RunRecord(
                jobId,
                JobPhase.Failed,
                now,
                Target: RunTargets.LocalRecord,
                ErrorCode: errorCode,
                ErrorDetail: errorDetail,
                StartedAt: now,
                FinishedAt: now,
                OptionsJson: RunOptionsJson.Serialize(options)),
            cancellationToken).ConfigureAwait(false);
        _messenger.Send(new RunPhaseChangedMessage(jobId, JobPhase.Failed));
    }
}
