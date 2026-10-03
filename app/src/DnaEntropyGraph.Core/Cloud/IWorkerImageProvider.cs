namespace DnaEntropyGraph.Core.Cloud;

/// <summary>Whether a worker image could be chosen for a run, and why not when it could not.</summary>
public enum WorkerImageStatus
{
    /// <summary>A digest-pinned reference is available.</summary>
    Available,

    /// <summary>The list shipped with this app version has no image for it (and nothing overrides it).</summary>
    NoneShipped,

    /// <summary>An override was set that is not on the pinned list and developer mode is off (or the override is not pinned by digest).</summary>
    OverrideRefused,
}

/// <param name="IsDeveloperOverride"><c>true</c> when <paramref name="Reference"/> was accepted only because developer mode is on (it is not the pinned image for this version and variant); it is exposed here but NOT yet recorded anywhere: App has no logger and the run record has no column for it. Whoever adds either must read this flag.</param>
public sealed record WorkerImageResolution(WorkerImageStatus Status, string? Reference, bool IsDeveloperOverride = false);

/// <summary>
/// Supplies the digest-pinned worker image for a run (issue #458; CLAUDE.md
/// Stack row "Container": the app pins the worker by digest and refuses one
/// outside its allowlist unless developer mode is on). Implemented in App,
/// where the pinned list ships with the build.
/// </summary>
public interface IWorkerImageProvider
{
    /// <param name="appVersion">The running app version; images are pinned per version.</param>
    /// <param name="gpu"><c>true</c> for a GPU machine (the -cuda image), <c>false</c> for a CPU smoke VM (-cpu).</param>
    WorkerImageResolution Resolve(string appVersion, bool gpu);
}
