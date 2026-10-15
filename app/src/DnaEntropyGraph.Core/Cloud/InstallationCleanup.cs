using System.Text.Json;

namespace DnaEntropyGraph.Core.Cloud;

/// <summary>What kind of thing a <see cref="CleanupItem"/> is.</summary>
public enum CleanupKind
{
    Vm,
    Disk,
    JobData,
}

/// <summary>
/// What became of one thing. <see cref="Deleted"/> means the delete was accepted AND a read back no longer finds it; <see cref="NotVerified"/>
/// means the delete was accepted but the read back still finds it (never reported as deleted); <see cref="Skipped"/> means the cleanup chose not
/// to touch it (not labelled for this installation, or a disk still held by a VM that is not ours).
/// </summary>
public enum CleanupOutcome
{
    Deleted,
    Failed,
    NotVerified,
    Skipped,
}

/// <summary>One thing the cleanup looked at. <paramref name="Location"/> is the zone, or the bucket for job data; <paramref name="Detail"/> is a machine-readable code or note for the caller to turn into words.</summary>
public sealed record CleanupItem(CleanupKind Kind, string Name, string Location, CleanupOutcome Outcome, string? Detail = null, CloudErrorKind? ErrorKind = null);

/// <summary>The result of a cleanup. <see cref="Complete"/> is true only when every item was deleted and verified gone.</summary>
public sealed record CleanupReport(IReadOnlyList<CleanupItem> Items)
{
    public bool Complete => Items.All(i => i.Outcome == CleanupOutcome.Deleted);
}

/// <summary>
/// This installation's footprint in the results bucket: the job folders whose manifest says this installation wrote them, with their object count and bytes.
/// </summary>
public sealed record InstallationBucketUsage(string BucketName, int JobCount, int ObjectCount, long TotalBytes);

/// <summary>
/// What this installation has in the project (issue #105). <see cref="Bucket"/> is null when the project has no results bucket yet.
/// <see cref="SharedCache"/> is the model-weights cache: shared by every installation, reported for the Cloud page's size line and never part of a cleanup.
/// </summary>
public sealed record InstallationInventory(
    IReadOnlyList<VmDescriptor> Vms,
    IReadOnlyList<DiskDescriptor> Disks,
    InstallationBucketUsage? Bucket,
    PrefixUsage SharedCache);

/// <summary>
/// Issue #105: the Cloud page's "what is mine" and "delete everything". Scoped by Hard Rules 9 and 10: a VM or disk is ours only with both
/// <c>app=dna-entropy-graph</c> and this installation's <c>installation-id</c> label (checked again here on every item, so a gateway that hands
/// over a stray one never gets a delete), and a job folder in the bucket is ours only when its <c>manifest.json</c> names this installation (a folder with no
/// readable manifest belongs to nobody and is left alone, as is the shared <c>cache/</c> and <c>app-config.json</c>). Nothing is believed:
/// every delete is read back, and a listing that fails stops the cleanup BEFORE any delete, because a blind listing must never read as "nothing to do".
/// VMs go first so their disks are detached when the disks are listed again.
/// </summary>
public sealed class InstallationCleanup
{
    private const string ManifestName = "manifest.json";

    private readonly IComputeGateway _compute;
    private readonly IStorageGateway _storage;

    public InstallationCleanup(IComputeGateway compute, IStorageGateway storage)
    {
        _compute = compute;
        _storage = storage;
    }

