using System.Collections.Concurrent;
using DnaEntropyGraph.Core.Cloud;

namespace DnaEntropyGraph.Cloud;

// Issue #105: the Cloud page's gateway methods on the fake: start a VM, the label-filtered disk listing, disk delete, and the bucket
// prefix usage / child listing / delete. Scripted failures follow the rest of this class: fluent With* setters, errors built with the
// kind the real gateway would classify them as.
public sealed partial class FakeGcp
{
    private readonly ConcurrentDictionary<(string Name, string Zone), DiskDescriptor> _disks = new();
    private readonly ConcurrentDictionary<string, CloudErrorKind> _deleteFailures = new(StringComparer.Ordinal);
    private CloudErrorKind? _startFailure;
    private bool _silentNoOpDeletes;
    private CloudErrorKind? _getVmFailure;
    private Action<string>? _afterPrefixDelete;
    private int? _bucketSoftDeleteDays;
    private readonly HashSet<string> _missingBuckets = new(StringComparer.Ordinal);
    private int? _prefixDeleteFailAfter;
    private CloudErrorKind _prefixDeleteFailKind;

    /// <summary>Every disk this fake holds, by name: the observable for "a disk was really deleted".</summary>
    public IReadOnlyList<DiskDescriptor> Disks => _disks.Values.OrderBy(d => d.Name, StringComparer.Ordinal).ToList();

    /// <summary>Puts a disk in the fake project, as if an earlier VM had left it (labelled or not, attached or not).</summary>
    public FakeGcp WithDisk(DiskDescriptor disk)
    {
        _disks[(disk.Name, disk.Zone)] = disk with { AttachedTo = disk.Users };
        return this;
    }

    /// <summary>Every <see cref="StartVmAsync"/> fails with <paramref name="kind"/> (a stockout, billing off, quota, permission...), classified as the real gateway would.</summary>
    public FakeGcp WithStartFailure(CloudErrorKind kind)
    {
        _startFailure = kind;
        return this;
    }

    /// <summary>
    /// The delete of the one VM, disk or bucket prefix called <paramref name="name"/> fails with <paramref name="kind"/> and removes nothing;
    /// every other delete is unaffected (so a test shows one failure not stopping the rest).
    /// </summary>
    public FakeGcp WithDeleteFailure(string name, CloudErrorKind kind)
    {
        _deleteFailures[name] = kind;
        return this;
    }

    /// <summary>
    /// Every VM, disk and prefix delete reports success and removes nothing: what a platform that accepted a delete and did not do it looks like.
    /// A cleanup that believes its own delete call reads "deleted"; one that reads back sees the truth.
    /// </summary>
    public FakeGcp WithSilentNoOpDeletes()
    {
        _silentNoOpDeletes = true;
        return this;
    }

    /// <summary>Every <see cref="GetVmAsync"/> fails with <paramref name="kind"/>: a read back that cannot be made.</summary>
    public FakeGcp WithGetVmFailure(CloudErrorKind kind)
    {
        _getVmFailure = kind;
        return this;
    }

    /// <summary>Runs <paramref name="hook"/> with the prefix after each prefix delete (a test cancels mid-sweep with it).</summary>
    public FakeGcp AfterPrefixDelete(Action<string> hook)
    {
        _afterPrefixDelete = hook;
        return this;
    }

    /// <summary>The fake bucket reports this soft delete retention (<see cref="BucketDescriptor.SoftDeleteRetentionDays"/>); 0 is off. Unset: unknown (null).</summary>
    public FakeGcp WithBucketSoftDeleteDays(int days)
    {
        _bucketSoftDeleteDays = days;
        return this;
    }

    /// <summary>
    /// The next prefix delete removes <paramref name="objects"/> objects, then fails once with <paramref name="kind"/> (403 for Permission): a sweep that dies partway.
    /// A retry after it runs clean.
    /// </summary>
    public FakeGcp WithPrefixDeleteFailingAfter(int objects, CloudErrorKind kind)
    {
        _prefixDeleteFailAfter = objects;
        _prefixDeleteFailKind = kind;
        return this;
    }

    /// <summary>Listing under this bucket fails with a 404, as a bucket that does not exist (a wrong project, a deleted bucket) does.</summary>
    public FakeGcp WithMissingBucket(string bucket)
    {
        _missingBuckets.Add(bucket);
        return this;
    }

    private void ThrowIfDeleteScripted(string name)
    {
        if (_deleteFailures.TryGetValue(name, out var kind))
        {
            throw Build(kind, "SCRIPTED_DELETE_FAILURE", kind == CloudErrorKind.Permission ? 403 : null, $"The delete of '{name}' was scripted to fail ({kind}).");
        }
    }

    private void DetachDisksOf(string vmName)
    {
        foreach (var (key, disk) in _disks)
        {
            if (disk.Users.Contains(vmName))
            {
                _disks[key] = disk with { AttachedTo = disk.Users.Where(u => u != vmName).ToList() };
            }
        }
    }

    // ---------------------------------------------------------------- IComputeGateway

