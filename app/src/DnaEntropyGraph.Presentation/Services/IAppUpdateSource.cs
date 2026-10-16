namespace DnaEntropyGraph.Presentation.Services;

/// <summary>Which GitHub releases the updater may offer (issue #145): Stable sees only full releases, Beta also sees pre-releases. A draft release is invisible to both.</summary>
public enum UpdateChannel
{
    Stable,
    Beta,
}

/// <summary>A newer release the updater found; <see cref="ReleaseNotes"/> is the GitHub release body, or null when the release has none.</summary>
public sealed record AvailableUpdate(string Version, string? ReleaseNotes);

public enum UpdateCheckOutcome
{
    UpToDate,
    Available,

    /// <summary>The running copy was not put there by the installer (a dev build, a CI artifact), so there is no feed to update it from.</summary>
    NotInstalled,
}

public sealed record UpdateCheckResult(UpdateCheckOutcome Outcome, AvailableUpdate? Update = null);

/// <summary>
/// The update feed behind an interface (issue #179), so ViewModel tests never touch the network or Velopack. The real one is
/// <c>VelopackUpdateSource</c> in the App project. Every method may throw; callers report the failure in plain words.
/// </summary>
public interface IAppUpdateSource
{
    /// <summary>The version of the running copy, for display.</summary>
    string CurrentVersion { get; }

    Task<UpdateCheckResult> CheckAsync(UpdateChannel channel, CancellationToken cancellationToken);

    /// <summary>Downloads the update the last <see cref="CheckAsync"/> found. <paramref name="progress"/> is 0 to 100.</summary>
    Task DownloadAsync(Action<int>? progress, CancellationToken cancellationToken);

    /// <summary>Applies the downloaded update and restarts the app. Never returns when it works. The caller has already checked that no run is active.</summary>
    void ApplyAndRestart();
}

/// <summary>Whether any run is being driven right now (issue #179: an update never restarts the app mid-run). The App implements it over <c>ActiveRuns</c>.</summary>
public interface IActiveRunProbe
{
    bool AnyActive { get; }
}
