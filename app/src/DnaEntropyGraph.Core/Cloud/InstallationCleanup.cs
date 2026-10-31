using System.Collections.Concurrent;
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

/// <summary>What <see cref="InstallationCleanup.EndVmAsync"/> did: <see cref="Done"/> (request accepted, run ended), <see cref="Failed"/> (refused or never accepted), <see cref="AlreadyEnding"/> (another end for the same job is in flight).</summary>
public enum VmEndResult
{
    Done,
    Failed,
    AlreadyEnding,
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
    private const string ManifestName = ResultsBucket.ManifestObject;

    private readonly IComputeGateway _compute;
    private readonly IStorageGateway _storage;
    private readonly RunRowStore? _rows;
    private readonly ActiveRuns? _active;
    private readonly TimeProvider _clock;

    /// <param name="runs">When given, a run whose VM is deleted is ended as Cancelled (<see cref="RunErrorCodes.DeletedByCleanup"/>) instead of being left Running (Hard Rule 11).</param>
    /// <param name="activeRuns">When given, the run's driver is stopped before its VM is deleted, so it never writes over the row.</param>
    /// <param name="onPhaseChanged">Told after a row's phase is committed, like the runner's callback.</param>
    public InstallationCleanup(IComputeGateway compute, IStorageGateway storage, IRunRepository? runs = null, ActiveRuns? activeRuns = null, Action<string, JobPhase>? onPhaseChanged = null, TimeProvider? clock = null)
    {
        _clock = clock ?? TimeProvider.System;
        _compute = compute;
        _storage = storage;
        _rows = runs is null ? null : new RunRowStore(runs, onPhaseChanged);
        _active = activeRuns;
    }

    /// <summary>What this installation has now. A failed listing throws (<see cref="CloudOperationException"/>).</summary>
    public async Task<InstallationInventory> InventoryAsync(CloudScope scope, string installationId, CancellationToken cancellationToken)
    {
        RequireInstallation(installationId);
        var vms = (await _compute.ListByInstallationAsync(scope, installationId, cancellationToken).ConfigureAwait(false)).Where(v => BelongsTo(v, installationId)).ToList();
        var disks = (await _compute.ListDisksByInstallationAsync(scope, installationId, cancellationToken).ConfigureAwait(false)).Where(d => d.BelongsTo(installationId)).ToList();
        var buckets = await _storage.ListBucketsAsync(scope, cancellationToken).ConfigureAwait(false);

        InstallationBucketUsage? usage = null;
        var folders = new List<JobFolderRef>();
        var cache = new PrefixUsage(0, 0);
        foreach (var bucket in buckets)
        {
            var jobs = await OwnJobPrefixesAsync(scope, bucket.Name, installationId, cancellationToken).ConfigureAwait(false);
            folders.AddRange(jobs.Select(j => new JobFolderRef(bucket.Name, j)));
            var objects = 0;
            long bytes = 0;
            foreach (var job in jobs)
            {
                var jobUsage = await _storage.GetPrefixUsageAsync(scope, bucket.Name, job, cancellationToken).ConfigureAwait(false);
                objects += jobUsage.ObjectCount;
                bytes += jobUsage.TotalBytes;
            }

            usage = usage is null
                ? new InstallationBucketUsage(bucket.Name, jobs.Count, objects, bytes)
                : usage with { JobCount = usage.JobCount + jobs.Count, ObjectCount = usage.ObjectCount + objects, TotalBytes = usage.TotalBytes + bytes };

            var bucketCache = await _storage.GetPrefixUsageAsync(scope, bucket.Name, ResultsBucket.CachePrefix, cancellationToken).ConfigureAwait(false);
            cache = new PrefixUsage(cache.ObjectCount + bucketCache.ObjectCount, cache.TotalBytes + bucketCache.TotalBytes);
        }

        return new InstallationInventory(vms, disks, usage, cache) { JobFolders = folders };
    }

