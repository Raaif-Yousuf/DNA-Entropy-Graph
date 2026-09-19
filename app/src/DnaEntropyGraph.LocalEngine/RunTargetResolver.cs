namespace DnaEntropyGraph.LocalEngine;

/// <summary>Cloud / This PC / Auto (docs/architecture.md). Auto picks This PC only when the local engine is healthy.</summary>
public sealed class RunTargetResolver
{
    private readonly LocalEngineManager _localEngineManager;

    public RunTargetResolver(LocalEngineManager localEngineManager)
    {
        _localEngineManager = localEngineManager;
    }

    public async Task<string> ResolveAsync(string requestedTarget, CancellationToken cancellationToken)
    {
        if (!string.Equals(requestedTarget, "Auto", StringComparison.OrdinalIgnoreCase))
        {
            return requestedTarget;
        }

        var health = await _localEngineManager.CheckHealthAsync(cancellationToken).ConfigureAwait(false);
        return health.IsInstalled && health.HasCudaGpu ? "LocalPc" : "Cloud";
    }
}
