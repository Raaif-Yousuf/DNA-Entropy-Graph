using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Cloud;

namespace DnaEntropyGraph.App.Services;

/// <summary>
/// Issue #458: the worker image comes from the list pinned by digest that ships
/// with the app (<c>worker-images.json</c>, embedded), never from a setting a user
/// has to know about. The same list is the allowlist (CLAUDE.md Stack row
/// "Container"): a reference outside it is refused unless developer mode is on,
/// and even then it must be pinned by digest, the only shape the VM startup
/// script can run.
/// </summary>
public sealed class PinnedWorkerImageProvider : IWorkerImageProvider
{
    /// <summary>Settings key whose value <c>true</c> allows a <see cref="WorkerImageOverrideSettingsKey"/> outside the pinned list. Read from <c>settings.json</c>; there is no UI for it on purpose.</summary>
    public const string DeveloperModeSettingsKey = "developer_mode";

    /// <summary>Settings key holding a developer's own digest-pinned worker image. Accepted only when it is the pinned image for this version and variant, or developer mode is on.</summary>
    public const string WorkerImageOverrideSettingsKey = "worker_image";

    private const string ResourceName = "worker-images.json";

    private readonly ISettingsStore _settings;
    private readonly PinnedWorkerImageList _list;

    public PinnedWorkerImageProvider(ISettingsStore settings, PinnedWorkerImageList list)
    {
        _settings = settings;
        _list = list;
    }

    /// <summary>Reads the list embedded in this assembly. Throws if it is missing or unreadable: a broken shipped list must fail loudly, not look like "no image shipped".</summary>
    public static PinnedWorkerImageList LoadShippedList()
    {
        using var stream = typeof(PinnedWorkerImageProvider).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"{ResourceName} is not embedded in DnaEntropyGraph.App; check the EmbeddedResource item in its csproj.");
        using var reader = new StreamReader(stream);
        return PinnedWorkerImageList.Parse(reader.ReadToEnd());
    }

    public WorkerImageResolution Resolve(string appVersion, bool gpu)
    {
        var requested = ReadSetting(WorkerImageOverrideSettingsKey)?.Trim();
        if (!string.IsNullOrEmpty(requested))
        {
            if (_list.Allows(requested, appVersion, gpu))
            {
                return new WorkerImageResolution(WorkerImageStatus.Available, requested);
            }

            return DeveloperMode() && StartupMetadata.IsPinnedImageReference(requested)
                ? new WorkerImageResolution(WorkerImageStatus.Available, requested, IsDeveloperOverride: true)
                : new WorkerImageResolution(WorkerImageStatus.OverrideRefused, null);
        }

        var pinned = _list.Find(appVersion, gpu);
        return pinned is null
            ? new WorkerImageResolution(WorkerImageStatus.NoneShipped, null)
            : new WorkerImageResolution(WorkerImageStatus.Available, pinned);
    }

    private bool DeveloperMode()
        => string.Equals(ReadSetting(DeveloperModeSettingsKey)?.Trim(), "true", StringComparison.OrdinalIgnoreCase);

    // #558: a locked settings file means "no override, no developer mode" (the safe defaults),
    // never an exception out of the run-start path.
    private string? ReadSetting(string key)
    {
        try
        {
            return _settings.GetString(key);
        }
        catch (SettingsUnavailableException)
        {
            return null;
        }
    }
}