    /// <summary>
    /// Deletes this installation's VMs, then its disks, then its job folders, and reports each one. See the class remarks for the guarantees.
    /// When <paramref name="confirmed"/> is given (what a confirm dialog showed, from <see cref="InventoryAsync"/>), only those items are deleted: anything that
    /// appeared since is reported as <see cref="CleanupOutcome.Skipped"/> with <see cref="NotConfirmedCode"/> and left alone (#740).
    /// </summary>
    public async Task<CleanupReport> DeleteAllAsync(CloudScope scope, string installationId, CancellationToken cancellationToken, InstallationInventory? confirmed = null)
    {
        RequireInstallation(installationId);
        cancellationToken.ThrowIfCancellationRequested();

        // Every listing comes first: if any of them fails, nothing has been deleted yet.
        var vmListing = await _compute.ListByInstallationAsync(scope, installationId, cancellationToken).ConfigureAwait(false);
        await _compute.ListDisksByInstallationAsync(scope, installationId, cancellationToken).ConfigureAwait(false);
        var buckets = await _storage.ListBucketsAsync(scope, cancellationToken).ConfigureAwait(false);
        var jobsByBucket = new List<(BucketDescriptor Bucket, IReadOnlyList<string> Prefixes)>();
        foreach (var bucket in buckets)
        {
            jobsByBucket.Add((bucket, await OwnJobPrefixesAsync(scope, bucket.Name, installationId, cancellationToken).ConfigureAwait(false)));
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

            if (confirmed is not null && !confirmed.Vms.Any(v => SameResource(v.Name, v.Zone, v.CreatedAt, vm.Name, vm.Zone, vm.CreatedAt)))
            {
                items.Add(new CleanupItem(CleanupKind.Vm, vm.Name, vm.Zone, CleanupOutcome.Skipped, NotConfirmedCode));
                continue;
            }

            try
            {
                await DeleteVmAndEndItsRunAsync(scope, vm, items, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Whatever escaped the body (ActiveRuns.CancelAsync lets its exception reach the caller) is this computer's failure, never the sweep's: the ones before it are
                // already gone and the report must say so, and the ones after it still get their turn (#740).
                if (!items.Any(i => i.Kind == CleanupKind.Vm && i.Name == vm.Name && i.Location == vm.Zone))
                {
                    items.Add(FailedItem(CleanupKind.Vm, vm.Name, vm.Zone, ex));
                }
            }
        }

        // --- Disks: listed again now, so the ones a deleted VM held are seen detached.
        IReadOnlyList<DiskDescriptor>? diskListing = null;
        try
        {
            diskListing = await _compute.ListDisksByInstallationAsync(scope, installationId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            items.Add(FailedItem(CleanupKind.Disk, DiskListingName, string.Empty, ex));
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

            if (confirmed is not null && !confirmed.Disks.Any(d => SameResource(d.Name, d.Zone, d.CreatedAt, disk.Name, disk.Zone, disk.CreatedAt)))
            {
                items.Add(new CleanupItem(CleanupKind.Disk, disk.Name, disk.Zone, CleanupOutcome.Skipped, NotConfirmedCode));
                continue;
            }

            if (disk.Users.Count > 0)
            {
                items.Add(new CleanupItem(CleanupKind.Disk, disk.Name, disk.Zone, CleanupOutcome.Skipped, "attached to " + string.Join(", ", disk.Users)));
                continue;
            }

            if (await TryAsync(() => _compute.DeleteDiskAsync(scope, disk.Name, disk.Zone, cancellationToken), CleanupKind.Disk, disk.Name, disk.Zone, items).ConfigureAwait(false))
            {
                deletedDisks.Add(disk);
            }
        }

        if (deletedDisks.Count > 0)
        {
            IReadOnlyList<DiskDescriptor>? remaining = null;
            Exception? readBackFailure = null;
            try
            {
                remaining = await _compute.ListDisksByInstallationAsync(scope, installationId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                readBackFailure = ex;
            }

            foreach (var disk in deletedDisks)
            {
                if (remaining is null)
                {
                    items.Add(FailedItem(CleanupKind.Disk, disk.Name, disk.Zone, readBackFailure!) with { Outcome = CleanupOutcome.NotVerified });
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

                if (!await TryAsync(() => _storage.DeletePrefixAsync(scope, bucket.Name, prefix, cancellationToken), CleanupKind.JobData, prefix, bucket.Name, items).ConfigureAwait(false))
                {
                    continue;
                }

                var recoverable = bucket.SoftDeleteRetentionDays is > 0 ? bucket.SoftDeleteRetentionDays : null;
                try
                {
                    var left = await _storage.GetPrefixUsageAsync(scope, bucket.Name, prefix, cancellationToken).ConfigureAwait(false);
                    items.Add(left.ObjectCount == 0
                        ? new CleanupItem(CleanupKind.JobData, prefix, bucket.Name, CleanupOutcome.Deleted, RecoverableForDays: recoverable)
                        : new CleanupItem(CleanupKind.JobData, prefix, bucket.Name, CleanupOutcome.NotVerified, StillThereCode));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    items.Add(FailedItem(CleanupKind.JobData, prefix, bucket.Name, ex) with { Outcome = CleanupOutcome.NotVerified });
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

    /// <summary>The longest <see cref="EndVmAsync"/> waits for a run's driver to stop once the request has been accepted, before it writes the run's end anyway.</summary>
    public static readonly TimeSpan DriverStopWait = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Issue #113: stops or deletes ONE computer of this installation (the exit window's Stop / Delete). The Compute request is SENT FIRST and nothing else is touched until Google
    /// has ACCEPTED it (<see cref="IComputeGateway.EndVmAsync"/> reports that moment, not the end of the operation): <paramref name="requestAccepted"/> is called then (the caller may
    /// close the app and say it finishes on Google's side), and only then is the job's driver stopped, so a refused request (403, quota) leaves the run, its driver and its row as
    /// they were. The run's end is written right after acceptance, with the driver wait bounded by <see cref="DriverStopWait"/> and neither the wait nor the write taking the caller's
    /// token: a run whose computer is stopped or deleted is ended as Cancelled (<see cref="RunErrorCodes.DeletedByCleanup"/> for a delete, <see cref="RunErrorCodes.StoppedByCleanup"/>
    /// for a stop), never left Running (Hard Rule 11). Refuses a computer not labelled for <paramref name="installationId"/> (<see cref="VmEndResult.Failed"/>, nothing sent).
    /// An end already in flight for the same job that this cleanup started is joined: its outcome is awaited (bounded by <see cref="InFlightWait"/>, then <see cref="VmEndResult.Failed"/>),
    /// reported as it was (accepted: <see cref="VmEndResult.Done"/>; refused: this call makes its own attempt), and a Delete asked behind an accepted Stop is sent. A cancel some other
    /// code owns (the runner's, "Delete everything") is <see cref="VmEndResult.AlreadyEnding"/>. A failure of the operation AFTER it was accepted is <see cref="VmEndResult.Failed"/>
    /// (the row already says the run ended). Never throws for a cloud failure.
    /// </summary>
    public async Task<VmEndResult> EndVmAsync(CloudScope cloud, string installationId, VmDescriptor vm, bool delete, Action? requestAccepted, CancellationToken cancellationToken)
    {
        RequireInstallation(installationId);
        if (!BelongsTo(vm, installationId))
        {
            return VmEndResult.Failed;
        }

        var jobId = vm.Labels is { } labels && labels.TryGetValue("job-id", out var id) ? id : null;
        for (var attempt = 0; ; attempt++)
        {
            ActiveRuns.CancelScope? scope = null;
            InFlightEnd? mine = null;
            InFlightEnd? first = null;
            if (_active is not null && jobId is not null)
            {
                // One step: look for an end of ours still in flight, and if there is none take the job and publish our own entry. A second caller therefore always
                // finds either our entry or somebody else's cancel, never the gap between taking the job and registering.
                lock (_endGate)
                {
                    if (!_ending.TryGetValue(jobId, out first))
                    {
                        scope = _active.TryBeginCancel(jobId);
                        if (scope is not null)
                        {
                            mine = new InFlightEnd(delete);
                            _ending[jobId] = mine;
                        }
                    }
                }

                if (first is null && scope is null)
                {
                    // A cancel some other code owns (the runner's, "Delete everything"): reported as such, not claimed.
                    return VmEndResult.AlreadyEnding;
                }
            }

            if (first is null)
            {
                return await EndOwnedAsync(cloud, scope, mine, jobId, vm, delete, requestAccepted, cancellationToken).ConfigureAwait(false);
            }

            // An end of ours for this job is in flight: its outcome is known (and awaited, bounded).
            bool firstAccepted;
            try
            {
                firstAccepted = await first.Accepted.Task.WaitAsync(InFlightWait, _clock, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // Neither accepted nor refused in time: nothing is known to be finishing, so nothing is claimed.
                return VmEndResult.Failed;
            }

            if (!firstAccepted)
            {
                // The first attempt was refused and has let go of the job (or is about to): make our own.
                if (attempt >= MaxJoinAttempts)
                {
                    return VmEndResult.Failed;
                }

                await Task.Yield();
                continue;
            }

            if (first.Delete || !delete)
            {
                Notify(requestAccepted); // what was asked is already accepted and on its way
                return VmEndResult.Done;
            }

            return await DeleteBehindAcceptedStopAsync(cloud, jobId!, first, vm, requestAccepted, cancellationToken).ConfigureAwait(false);
        }
    }

    private readonly object _endGate = new();

    /// <summary>How many times an end that found another end in flight and saw it refused tries again by itself.</summary>
    private const int MaxJoinAttempts = 3;

    /// <summary>The longest an end waits to learn how the end already in flight for the same job went (accepted, or refused) before it says it does not know.</summary>
    public static readonly TimeSpan InFlightWait = TimeSpan.FromSeconds(20);

    /// <summary>What an end in flight for one job has so far: whether Google accepted its request, and when the run row was written.</summary>
    private sealed class InFlightEnd(bool delete)
    {
        public bool Delete { get; } = delete;

        /// <summary>True when Google accepted the request, false when it was refused or failed first. Never faults.</summary>
        public TaskCompletionSource<bool> Accepted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Completes when the run row's end has been written (or there was nothing to write). Never faults.</summary>
        public TaskCompletionSource RowEnded { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Set when a Delete was accepted behind this end (a Stop): that Delete's outcome, which this end reports if its own operation ends in error (the computer it was stopping is gone).</summary>
        public volatile Task<VmEndResult>? Superseder;
    }

    private readonly ConcurrentDictionary<string, InFlightEnd> _ending = new();

    private static void Notify(Action? callback)
    {
        try
        {
            callback?.Invoke();
        }
        catch (Exception)
        {
            // A callback that throws is the caller's problem, never the end's.
        }
    }

    /// <summary>
    /// Sends the Compute request and returns two tasks: whether Google ACCEPTED it (true as soon as the gateway says so, or when it finished without saying; false when it
    /// failed first), and the whole call (which ends when the operation is done and carries what it ended with).
    /// </summary>
    private (Task<bool> Accepted, Task Operation) Send(CloudScope cloud, VmDescriptor vm, bool delete, Action onAccepted, CancellationToken cancellationToken)
    {
        var accepted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task operation;
        try
        {
            operation = _compute.EndVmAsync(cloud, vm.Name, vm.Zone, delete, () =>
            {
                onAccepted();
                accepted.TrySetResult(true);
            }, cancellationToken);
        }
        catch (Exception ex)
        {
            operation = Task.FromException(ex);
        }

        _ = operation.ContinueWith(
            t => accepted.TrySetResult(t.Status == TaskStatus.RanToCompletion),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return (accepted.Task, operation);
    }

    /// <summary>
    /// The end this call owns: the request goes out FIRST, and nothing else is touched until Google has accepted it. Only then is the job's driver stopped (a refused request
    /// leaves the run, its driver and its row exactly as they were), the run's row ended (the driver wait is bounded, and neither the wait nor the write takes the caller's token:
    /// once the request is accepted the run's end is recorded, Hard Rule 11), and the operation awaited to its end.
    /// </summary>
    private async Task<VmEndResult> EndOwnedAsync(CloudScope cloud, ActiveRuns.CancelScope? scope, InFlightEnd? entry, string? jobId, VmDescriptor vm, bool delete, Action? requestAccepted, CancellationToken cancellationToken)
    {
        // The job (the cancel scope) is let go of as soon as the run's row is ended: a runner cancel, "Delete everything" or the reconciler must not be told "already ending" or wait
        // out the operation, which for a GPU stop is minutes. The entry stays published until the operation is done, so a second end for this job still joins this one.
        void Release() => scope?.Dispose();

        void Forget()
        {
            if (entry is not null && jobId is not null)
            {
                lock (_endGate)
                {
                    _ending.TryRemove(new KeyValuePair<string, InFlightEnd>(jobId, entry));
                }
            }
        }

        try
        {
            var (acceptedTask, operation) = Send(cloud, vm, delete, () =>
            {
                Notify(requestAccepted);
                entry?.Accepted.TrySetResult(true);
            }, cancellationToken);

            if (!await acceptedTask.ConfigureAwait(false))
            {
                // Refused (or cancelled) before Google accepted it: let go of the job BEFORE saying so, so whoever waits behind us can try for itself.
                Release();
                Forget();
                entry?.Accepted.TrySetResult(false);
                entry?.RowEnded.TrySetResult();
                try
                {
                    await operation.ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Reported as Failed below; the class of the failure is the caller's to learn from the cloud log, not from here.
                }

                return VmEndResult.Failed;
            }

            if (jobId is not null)
            {
                var driver = scope?.StopDriverAsync() ?? Task.CompletedTask;
                try
                {
                    await driver.WaitAsync(DriverStopWait, _clock, CancellationToken.None).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    // A driver that will not stop does not keep the run Running: its end is written below.
                }

                await EndRunRowAsync(jobId, delete ? RunErrorCodes.DeletedByCleanup : RunErrorCodes.StoppedByCleanup).ConfigureAwait(false);
            }

            entry?.RowEnded.TrySetResult();
            Release();
            try
            {
                await operation.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // The caller stopped waiting for the operation to finish; Google accepted it and the run's end is recorded.
            }
            catch (Exception)
            {
                // A Delete accepted behind this end supersedes it: this operation (a Stop) ending in error is what deleting the computer under it does, so the Delete's outcome is reported.
                if (entry?.Superseder is { } superseder)
                {
                    return await superseder.ConfigureAwait(false);
                }

                // THEORY (unverified): the operation failed after being accepted (nothing measured). The row already says the run ended and the VM may still be running:
                // that is Failed, and the next scan finds a running computer with no live run and says so.
                return VmEndResult.Failed;
            }

            return VmEndResult.Done;
        }
        finally
        {
            Release();
            Forget();
            entry?.Accepted.TrySetResult(false); // only reached unresolved when the call itself was abandoned by an exception
            entry?.RowEnded.TrySetResult();
        }
    }

    /// <summary>
    /// A Stop of this job is accepted and still finishing while the user now wants a Delete: the Delete is sent, and once the first end has written the run's row the row's
    /// error code is changed from the stop's to the delete's, so the history says what became of the computer.
    /// </summary>
    private async Task<VmEndResult> DeleteBehindAcceptedStopAsync(CloudScope cloud, string jobId, InFlightEnd first, VmDescriptor vm, Action? requestAccepted, CancellationToken cancellationToken)
    {
        var outcome = new TaskCompletionSource<VmEndResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var result = await DeleteBehindCoreAsync(cloud, jobId, first, vm, outcome, requestAccepted, cancellationToken).ConfigureAwait(false);
            outcome.TrySetResult(result);
            return result;
        }
        finally
        {
            outcome.TrySetResult(VmEndResult.Failed);
        }
    }

    private async Task<VmEndResult> DeleteBehindCoreAsync(CloudScope cloud, string jobId, InFlightEnd first, VmDescriptor vm, TaskCompletionSource<VmEndResult> outcome, Action? requestAccepted, CancellationToken cancellationToken)
    {
        // The moment the Delete is accepted the Stop it supersedes is told: from then on that Stop's own error is not the computer's outcome, this Delete's is.
        var (acceptedTask, operation) = Send(cloud, vm, true, () =>
        {
            first.Superseder = outcome.Task;
            Notify(requestAccepted);
        }, cancellationToken);
        if (!await acceptedTask.ConfigureAwait(false))
        {
            try
            {
                await operation.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Reported as Failed below.
            }

            return VmEndResult.Failed;
        }

        try
        {
            await first.RowEnded.Task.WaitAsync(InFlightWait, _clock, CancellationToken.None).ConfigureAwait(false);
            await RelabelStoppedRowAsync(jobId).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // The first end never wrote its row in time; the row keeps the stop's code.
        }

        try
        {
            await operation.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // See EndOwnedAsync.
        }
        catch (Exception)
        {
            return VmEndResult.Failed;
        }

        return VmEndResult.Done;
    }

    private async Task RelabelStoppedRowAsync(string jobId)
    {
        if (_rows is null)
        {
            return;
        }

        try
        {
            await _rows.UpdateRowAsync(
                jobId,
                row => row.Phase == JobPhase.Cancelled && row.ErrorCode == RunErrorCodes.StoppedByCleanup ? row with { ErrorCode = RunErrorCodes.DeletedByCleanup } : row,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // History text only; the VM is being deleted either way.
        }
    }

    /// <summary>
    /// Deletes one VM and reads it back, contained: a failed delete or read back is an item, never an exception. When the VM carries a <c>job-id</c> the
    /// work runs under <see cref="ActiveRuns.CancelAsync"/>, so the run's driver is stopped first and one writer owns the row; a VM verified gone then
    /// ends the run's row (Hard Rule 11: a run whose VM is gone is never left Running).
    /// </summary>
    private async Task DeleteVmAndEndItsRunAsync(CloudScope scope, VmDescriptor vm, List<CleanupItem> items, CancellationToken cancellationToken)
    {
        var jobId = vm.Labels is { } labels && labels.TryGetValue("job-id", out var id) ? id : null;
        var ran = false;

        async Task BodyAsync()
        {
            ran = true;
            if (!await TryAsync(() => _compute.DeleteVmAsync(scope, vm.Name, vm.Zone, cancellationToken), CleanupKind.Vm, vm.Name, vm.Zone, items).ConfigureAwait(false))
            {
                return;
            }

            VmDescriptor? still;
            try
            {
                still = await _compute.GetVmAsync(scope, vm.Name, vm.Zone, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                items.Add(FailedItem(CleanupKind.Vm, vm.Name, vm.Zone, ex) with { Outcome = CleanupOutcome.NotVerified });
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
    private async Task EndRunRowAsync(string jobId, string errorCode = RunErrorCodes.DeletedByCleanup)
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

            await _rows.SetPhaseAsync(jobId, JobPhase.Cancelled, CancellationToken.None, errorCode).ConfigureAwait(false);
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
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            items.Add(FailedItem(kind, name, location, ex));
            return false;
        }
    }

    private static async Task<bool> TryAsync<T>(Func<Task<T>> delete, CleanupKind kind, string name, string location, List<CleanupItem> items)
        => await TryAsync(async () => { await delete().ConfigureAwait(false); }, kind, name, location, items).ConfigureAwait(false);
    /// <summary>A Failed item for an exception: the cloud error's code and kind, else only the exception's class name (never its message, which can carry a name or a path).</summary>
    private static CleanupItem FailedItem(CleanupKind kind, string name, string location, Exception ex)
        => ex is CloudOperationException cloud
            ? new CleanupItem(kind, name, location, CleanupOutcome.Failed, cloud.Error.Code, cloud.Kind)
            : new CleanupItem(kind, name, location, CleanupOutcome.Failed, ex.GetType().Name);

    /// <summary>
    /// The same resource as the one the user was shown: name and zone, and the creation time when both sides have one (a computer or disk deleted and created again under the
    /// same name is a different resource). THEORY (unverified): the gateways report Compute's creationTimestamp; the descriptors carry no numeric resource id.
    /// </summary>
    private static bool SameResource(string name, string zone, DateTimeOffset? createdAt, string otherName, string otherZone, DateTimeOffset? otherCreatedAt)
        => name == otherName && zone == otherZone && (createdAt is null || otherCreatedAt is null || createdAt == otherCreatedAt);

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
    private async Task<IReadOnlyList<string>> OwnJobPrefixesAsync(CloudScope scope, string bucket, string installationId, CancellationToken cancellationToken)
    {
        var own = new List<string>();
        foreach (var prefix in await _storage.ListChildPrefixesAsync(scope, bucket, ResultsBucket.JobsPrefix, cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var stream = await _storage.TryDownloadAsync(scope, bucket, prefix + ManifestName, cancellationToken).ConfigureAwait(false);
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
