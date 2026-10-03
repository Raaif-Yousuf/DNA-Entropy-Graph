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
}
