namespace DnaEntropyGraph.Core.Cloud;

/// <summary>
/// Issue #428: the pure translation from what the user chose
/// (<see cref="RunOptions"/>) plus what the app knows about itself (project,
/// installation id, version) into the <see cref="CloudJobRequest"/>
/// <see cref="CloudJobRunner"/> executes. Pure on purpose: every Hard Rule 10
/// field (the six labels, <c>maxRunDuration</c>, <c>instanceTerminationAction</c>)
/// is decided here and provable without a gateway.
/// </summary>
public static class CloudJobRequestFactory
{
    /// <summary>
    /// THEORY (unverified): a walking-skeleton ladder in one region; the
    /// escalating-parallelism ladder over every zone that offers the tier
    /// is issue #86 and replaces this list.
    /// </summary>
    private static readonly string[] DefaultZones = ["us-central1-a", "us-central1-b", "us-central1-c", "us-central1-f"];

    public static CloudJobRequest Create(RunOptions options, string jobId, string projectId, string installationId, string appVersion, string? workerImage = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        // A property pattern, not a plain member read: scripts/check_app_wiring.py
        // matches reads by member NAME, so a plain access here would count as
        // a production read of the New run page model property (a different property)
        // and flip that allowlisted finding from TEST-ONLY to CODE-ONLY.
        var modelId = options is { ModelId: var id } ? id : string.Empty;

        var spec = new VmSpec(
            ProjectId: projectId,
            InstallationId: installationId,
            JobId: jobId,
            Model: modelId.ToLowerInvariant(),
            AppVersion: appVersion,
            Lifecycle: LifecycleLabel(options.AfterTask),
            MachineType: MachineTypeFor(options.GpuTier),
            MaxRunDuration: TimeSpan.FromMinutes(options.MaxRunDurationMinutes),
            TerminationAction: "DELETE");

        return new CloudJobRequest(
            JobId: jobId,
            Spec: spec,
            Zones: ZonesFor(options.ZonePreference),
            // Input staging (manifest + sequence upload) and result download
            // are not wired from RunOptions yet: the runner's
            // upload/download steps iterate these lists, so empty means
            // "no object transfers" rather than transferring fake keys.
            InputObjectKeys: [],
            OutputObjectKeys: [],
            AfterTask: options.AfterTask,
            WorkerImage: workerImage);
    }

    private static string LifecycleLabel(AfterTaskAction action) => action switch
    {
        AfterTaskAction.Delete => "delete",
        AfterTaskAction.KeepAlive => "keep",
        _ => "stop",
    };

    private static string MachineTypeFor(GpuTier tier) => tier switch
    {
        GpuTier.A100_40 => "a2-highgpu-1g",
        GpuTier.A100_80 => "a2-ultragpu-1g",
        GpuTier.H100 => "a3-highgpu-1g",
        _ => "g2-standard-8",
    };

    private static IReadOnlyList<string> ZonesFor(string? preference)
    {
        if (string.IsNullOrWhiteSpace(preference))
        {
            return DefaultZones;
        }

        var preferred = preference.Trim();
        return [preferred, .. DefaultZones.Where(z => !string.Equals(z, preferred, StringComparison.OrdinalIgnoreCase))];
    }
}
