using System.Text.Json;
using DnaEntropyGraph.Core.Abstractions;

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

/// <summary>One thing the cleanup looked at. <paramref name="RecoverableForDays"/> is set on a deleted job folder in a bucket with soft delete on: Google keeps a recoverable copy (and bills it) that many days, so "deleted" is never the whole truth. <paramref name="Location"/> is the zone, or the bucket for job data; <paramref name="Detail"/> is a machine-readable code or note for the caller to turn into words.</summary>
public sealed record CleanupItem(CleanupKind Kind, string Name, string Location, CleanupOutcome Outcome, string? Detail = null, CloudErrorKind? ErrorKind = null, int? RecoverableForDays = null);

/// <summary>The result of a cleanup. <see cref="Complete"/> is true only when every item was deleted and verified gone.</summary>
public sealed record CleanupReport(IReadOnlyList<CleanupItem> Items)
{
    public bool Complete => Items.All(i => i.Outcome == CleanupOutcome.Deleted);
}

/// <summary>
/// This installation's footprint in the results bucket: the job folders whose manifest says this installation wrote them, with their object count and bytes.
/// </summary>
public sealed record InstallationBucketUsage(string BucketName, int JobCount, int ObjectCount, long TotalBytes);

/// <summary>One job folder of this installation in a bucket (the key to delete is the prefix).</summary>
public sealed record JobFolderRef(string Bucket, string Prefix);