    public Task StartVmAsync(string vmName, string zone, CancellationToken cancellationToken)
    {
        ThrowIfScriptedTransient();
        ExpireOverdueVms();
        var key = (vmName, zone);
        if (!_vms.TryGetValue(key, out var vm))
        {
            throw Build(CloudErrorKind.Other, "VM_NOT_FOUND", 404, $"The VM '{vmName}' is gone.");
        }

        if (_startFailure is { } kind)
        {
            throw kind switch
            {
                CloudErrorKind.Stockout => Build(kind, "ZONE_RESOURCE_POOL_EXHAUSTED", null, $"The zone '{zone}' does not have enough resources available to fulfill the request."),
                CloudErrorKind.Billing => Build(kind, "BILLING_DISABLED", 403, "This API method requires billing to be enabled."),
                CloudErrorKind.Permission => Build(kind, "PERMISSION_DENIED", 403, "Required 'compute.instances.start' permission."),
                _ => Build(kind, "START_FAILED", null, "The start failed."),
            };
        }

        if (vm.Status is "STOPPED" or "TERMINATED")
        {
            _vms[key] = vm with { Status = "RUNNING", StatusReason = null, StartedAt = _timeProvider.GetUtcNow(), StoppedAt = null };
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<DiskDescriptor>> ListDisksByInstallationAsync(string installationId, CancellationToken cancellationToken)
    {
        if (ConsumeHang())
        {
            return HangAsync<IReadOnlyList<DiskDescriptor>>(cancellationToken);
        }

        ThrowIfScriptedTransient();
        if (_notConnected)
        {
            throw Build(CloudErrorKind.Other, NotConnectedErrorCode, null, "No Google Cloud connection is built into this version.");
        }

        IReadOnlyList<DiskDescriptor> matches = _disks.Values
            .Where(d => d.BelongsTo(installationId))
            .OrderBy(d => d.Name, StringComparer.Ordinal)
            .ToList();
        return Task.FromResult(matches);
    }

    public Task DeleteDiskAsync(string diskName, string zone, CancellationToken cancellationToken)
    {
        ThrowIfScriptedTransient();
        ThrowIfDeleteScripted(diskName);
        if (_silentNoOpDeletes)
        {
            return Task.CompletedTask;
        }

        if (_disks.TryGetValue((diskName, zone), out var disk) && disk.Users.Count > 0)
        {
            throw Build(CloudErrorKind.Other, "RESOURCE_IN_USE_BY_ANOTHER_RESOURCE", 400, $"The disk '{diskName}' is already being used by '{disk.Users[0]}'.");
        }

        _disks.TryRemove((diskName, zone), out _);
        return Task.CompletedTask;
    }

    // ---------------------------------------------------------------- IStorageGateway

    public Task<PrefixUsage> GetPrefixUsageAsync(string bucket, string prefix, CancellationToken cancellationToken)
    {
        if (ConsumeHang())
        {
            return HangAsync<PrefixUsage>(cancellationToken);
        }

        ThrowIfScriptedTransient();
        if (_missingBuckets.Contains(bucket))
        {
            throw Build(CloudErrorKind.Other, "NOT_FOUND", 404, $"The bucket '{bucket}' does not exist.");
        }

        var under = _objects.Where(o => o.Key.Bucket == bucket && o.Key.Key.StartsWith(prefix, StringComparison.Ordinal)).ToList();
        return Task.FromResult(new PrefixUsage(under.Count, under.Sum(o => (long)o.Value.Length)));
    }

    public Task<IReadOnlyList<string>> ListChildPrefixesAsync(string bucket, string prefix, CancellationToken cancellationToken)
    {
        if (ConsumeHang())
        {
            return HangAsync<IReadOnlyList<string>>(cancellationToken);
        }

        ThrowIfScriptedTransient();
        IReadOnlyList<string> children = _objects.Keys
            .Where(k => k.Bucket == bucket && k.Key.StartsWith(prefix, StringComparison.Ordinal))
            .Select(k => k.Key[prefix.Length..])
            .Where(rest => rest.Contains('/'))
            .Select(rest => prefix + rest[..(rest.IndexOf('/') + 1)])
            .Distinct(StringComparer.Ordinal)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();
        return Task.FromResult(children);
    }

    public Task<int> DeletePrefixAsync(string bucket, string prefix, CancellationToken cancellationToken)
    {
        StoragePrefixes.RequireDeletable(prefix);
        ThrowIfScriptedTransient();
        ThrowIfDeleteScripted(prefix);
        // Like Cloud Storage: lexicographic key order, except that the folder's manifest.json (the proof of ownership) goes last.
        var manifestKey = prefix + ResultsBucket.ManifestObject;
        var keys = _objects.Keys.Where(k => k.Bucket == bucket && k.Key.StartsWith(prefix, StringComparison.Ordinal))
            .OrderBy(k => k.Key == manifestKey ? 1 : 0)
            .ThenBy(k => k.Key, StringComparer.Ordinal)
            .ToList();
        if (_silentNoOpDeletes)
        {
            return Task.FromResult(keys.Count);
        }

        var deleted = 0;
        foreach (var key in keys)
        {
            if (_prefixDeleteFailAfter is { } allowed && deleted >= allowed)
            {
                // Issue #262: a sweep that dies partway (a 403 or a dropped connection on object N), leaving the rest in place.
                _prefixDeleteFailAfter = null;
                throw Build(_prefixDeleteFailKind, "SCRIPTED_DELETE_FAILURE", _prefixDeleteFailKind == CloudErrorKind.Permission ? 403 : null, $"The delete under '{prefix}' was scripted to stop after {allowed} objects ({_prefixDeleteFailKind}).");
            }

            if (_objects.TryRemove(key, out _))
            {
                deleted++;
            }
        }

        _afterPrefixDelete?.Invoke(prefix);
        return Task.FromResult(deleted);
    }
}
