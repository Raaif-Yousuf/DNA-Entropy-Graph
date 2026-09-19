namespace DnaEntropyGraph.Core.Cloud;

/// <summary>Bucket operations (Hard Rule 7 - see IComputeGateway for the boundary rule).</summary>
public interface IStorageGateway
{
    Task<string> EnsureBucketAsync(string projectId, CancellationToken cancellationToken);

    Task UploadAsync(string bucket, string objectKey, Stream content, CancellationToken cancellationToken);

    Task<Stream> DownloadAsync(string bucket, string objectKey, CancellationToken cancellationToken);
}
