using DnaEntropyGraph.Presentation.Services;
using Velopack;
using Velopack.Sources;

namespace DnaEntropyGraph.App.Services;

/// <summary>
/// Issue #179: the updater over Velopack and the GitHub Releases feed (docs/packaging_design.md section 4). The Releases API hides a
/// draft release, and <c>prerelease</c> decides whether a pre-release is offered, which is how the Stable and Beta channels differ (#145).
/// A copy that the installer did not put there (a dev build, a CI artifact) has no feed, and says so instead of throwing.
/// </summary>
public sealed class VelopackUpdateSource : IAppUpdateSource
{
    /// <summary>The releases feed. A constant: the app only ever updates from its own repository.</summary>
    public const string RepoUrl = "https://github.com/Raaif-Yousuf/DNA-Entropy-Graph";

    private UpdateManager? _manager;
    private UpdateInfo? _info;

    public string CurrentVersion => InstalledVersion() ?? System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

    // A copy the installer did not put there has no installed version; any failure to ask falls back to the assembly's own.
    private static string? InstalledVersion()
    {
        try
        {
            return new UpdateManager(new GithubSource(RepoUrl, null, false)).CurrentVersion?.ToString();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning($"installed version unknown: {ex.GetType().Name}");
            return null;
        }
    }

    public async Task<UpdateCheckResult> CheckAsync(UpdateChannel channel, CancellationToken cancellationToken)
    {
        var manager = new UpdateManager(new GithubSource(RepoUrl, null, channel == UpdateChannel.Beta));
        if (!manager.IsInstalled)
        {
            return new UpdateCheckResult(UpdateCheckOutcome.NotInstalled);
        }

        var info = await manager.CheckForUpdatesAsync().ConfigureAwait(false);
        if (info is null)
        {
            _manager = null;
            _info = null;
            return new UpdateCheckResult(UpdateCheckOutcome.UpToDate);
        }

        _manager = manager;
        _info = info;
        return new UpdateCheckResult(
            UpdateCheckOutcome.Available,
            new AvailableUpdate(info.TargetFullRelease.Version.ToString(), info.TargetFullRelease.NotesMarkdown));
    }

    public Task DownloadAsync(Action<int>? progress, CancellationToken cancellationToken)
        => _manager is { } manager && _info is { } info
            ? manager.DownloadUpdatesAsync(info, progress, cancellationToken)
            : throw new InvalidOperationException("No update has been found yet.");

    public void ApplyAndRestart()
    {
        if (_manager is not { } manager || _info is not { } info)
        {
            throw new InvalidOperationException("No update has been downloaded yet.");
        }

        manager.ApplyUpdatesAndRestart(info.TargetFullRelease);
    }
}