    /// <summary>What this installation has now. A failed listing throws (<see cref="CloudOperationException"/>).</summary>
    public async Task<InstallationInventory> InventoryAsync(string projectId, string installationId, CancellationToken cancellationToken)
    {
        RequireInstallation(installationId);
        var vms = (await _compute.ListByInstallationAsync(installationId, cancellationToken).ConfigureAwait(false)).Where(v => BelongsTo(v, installationId)).ToList();
        var disks = (await _compute.ListDisksByInstallationAsync(installationId, cancellationToken).ConfigureAwait(false)).Where(d => d.BelongsTo(installationId)).ToList();
        var buckets = await _storage.ListBucketsAsync(projectId, cancellationToken).ConfigureAwait(false);

        InstallationBucketUsage? usage = null;
        var cache = new PrefixUsage(0, 0);
        foreach (var bucket in buckets)
        {
            var jobs = await OwnJobPrefixesAsync(bucket.Name, installationId, cancellationToken).ConfigureAwait(false);
            var objects = 0;
            long bytes = 0;
            foreach (var job in jobs)
            {
                var jobUsage = await _storage.GetPrefixUsageAsync(bucket.Name, job, cancellationToken).ConfigureAwait(false);
                objects += jobUsage.ObjectCount;
                bytes += jobUsage.TotalBytes;
            }

            usage = usage is null
                ? new InstallationBucketUsage(bucket.Name, jobs.Count, objects, bytes)
                : usage with { JobCount = usage.JobCount + jobs.Count, ObjectCount = usage.ObjectCount + objects, TotalBytes = usage.TotalBytes + bytes };

            var bucketCache = await _storage.GetPrefixUsageAsync(bucket.Name, ResultsBucket.CachePrefix, cancellationToken).ConfigureAwait(false);
            cache = new PrefixUsage(cache.ObjectCount + bucketCache.ObjectCount, cache.TotalBytes + bucketCache.TotalBytes);
        }

        return new InstallationInventory(vms, disks, usage, cache);
    }

    /// <summary>Deletes this installation's VMs, then its disks, then its job folders, and reports each one. See the class remarks for the guarantees.</summary>
    public async Task<CleanupReport> DeleteAllAsync(string projectId, string installationId, CancellationToken cancellationToken)
    {
        RequireInstallation(installationId);
        cancellationToken.ThrowIfCancellationRequested();

        // Every listing comes first: if any of them fails, nothing has been deleted yet.
        var vmListing = await _compute.ListByInstallationAsync(installationId, cancellationToken).ConfigureAwait(false);
        await _compute.ListDisksByInstallationAsync(installationId, cancellationToken).ConfigureAwait(false);
        var buckets = await _storage.ListBucketsAsync(projectId, cancellationToken).ConfigureAwait(false);
        var jobsByBucket = new List<(string Bucket, IReadOnlyList<string> Prefixes)>();
        foreach (var bucket in buckets)
        {
            jobsByBucket.Add((bucket.Name, await OwnJobPrefixesAsync(bucket.Name, installationId, cancellationToken).ConfigureAwait(false)));
        }

        var items = new List<CleanupItem>();

        // --- VMs
        var deletedVms = new List<VmDescriptor>();
        foreach (var vm in vmListing)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!BelongsTo(vm, installationId))
            {
                items.Add(new CleanupItem(CleanupKind.Vm, vm.Name, vm.Zone, CleanupOutcome.Skipped, NotOursCode));
                continue;
            }

