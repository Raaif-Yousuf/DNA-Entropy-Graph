namespace DnaEntropyGraph.LocalEngine;

/// <summary>
/// Install/repair/health of the local `uv`-managed venv or WSL2 fallback
/// (docs/superpowers/specs Appendix A). The skeleton reports "not
/// installed" honestly rather than faking success, since that is the
/// correct default until the real installer flow exists.
/// </summary>
public sealed class LocalEngineManager
{
    public Task<EngineHealth> CheckHealthAsync(CancellationToken cancellationToken)
        => Task.FromResult(new EngineHealth(IsInstalled: false, HasCudaGpu: false, Detail: "Local engine not yet installed."));
}

public sealed record EngineHealth(bool IsInstalled, bool HasCudaGpu, string Detail);
