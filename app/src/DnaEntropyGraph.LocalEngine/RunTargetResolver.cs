using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Contract;
using DnaEntropyGraph.Core.LocalEngine;

namespace DnaEntropyGraph.LocalEngine;

/// <summary>
/// Cloud / This PC / Auto (docs/architecture.md, issue #173). Auto picks This PC only when the engine is healthy and the
/// model fits this PC; anything else is Cloud. An explicit choice is never changed: a This PC that cannot run fails
/// visibly in the run, because quietly switching it to the cloud would spend the user's money.
/// </summary>
public sealed class RunTargetResolver : IRunTargetResolver
{
    private readonly ILocalReadinessSource _readiness;

    public RunTargetResolver(ILocalReadinessSource readiness)
    {
        _readiness = readiness;
    }

    public async Task<RunTargetDecision> ResolveAsync(RunOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (RunTargets.IsThisPc(options.RunTarget))
        {
            return new RunTargetDecision(RunTargets.ThisPc);
        }

        if (!RunTargets.IsAuto(options.RunTarget))
        {
            // Cloud, or a value nobody offers: the caller refuses what it does not know.
            return new RunTargetDecision(string.Equals(options.RunTarget, RunTargets.Cloud, StringComparison.OrdinalIgnoreCase) ? RunTargets.Cloud : options.RunTarget);
        }

        var mock = options.Predictor == PredictorSelection.MockTestPredictor;
        if (!mock && WorkerManifestBuilder.NeedsFp8(options.ModelId))
        {
            return new RunTargetDecision(RunTargets.Cloud, "model_needs_fp8");
        }

        LocalReadiness readiness;
        try
        {
            readiness = await _readiness.CheckAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new RunTargetDecision(RunTargets.Cloud, "local_not_ready");
        }

        if (readiness.CanRun(needsGpu: !mock))
        {
            return new RunTargetDecision(RunTargets.ThisPc, "local_ready");
        }

        var engineUsable = readiness.Engine.State == LocalEngineState.Healthy;
        return new RunTargetDecision(RunTargets.Cloud, engineUsable ? "local_blocked" : "local_not_ready");
    }
}
