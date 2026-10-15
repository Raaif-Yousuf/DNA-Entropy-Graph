namespace DnaEntropyGraph.Core.LocalEngine;

/// <summary>The three places a run can go (spec 4.3 "Where to run"). The strings are what <see cref="RunOptions.RunTarget"/> holds.</summary>
public static class RunTargets
{
    public const string Cloud = "Cloud";
    public const string ThisPc = "This PC";
    public const string Auto = "Auto";

    /// <summary>The <c>RunRecord.Target</c> column values.</summary>
    public const string CloudRecord = "cloud";

    public const string LocalRecord = "local";

    public static bool IsThisPc(string? target) => string.Equals(target, ThisPc, StringComparison.OrdinalIgnoreCase);

    public static bool IsAuto(string? target) => string.Equals(target, Auto, StringComparison.OrdinalIgnoreCase);

    public static bool IsLocalRecord(string? recordTarget) => string.Equals(recordTarget, LocalRecord, StringComparison.OrdinalIgnoreCase);
}

public enum LocalEngineState
{
    /// <summary>No engine folder or no Python in it.</summary>
    NotInstalled,

    /// <summary>The engine is there but the worker did not answer, or answered nonsense.</summary>
    Broken,

    /// <summary>The worker answered, but it is not the app's version (the contract may have changed).</summary>
    WorkerVersionMismatch,

    /// <summary>The worker answered and is the app's version.</summary>
    Healthy,
}

/// <param name="State">Whether the worker in the engine's own Python answered and matches the app.</param>
/// <param name="WorkerVersion">What the worker said it is, when it answered.</param>
/// <param name="EvoInstalled">The <c>[evo]</c> extra (torch and evo2) imports.</param>
/// <param name="CudaAvailable">torch can see a CUDA device in that Python.</param>
/// <param name="Detail">Raw diagnostic text for logs; never shown to a user.</param>
public sealed record LocalEngineHealth(LocalEngineState State, string? WorkerVersion, bool EvoInstalled, bool CudaAvailable, string Detail)
{
    public static LocalEngineHealth NotInstalled(string detail = "") => new(LocalEngineState.NotInstalled, null, false, false, detail);
}

/// <summary>The PC's hardware and the engine's health, together: everything "can this PC run it" depends on.</summary>
public sealed record LocalReadiness(LocalCapability Capability, LocalEngineHealth Engine)
{
    /// <summary>
    /// True when a run of this kind can start here. The mock predictor needs only a healthy engine (it runs on the CPU, so a PC
    /// with no NVIDIA card can still check the whole local path); a real model needs a capable GPU and a usable CUDA in the engine.
    /// </summary>
    public bool CanRun(bool needsGpu)
        => Engine.State == LocalEngineState.Healthy && (!needsGpu || (Capability.Eligible && Engine.CudaAvailable));
}

/// <summary>Checks this PC and its local engine (issue #173's health card). Never throws.</summary>
public interface ILocalReadinessSource
{
    Task<LocalReadiness> CheckAsync(CancellationToken cancellationToken);
}

/// <summary>Where a run was sent, and why not where the user asked when those differ.</summary>
/// <param name="Target"><see cref="RunTargets.Cloud"/> or <see cref="RunTargets.ThisPc"/>; never Auto.</param>
/// <param name="AutoReason">For Auto: why this one was chosen, as a code (<c>local_ready</c>, <c>local_not_ready</c>, <c>model_needs_fp8</c>, <c>local_blocked</c>). Null for an explicit choice.</param>
public sealed record RunTargetDecision(string Target, string? AutoReason = null);

public interface IRunTargetResolver
{
    /// <summary>
    /// Turns the user's choice into a place. An explicit Cloud or This PC is returned as it is (a This PC that cannot run is
    /// the run's own visible failure, never a silent switch to the cloud: a cloud run costs money). Auto picks This PC only when the engine
    /// is healthy and the model fits this PC's GPU.
    /// </summary>
    Task<RunTargetDecision> ResolveAsync(RunOptions options, CancellationToken cancellationToken);
}

public enum LocalInstallStep
{
    CreatingEnvironment,
    InstallingPackages,
    InstallingWorker,
    Checking,
    Removing,
}

public sealed record LocalInstallProgress(LocalInstallStep Step, string? Line = null);

/// <summary>Result of install, repair or remove. <paramref name="ErrorCode"/> is one of <see cref="LocalEngineErrorCodes"/>.</summary>
public sealed record LocalInstallResult(bool Succeeded, string? ErrorCode = null, string? Detail = null);

public static class LocalEngineErrorCodes
{
    /// <summary>The bundled <c>uv</c> is not next to the app (a damaged install).</summary>
    public const string UvMissing = "local_uv_missing";

    /// <summary>The pinned package list shipped with the app is missing or empty.</summary>
    public const string LockMissing = "local_lock_missing";

    public const string InstallFailed = "local_install_failed";

    /// <summary>The install ran but the worker in the new environment did not pass the health check.</summary>
    public const string CheckFailed = "local_check_failed";

    /// <summary>A remove aimed at a folder that is not the engine's own.</summary>
    public const string RemoveRefused = "local_remove_refused";

    public const string Cancelled = "local_cancelled";

    public static IReadOnlyList<string> All { get; } = [UvMissing, LockMissing, InstallFailed, CheckFailed, RemoveRefused, Cancelled];

    /// <summary>The <c>Resources.resw</c> key whose text names one action for the code.</summary>
    public static string ResourceKey(string? code) => code switch
    {
        UvMissing => "LocalEngineError_local_uv_missing",
        LockMissing => "LocalEngineError_local_lock_missing",
        CheckFailed => "LocalEngineError_local_check_failed",
        RemoveRefused => "LocalEngineError_local_remove_refused",
        Cancelled => "LocalEngineError_local_cancelled",
        _ => "LocalEngineError_local_install_failed",
    };
}

/// <summary>The engine's one-click install, repair and removal (issue #172). The real one drives the bundled <c>uv</c>; tests use a fake.</summary>
public interface ILocalEngineInstaller
{
    /// <summary>The folder the whole engine lives in; Remove deletes exactly this.</summary>
    string EngineFolder { get; }

    Task<LocalInstallResult> InstallAsync(IProgress<LocalInstallProgress>? progress, CancellationToken cancellationToken);

    /// <summary>Re-syncs an existing environment to the pinned list.</summary>
    Task<LocalInstallResult> RepairAsync(IProgress<LocalInstallProgress>? progress, CancellationToken cancellationToken);

    Task<LocalInstallResult> RemoveAsync(CancellationToken cancellationToken);
}
