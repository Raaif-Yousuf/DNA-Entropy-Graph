using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Cloud;

namespace DnaEntropyGraph.App.Startup;

/// <summary>
/// Issue #69: the developer switch back to the simulated cloud. <c>--fake-cloud</c> or <c>DEG_FAKE_CLOUD=1</c> boots the app over
/// <see cref="CloudBackend.Developer"/> (the connected, signed-in-on-click fake and its scripted worker, no network, no project, no
/// money); nothing else does, and a shipped launch has neither. <c>--fresh-profile</c> adds an empty throwaway data folder
/// (<c>--profile</c>, issue #638) so the real history is never touched. <c>Program.Main</c> reads both once, before any window.
/// </summary>
public static class FakeCloudMode
{
    public const string Flag = "--fake-cloud";
    public const string FreshProfileFlag = "--fresh-profile";
    public const string EnvironmentVariable = "DEG_FAKE_CLOUD";

    /// <summary>The backend the process was launched over. Set once by <see cref="Resolve"/>; the real cloud until then.</summary>
    public static CloudBackend Backend { get; private set; } = CloudBackend.Google;

    /// <summary>Reads the launch arguments once: sets <see cref="Backend"/> and returns the arguments to resolve the data folder from.</summary>
    public static IReadOnlyList<string> Resolve(IReadOnlyList<string> args)
    {
        Backend = BackendFor(args, Environment.GetEnvironmentVariable);
        return WithFreshProfile(args, Path.GetTempPath());
    }

    public static CloudBackend BackendFor(IReadOnlyList<string> args, Func<string, string?> getEnvironmentVariable)
    {
        var variable = getEnvironmentVariable(EnvironmentVariable)?.Trim();
        var fromVariable = variable == "1" || string.Equals(variable, "true", StringComparison.OrdinalIgnoreCase);
        return fromVariable || args.Contains(Flag, StringComparer.OrdinalIgnoreCase) ? CloudBackend.Developer : CloudBackend.Google;
    }

    /// <summary>
    /// With <c>--fresh-profile</c>, replaces it by <c>--profile &lt;new folder under <paramref name="parent"/>&gt;</c>, unless the caller named a
    /// profile (their choice wins). The folder is created by the data-root resolution, not here.
    /// </summary>
    public static IReadOnlyList<string> WithFreshProfile(IReadOnlyList<string> args, string parent)
    {
        if (!args.Contains(FreshProfileFlag, StringComparer.OrdinalIgnoreCase))
        {
            return args;
        }

        var rest = args.Where(a => !string.Equals(a, FreshProfileFlag, StringComparison.OrdinalIgnoreCase)).ToList();
        if (rest.Any(a => a == "--profile" || a.StartsWith("--profile=", StringComparison.Ordinal)))
        {
            return rest;
        }

        return ["--profile", Path.Combine(parent, "deg-fresh-" + Guid.NewGuid().ToString("N")), .. rest];
    }

    /// <summary>
    /// DECISION (agent-made, reversible): the simulated cloud runs only on a sandbox data folder (<c>--fresh-profile</c>, <c>--profile</c> or
    /// <c>DEG_DATA_DIR</c> pointing away from the real one). Fake runs "complete" with fake results, so on the real profile they would
    /// write fake rows into the owner's real history and fake files into the real output folder, and a stray <c>DEG_FAKE_CLOUD=1</c> in the
    /// environment of a shipped build would do it silently.
    /// </summary>
    public static bool IsAllowedOn(CloudBackend backend, AppDataRoot root) => backend != CloudBackend.Developer || root.IsOverride;

    /// <summary>Called by <c>Program.Main</c> once the data folder is resolved: false (after saying why, one action) when the simulated cloud was asked for on the real profile.</summary>
    public static bool CheckProfile(AppDataRoot root)
    {
        if (IsAllowedOn(Backend, root))
        {
            return true;
        }

        StartupDataRoot.ShowMessage(new Services.ReswStringResourceProvider().GetString("FakeCloud_NeedsProfile"));
        return false;
    }

    /// <summary>The access token of the developer fake: any string, no network (the fake gateways never send it anywhere).</summary>
    public sealed class FakeTokenSource : IGcpAccessTokenSource
    {
        public Task<string> GetAccessTokenAsync(string? accountSub, CancellationToken cancellationToken) => Task.FromResult("fake-token");
    }

    /// <summary>The worker image of a developer-fake run: a pinned-looking reference the fake cloud accepts (no VM ever pulls it).</summary>
    public sealed class FakeWorkerImageProvider : IWorkerImageProvider
    {
        public WorkerImageResolution Resolve(string appVersion, bool gpu)
            => new(WorkerImageStatus.Available, "ghcr.io/raaif-yousuf/dna-entropy-worker@sha256:" + new string('0', 64));
    }
}