            if (await TryAsync(() => _compute.DeleteVmAsync(vm.Name, vm.Zone, cancellationToken), CleanupKind.Vm, vm.Name, vm.Zone, items).ConfigureAwait(false))
            {
                deletedVms.Add(vm);
            }
        }

        foreach (var vm in deletedVms)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var still = await _compute.GetVmAsync(vm.Name, vm.Zone, cancellationToken).ConfigureAwait(false);
            items.Add(new CleanupItem(CleanupKind.Vm, vm.Name, vm.Zone, still is null ? CleanupOutcome.Deleted : CleanupOutcome.NotVerified, still is null ? null : StillThereCode));
        }

        // --- Disks: listed again now, so the ones a deleted VM held are seen detached.
        var diskListing = await _compute.ListDisksByInstallationAsync(installationId, cancellationToken).ConfigureAwait(false);
        var deletedDisks = new List<DiskDescriptor>();
        foreach (var disk in diskListing)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!disk.BelongsTo(installationId))
            {
                items.Add(new CleanupItem(CleanupKind.Disk, disk.Name, disk.Zone, CleanupOutcome.Skipped, NotOursCode));
                continue;
            }

            if (disk.Users.Count > 0)
            {
                items.Add(new CleanupItem(CleanupKind.Disk, disk.Name, disk.Zone, CleanupOutcome.Skipped, "attached to " + string.Join(", ", disk.Users)));
                continue;
            }

            if (await TryAsync(() => _compute.DeleteDiskAsync(disk.Name, disk.Zone, cancellationToken), CleanupKind.Disk, disk.Name, disk.Zone, items).ConfigureAwait(false))
            {
                deletedDisks.Add(disk);
            }
        }

        if (deletedDisks.Count > 0)
        {
            var remaining = await _compute.ListDisksByInstallationAsync(installationId, cancellationToken).ConfigureAwait(false);
            foreach (var disk in deletedDisks)
            {
                var still = remaining.Any(r => r.Name == disk.Name && r.Zone == disk.Zone);
                items.Add(new CleanupItem(CleanupKind.Disk, disk.Name, disk.Zone, still ? CleanupOutcome.NotVerified : CleanupOutcome.Deleted, still ? StillThereCode : null));
            }
        }

        // --- Job data
        foreach (var (bucket, prefixes) in jobsByBucket)
        {
            foreach (var prefix in prefixes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!await TryAsync(() => _storage.DeletePrefixAsync(bucket, prefix, cancellationToken), CleanupKind.JobData, prefix, bucket, items).ConfigureAwait(false))
                {
                    continue;
                }

                var left = await _storage.GetPrefixUsageAsync(bucket, prefix, cancellationToken).ConfigureAwait(false);
                items.Add(new CleanupItem(CleanupKind.JobData, prefix, bucket, left.ObjectCount == 0 ? CleanupOutcome.Deleted : CleanupOutcome.NotVerified, left.ObjectCount == 0 ? null : StillThereCode));
            }
        }

        return new CleanupReport(items);
    }

    /// <summary>The detail code of a <see cref="CleanupOutcome.Skipped"/> item that did not carry this installation's labels.</summary>
    public const string NotOursCode = "NOT_LABELLED_FOR_THIS_INSTALLATION";

    /// <summary>The detail code of a <see cref="CleanupOutcome.NotVerified"/> item: the delete was accepted and the read back still finds it.</summary>
    public const string StillThereCode = "STILL_THERE_AFTER_DELETE";

    /// <summary>Runs one delete; a cloud failure becomes a <see cref="CleanupOutcome.Failed"/> item (and false) so the rest still run. Cancellation is not caught.</summary>
    private static async Task<bool> TryAsync(Func<Task> delete, CleanupKind kind, string name, string location, List<CleanupItem> items)
    {
        try
        {
            await delete().ConfigureAwait(false);
            return true;
        }
        catch (CloudOperationException ex)
        {
            items.Add(new CleanupItem(kind, name, location, CleanupOutcome.Failed, ex.Error.Code, ex.Kind));
            return false;
        }
    }

    private static async Task<bool> TryAsync<T>(Func<Task<T>> delete, CleanupKind kind, string name, string location, List<CleanupItem> items)
        => await TryAsync(async () => { await delete().ConfigureAwait(false); }, kind, name, location, items).ConfigureAwait(false);

    private static bool BelongsTo(VmDescriptor vm, string installationId)
        => vm.Labels is { } labels
            && labels.TryGetValue("app", out var app) && app == VmSpec.AppLabelValue
            && labels.TryGetValue("installation-id", out var owner) && owner == installationId;

    private static void RequireInstallation(string installationId)
    {
        if (!InstallationId.IsValidLabelValue(installationId))
        {
            throw new ArgumentException("The installation id is empty or not a legal label value, so there is no safe way to say which resources are this installation's.", nameof(installationId));
        }
    }

    /// <summary>The <c>jobs/&lt;id&gt;/</c> folders in <paramref name="bucket"/> whose manifest names <paramref name="installationId"/>.</summary>
    private async Task<IReadOnlyList<string>> OwnJobPrefixesAsync(string bucket, string installationId, CancellationToken cancellationToken)
    {
        var own = new List<string>();
        foreach (var prefix in await _storage.ListChildPrefixesAsync(bucket, ResultsBucket.JobsPrefix, cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var stream = await _storage.TryDownloadAsync(bucket, prefix + ManifestName, cancellationToken).ConfigureAwait(false);
            if (stream is null)
            {
                continue;
            }

            await using (stream.ConfigureAwait(false))
            {
                if (await ManifestNamesAsync(stream, installationId, cancellationToken).ConfigureAwait(false))
                {
                    own.Add(prefix);
                }
            }
        }

        return own;
    }

    private static async Task<bool> ManifestNamesAsync(Stream manifest, string installationId, CancellationToken cancellationToken)
    {
        try
        {
            using var document = await JsonDocument.ParseAsync(manifest, cancellationToken: cancellationToken).ConfigureAwait(false);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("createdBy", out var createdBy)
                && createdBy.ValueKind == JsonValueKind.Object
                && createdBy.TryGetProperty("installationId", out var id)
                && id.ValueKind == JsonValueKind.String
                && id.GetString() == installationId;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