/// <summary>
/// What this installation has in the project (issue #105). <see cref="Bucket"/> is null when the project has no results bucket yet.
/// <see cref="SharedCache"/> is the model-weights cache: shared by every installation, reported for the Cloud page's size line and never part of a cleanup.
/// </summary>
public sealed record InstallationInventory(
    IReadOnlyList<VmDescriptor> Vms,
    IReadOnlyList<DiskDescriptor> Disks,
    InstallationBucketUsage? Bucket,
    PrefixUsage SharedCache)
{
    /// <summary>The job folders counted in <see cref="Bucket"/>: what a confirm dialog showed, so a delete can be limited to exactly that (#740).</summary>
    public IReadOnlyList<JobFolderRef> JobFolders { get; init; } = [];
}

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
    private readonly RunRowStore? _rows;
    private readonly ActiveRuns? _active;

    /// <param name="runs">When given, a run whose VM is deleted is ended as Cancelled (<see cref="RunErrorCodes.DeletedByCleanup"/>) instead of being left Running (Hard Rule 11).</param>
    /// <param name="activeRuns">When given, the run's driver is stopped before its VM is deleted, so it never writes over the row.</param>
    /// <param name="onPhaseChanged">Told after a row's phase is committed, like the runner's callback.</param>
    public InstallationCleanup(IComputeGateway compute, IStorageGateway storage, IRunRepository? runs = null, ActiveRuns? activeRuns = null, Action<string, JobPhase>? onPhaseChanged = null)
    {
        _compute = compute;
        _storage = storage;
        _rows = runs is null ? null : new RunRowStore(runs, onPhaseChanged);
        _active = activeRuns;
    }

    /// <summary>What this installation has now. A failed listing throws (<see cref="CloudOperationException"/>).</summary>
    public async Task<InstallationInventory> InventoryAsync(string projectId, string installationId, CancellationToken cancellationToken)
    {
        RequireInstallation(installationId);
        var vms = (await _compute.ListByInstallationAsync(installationId, cancellationToken).ConfigureAwait(false)).Where(v => BelongsTo(v, installationId)).ToList();
        var disks = (await _compute.ListDisksByInstallationAsync(installationId, cancellationToken).ConfigureAwait(false)).Where(d => d.BelongsTo(installationId)).ToList();
        var buckets = await _storage.ListBucketsAsync(projectId, cancellationToken).ConfigureAwait(false);

        InstallationBucketUsage? usage = null;
        var folders = new List<JobFolderRef>();
        var cache = new PrefixUsage(0, 0);
        foreach (var bucket in buckets)
        {
            var jobs = await OwnJobPrefixesAsync(bucket.Name, installationId, cancellationToken).ConfigureAwait(false);
            folders.AddRange(jobs.Select(j => new JobFolderRef(bucket.Name, j)));
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

        return new InstallationInventory(vms, disks, usage, cache) { JobFolders = folders };
    }

    /// <summary>
    /// Deletes this installation's VMs, then its disks, then its job folders, and reports each one. See the class remarks for the guarantees.
    /// When <paramref name="confirmed"/> is given (what a confirm dialog showed, from <see cref="InventoryAsync"/>), only those items are deleted: anything that
    /// appeared since is reported as <see cref="CleanupOutcome.Skipped"/> with <see cref="NotConfirmedCode"/> and left alone (#740).
    /// </summary>
    public async Task<CleanupReport> DeleteAllAsync(string projectId, string installationId, CancellationToken cancellationToken, InstallationInventory? confirmed = null)
    {
        RequireInstallation(installationId);
        cancellationToken.ThrowIfCancellationRequested();

        // Every listing comes first: if any of them fails, nothing has been deleted yet.
        var vmListing = await _compute.ListByInstallationAsync(installationId, cancellationToken).ConfigureAwait(false);
        await _compute.ListDisksByInstallationAsync(installationId, cancellationToken).ConfigureAwait(false);
        var buckets = await _storage.ListBucketsAsync(projectId, cancellationToken).ConfigureAwait(false);
        var jobsByBucket = new List<(BucketDescriptor Bucket, IReadOnlyList<string> Prefixes)>();
        foreach (var bucket in buckets)
        {
            jobsByBucket.Add((bucket, await OwnJobPrefixesAsync(bucket.Name, installationId, cancellationToken).ConfigureAwait(false)));
        }

        // From here on nothing aborts the sweep except the caller's cancellation: every step is contained and reported as its own item.
        var items = new List<CleanupItem>();

        // --- VMs: each is deleted and read back on its own, and the row of its run is ended once it is verified gone.
        foreach (var vm in vmListing)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!BelongsTo(vm, installationId))
            {
                items.Add(new CleanupItem(CleanupKind.Vm, vm.Name, vm.Zone, CleanupOutcome.Skipped, NotOursCode));
                continue;
            }

            if (confirmed is not null && !confirmed.Vms.Any(v => v.Name == vm.Name && v.Zone == vm.Zone))
            {
                items.Add(new CleanupItem(CleanupKind.Vm, vm.Name, vm.Zone, CleanupOutcome.Skipped, NotConfirmedCode));
                continue;
            }

            await DeleteVmAndEndItsRunAsync(vm, items, cancellationToken).ConfigureAwait(false);
        }

        // --- Disks: listed again now, so the ones a deleted VM held are seen detached.
        IReadOnlyList<DiskDescriptor>? diskListing = null;
        try
        {
            diskListing = await _compute.ListDisksByInstallationAsync(installationId, cancellationToken).ConfigureAwait(false);
        }
        catch (CloudOperationException ex)
        {
            items.Add(new CleanupItem(CleanupKind.Disk, DiskListingName, string.Empty, CleanupOutcome.Failed, ex.Error.Code, ex.Kind));
        }

        var deletedDisks = new List<DiskDescriptor>();
        foreach (var disk in diskListing ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!disk.BelongsTo(installationId))
            {
                items.Add(new CleanupItem(CleanupKind.Disk, disk.Name, disk.Zone, CleanupOutcome.Skipped, NotOursCode));
                continue;
            }

            if (confirmed is not null && !confirmed.Disks.Any(d => d.Name == disk.Name && d.Zone == disk.Zone))
            {
                items.Add(new CleanupItem(CleanupKind.Disk, disk.Name, disk.Zone, CleanupOutcome.Skipped, NotConfirmedCode));
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
            IReadOnlyList<DiskDescriptor>? remaining = null;
            CloudOperationException? readBackFailure = null;
            try
            {
                remaining = await _compute.ListDisksByInstallationAsync(installationId, cancellationToken).ConfigureAwait(false);
            }
            catch (CloudOperationException ex)
            {
                readBackFailure = ex;
            }

            foreach (var disk in deletedDisks)
            {
                if (remaining is null)
                {
                    items.Add(new CleanupItem(CleanupKind.Disk, disk.Name, disk.Zone, CleanupOutcome.NotVerified, readBackFailure!.Error.Code, readBackFailure.Kind));
                    continue;
                }

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
                if (confirmed is not null && !confirmed.JobFolders.Any(j => j.Bucket == bucket.Name && j.Prefix == prefix))
                {
                    items.Add(new CleanupItem(CleanupKind.JobData, prefix, bucket.Name, CleanupOutcome.Skipped, NotConfirmedCode));
                    continue;
                }

                try
                {
                    StoragePrefixes.RequireDeletable(prefix);
                }
                catch (ArgumentException)
                {
                    // A key such as jobs/../x/manifest.json can sit in a bucket anyone with write access filled: it is reported and never deleted.
                    items.Add(new CleanupItem(CleanupKind.JobData, prefix, bucket.Name, CleanupOutcome.Failed, PrefixRefusedCode));
                    continue;
                }

                if (!await TryAsync(() => _storage.DeletePrefixAsync(bucket.Name, prefix, cancellationToken), CleanupKind.JobData, prefix, bucket.Name, items).ConfigureAwait(false))
                {
                    continue;
                }

                var recoverable = bucket.SoftDeleteRetentionDays is > 0 ? bucket.SoftDeleteRetentionDays : null;
                try
                {
                    var left = await _storage.GetPrefixUsageAsync(bucket.Name, prefix, cancellationToken).ConfigureAwait(false);
                    items.Add(left.ObjectCount == 0
                        ? new CleanupItem(CleanupKind.JobData, prefix, bucket.Name, CleanupOutcome.Deleted, RecoverableForDays: recoverable)
                        : new CleanupItem(CleanupKind.JobData, prefix, bucket.Name, CleanupOutcome.NotVerified, StillThereCode));
                }
                catch (CloudOperationException ex)
                {
                    items.Add(new CleanupItem(CleanupKind.JobData, prefix, bucket.Name, CleanupOutcome.NotVerified, ex.Error.Code, ex.Kind));
                }
            }
        }

        return new CleanupReport(items);
    }

    /// <summary>The detail code of a <see cref="CleanupOutcome.Skipped"/> item that did not carry this installation's labels.</summary>
    public const string NotOursCode = "NOT_LABELLED_FOR_THIS_INSTALLATION";

    /// <summary>The detail code of a <see cref="CleanupOutcome.Skipped"/> item that was not in the inventory the user confirmed (it appeared after the dialog was shown).</summary>
    public const string NotConfirmedCode = "NOT_IN_CONFIRMED_LIST";

    /// <summary>The detail code of a <see cref="CleanupOutcome.NotVerified"/> item: the delete was accepted and the read back still finds it.</summary>
    public const string StillThereCode = "STILL_THERE_AFTER_DELETE";

    /// <summary>The detail code of a job folder whose key is not a folder path a delete may use (see <see cref="StoragePrefixes.RequireDeletable"/>): nothing was deleted.</summary>
    public const string PrefixRefusedCode = "PREFIX_REFUSED";

    /// <summary>The detail code of a VM left alone because a user cancel of its run was already in progress (that cancel owns the VM).</summary>
    public const string CancelInProgressCode = "CANCEL_IN_PROGRESS";

    /// <summary>The name of the <see cref="CleanupKind.Disk"/> item reported when the second disk listing failed (no disk could be looked at).</summary>
    public const string DiskListingName = "(disk listing)";

    /// <summary>
    /// Deletes one VM and reads it back, contained: a failed delete or read back is an item, never an exception. When the VM carries a <c>job-id</c> the
    /// work runs under <see cref="ActiveRuns.CancelAsync"/>, so the run's driver is stopped first and one writer owns the row; a VM verified gone then
    /// ends the run's row (Hard Rule 11: a run whose VM is gone is never left Running).
    /// </summary>
    private async Task DeleteVmAndEndItsRunAsync(VmDescriptor vm, List<CleanupItem> items, CancellationToken cancellationToken)
    {
        var jobId = vm.Labels is { } labels && labels.TryGetValue("job-id", out var id) ? id : null;
        var ran = false;

        async Task BodyAsync()
        {
            ran = true;
            if (!await TryAsync(() => _compute.DeleteVmAsync(vm.Name, vm.Zone, cancellationToken), CleanupKind.Vm, vm.Name, vm.Zone, items).ConfigureAwait(false))
            {
                return;
            }

            VmDescriptor? still;
            try
            {
                still = await _compute.GetVmAsync(vm.Name, vm.Zone, cancellationToken).ConfigureAwait(false);
            }
            catch (CloudOperationException ex)
            {
                items.Add(new CleanupItem(CleanupKind.Vm, vm.Name, vm.Zone, CleanupOutcome.NotVerified, ex.Error.Code, ex.Kind));
                return;
            }

            items.Add(new CleanupItem(CleanupKind.Vm, vm.Name, vm.Zone, still is null ? CleanupOutcome.Deleted : CleanupOutcome.NotVerified, still is null ? null : StillThereCode));
            if (still is null && jobId is not null)
            {
                await EndRunRowAsync(jobId).ConfigureAwait(false);
            }
        }

        if (_active is not null && jobId is not null)
        {
            await _active.CancelAsync(jobId, BodyAsync).ConfigureAwait(false);
            if (!ran)
            {
                items.Add(new CleanupItem(CleanupKind.Vm, vm.Name, vm.Zone, CleanupOutcome.Skipped, CancelInProgressCode));
            }
        }
        else
        {
            await BodyAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Ends a non-terminal run row as Cancelled under <see cref="RunErrorCodes.DeletedByCleanup"/>. A row already terminal, or none, is left alone; a failed write is not the cleanup's failure (the VM is gone and the reconciler looks again).</summary>
    private async Task EndRunRowAsync(string jobId)
    {
        if (_rows is null)
        {
            return;
        }

        try
        {
            var row = await _rows.LatestRecordAsync(jobId, CancellationToken.None).ConfigureAwait(false);
            if (row is null || JobStateMachine.IsTerminal(row.Phase))
            {
                return;
            }

            if (row.Phase != JobPhase.Cancelling)
            {
                await _rows.SetPhaseAsync(jobId, JobPhase.Cancelling, CancellationToken.None).ConfigureAwait(false);
            }

            await _rows.SetPhaseAsync(jobId, JobPhase.Cancelled, CancellationToken.None, RunErrorCodes.DeletedByCleanup).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // See the summary.
        }
    }

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
        catch (ArgumentException)
        {
            items.Add(new CleanupItem(kind, name, location, CleanupOutcome.Failed, PrefixRefusedCode));
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
