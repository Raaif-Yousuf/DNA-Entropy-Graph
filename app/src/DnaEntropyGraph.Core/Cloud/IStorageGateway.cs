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
}

/// <summary>A bucket as a gateway reports it: its name, its location (the region group, such as US), its labels and when it was created (null when unknown).</summary>
public sealed record BucketDescriptor(string Name, string Location, IReadOnlyDictionary<string, string> Labels, DateTimeOffset? CreatedAt);
