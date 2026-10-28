using DnaEntropyGraph.Core.Contract;

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
    /// The platform's <c>maxRunDuration</c> is the user's cap plus this (#89): the worker enforces the cap itself
    /// (manifest <c>limits.maxRunSeconds</c>; past it the worker stops, uploads what finished and writes
    /// <c>result.json</c> with <c>RUN_TIME_LIMIT</c>) and gets time to upload and stop before Compute Engine deletes the VM.
    /// </summary>
    public static readonly TimeSpan VmGrace = TimeSpan.FromMinutes(15);

    /// <summary>
    /// THEORY (unverified): a walking-skeleton ladder in one region; the
    /// escalating-parallelism ladder over every zone that offers the tier
    /// is issue #86 and replaces this list.
    /// </summary>
    private static readonly string[] DefaultZones = ["us-central1-a", "us-central1-b", "us-central1-c", "us-central1-f"];

    /// <summary>The region a run starts in when the user pins no zone: the region of <see cref="DefaultZones"/> (the one source of truth the wizard's GPU ask also uses, #740).</summary>
    public static string DefaultRegion => ZoneCandidates.RegionOf(DefaultZones[0]);

    public static CloudJobRequest Create(
        RunOptions options,
        string jobId,
        string projectId,
        string installationId,
        string appVersion,
        string? workerImage,
        IReadOnlyList<StagedInput> inputs,
        string outputFolder)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(inputs);
        if (inputs.Count == 0)
        {
            // A request with nothing to upload is the stub this issue removed (#460): refuse it here.
            throw new ArgumentException("A run needs at least one staged input.", nameof(inputs));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(outputFolder);

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
            MaxRunDuration: TimeSpan.FromMinutes(options.MaxRunDurationMinutes) + VmGrace,
            TerminationAction: "DELETE")
        {
            Spot = options.SpotVm,
        };

        return new CloudJobRequest(
            JobId: jobId,
            Spec: spec,
            Zones: ZonesFor(options.ZonePreference),
            Options: options,
            Inputs: inputs,
            OutputFolder: outputFolder,
            AfterTask: options.AfterTask,
            WorkerImage: workerImage);
    }

    /// <summary>The <c>lifecycle</c> label value (<c>delete</c>, <c>keep</c>, <c>stop</c>) a VM for a run with this after-task choice carries.</summary>
    internal static string LifecycleLabel(AfterTaskAction action) => action switch
    {
        AfterTaskAction.Delete => "delete",
        AfterTaskAction.KeepAlive => "keep",
        _ => "stop",
    };

    /// <summary>The machine type for a GPU tier; JobEngine asks it which worker image (-cuda or -cpu) the run needs before the request exists.</summary>
    public static string MachineTypeFor(GpuTier tier)
        => GpuCatalog.Shipped.Gpus.FirstOrDefault(g => g.Tier == tier)?.MachineType
            ?? GpuCatalog.Shipped.Gpu(GpuTier.L4).MachineType; // CheapestAvailable: no ladder runs yet (#86), so it starts on the first tier, the L4.

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
