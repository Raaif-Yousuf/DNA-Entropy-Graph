namespace DnaEntropyGraph.Core.Cloud;

/// <summary>Bucket operations (Hard Rule 7 - see IComputeGateway for the boundary rule).</summary>
public interface IStorageGateway
{
    Task<string> EnsureBucketAsync(string projectId, CancellationToken cancellationToken);

    Task UploadAsync(string bucket, string objectKey, Stream content, CancellationToken cancellationToken);

    /// <summary>Downloads an object that must exist; a missing one is a not-found <see cref="CloudOperationException"/>.</summary>
    Task<Stream> DownloadAsync(string bucket, string objectKey, CancellationToken cancellationToken);

    /// <summary>
    /// Downloads an object that may not exist yet: null means "not there" (a 404), every other
    /// failure still throws. The runner polls <c>result.json</c> with this, so a transport error
    /// must never read as "the worker has not finished".
    /// </summary>
    Task<Stream?> TryDownloadAsync(string bucket, string objectKey, CancellationToken cancellationToken);

    /// <summary>
    /// Every bucket of ours in the project: a <c>deg-</c> bucket that carries <c>app=dna-entropy-graph</c> (Hard Rule 9: by label, never
    /// by name alone). Read-only: unlike <see cref="EnsureBucketAsync"/> it never creates or repairs one. For the CloudCli (issue #60).
    /// </summary>
    Task<IReadOnlyList<BucketDescriptor>> ListBucketsAsync(string projectId, CancellationToken cancellationToken);

    /// <summary>
    /// Issue #105: how many objects and bytes sit under <paramref name="prefix"/> in <paramref name="bucket"/> (a full, paged listing: a repeated page token fails the
    /// read rather than return a short total). An empty prefix is zero, not an error.
    /// </summary>
    Task<PrefixUsage> GetPrefixUsageAsync(string bucket, string prefix, CancellationToken cancellationToken);

    /// <summary>
    /// Issue #105: the next-level "folders" under <paramref name="prefix"/> (each returned with its trailing slash, such as <c>jobs/&lt;id&gt;/</c>), by a
    /// slash-delimited listing, so a bucket with thousands of objects is not listed whole to find its jobs.
    /// </summary>
    Task<IReadOnlyList<string>> ListChildPrefixesAsync(string bucket, string prefix, CancellationToken cancellationToken);

    /// <summary>
    /// Issue #105: deletes every object under <paramref name="prefix"/> and returns how many it deleted. The prefix must pass
    /// <see cref="StoragePrefixes.RequireDeletable"/> (an <see cref="ArgumentException"/> otherwise, before any request). An object already gone counts as
    /// deleted; the caller reads back to verify (see <c>InstallationCleanup</c>).
    /// </summary>
    Task<int> DeletePrefixAsync(string bucket, string prefix, CancellationToken cancellationToken);

    /// <summary>
    /// Issue #91: deletes exactly one object (an object already gone is a success). The Spot retry uses it to clear the status and progress files the
    /// preempted attempt left, so the new attempt's watcher never reads them. The default refuses, so a gateway that cannot delete one object fails the
    /// retry loudly instead of letting stale files through.
    /// </summary>
    Task DeleteObjectAsync(string bucket, string objectKey, CancellationToken cancellationToken)
        => throw new NotSupportedException("This storage gateway cannot delete a single object.");
}

/// <summary>
/// A bucket as a gateway reports it: its name, its location (the region group, such as US), its labels and when it was created (null when unknown).
/// <paramref name="SoftDeleteRetentionDays"/> is the days Cloud Storage keeps a deleted object recoverable (and billed) in this bucket: 0 when soft delete
/// is off, null when the gateway does not know (issue #105: a delete is then reported without a promise either way).
/// </summary>
public sealed record BucketDescriptor(string Name, string Location, IReadOnlyDictionary<string, string> Labels, DateTimeOffset? CreatedAt, int? SoftDeleteRetentionDays = null);
