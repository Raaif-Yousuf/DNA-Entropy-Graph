using DnaEntropyGraph.Core.Cloud;

namespace DnaEntropyGraph.Cloud;

// Issue #258: one decorator per gateway interface, each method routed through
// the single CloudCallPipeline. The real Google-backed gateways go inside
// these (Hard Rule 7: only this project references Google.*), so retry,
// token refresh and the offline breaker are written once, not per gateway.
// ResilientGatewayTests.Every_method_of_every_gateway_goes_through_the_pipeline
// fails if a method is added to an interface and not routed here. Stop, delete
// and find-by-job bypass the breaker: they are the cleanup that stops the meter.

public sealed class ResilientComputeGateway : IComputeGateway
{
    private readonly IComputeGateway _inner;
    private readonly CloudCallPipeline _pipeline;

    public ResilientComputeGateway(IComputeGateway inner, CloudCallPipeline pipeline)
    {
        _inner = inner;
        _pipeline = pipeline;
    }

    public Task<VmDescriptor> CreateVmAsync(VmSpec spec, string zone, CancellationToken cancellationToken)
        => _pipeline.ExecuteAsync("Compute.CreateVm", token => _inner.CreateVmAsync(spec, zone, token), cancellationToken);

    public Task<VmDescriptor?> GetVmAsync(string vmName, string zone, CancellationToken cancellationToken)
        => _pipeline.ExecuteAsync("Compute.GetVm", token => _inner.GetVmAsync(vmName, zone, token), cancellationToken);

    public Task StopVmAsync(string vmName, string zone, CancellationToken cancellationToken)
        => _pipeline.ExecuteAsync("Compute.StopVm", token => _inner.StopVmAsync(vmName, zone, token), cancellationToken, bypassBreaker: true);

    public Task DeleteVmAsync(string vmName, string zone, CancellationToken cancellationToken)
        => _pipeline.ExecuteAsync("Compute.DeleteVm", token => _inner.DeleteVmAsync(vmName, zone, token), cancellationToken, bypassBreaker: true);

    public Task<IReadOnlyList<VmDescriptor>> FindByJobIdAsync(string jobId, CancellationToken cancellationToken)
        => _pipeline.ExecuteAsync("Compute.FindByJobId", token => _inner.FindByJobIdAsync(jobId, token), cancellationToken, bypassBreaker: true);
}

public sealed class ResilientStorageGateway : IStorageGateway
{
    private readonly IStorageGateway _inner;
    private readonly CloudCallPipeline _pipeline;

    public ResilientStorageGateway(IStorageGateway inner, CloudCallPipeline pipeline)
    {
        _inner = inner;
        _pipeline = pipeline;
    }

    public Task<string> EnsureBucketAsync(string projectId, CancellationToken cancellationToken)
        => _pipeline.ExecuteAsync("Storage.EnsureBucket", token => _inner.EnsureBucketAsync(projectId, token), cancellationToken);

    public Task UploadAsync(string bucket, string objectKey, Stream content, CancellationToken cancellationToken)
    {
        // A retry re-reads the stream, so rewind it first; a stream that
        // cannot seek cannot be replayed and gets a single attempt.
        var start = content.CanSeek ? content.Position : 0;
        return _pipeline.ExecuteAsync(
            "Storage.Upload",
            token =>
            {
                if (content.CanSeek)
                {
                    content.Position = start;
                }

                return _inner.UploadAsync(bucket, objectKey, content, token);
            },
            cancellationToken,
            replayable: content.CanSeek);
    }

    public Task<Stream> DownloadAsync(string bucket, string objectKey, CancellationToken cancellationToken)
        => _pipeline.ExecuteAsync("Storage.Download", token => _inner.DownloadAsync(bucket, objectKey, token), cancellationToken);

    public Task<Stream?> TryDownloadAsync(string bucket, string objectKey, CancellationToken cancellationToken)
        => _pipeline.ExecuteAsync("Storage.TryDownload", token => _inner.TryDownloadAsync(bucket, objectKey, token), cancellationToken);
}

public sealed class ResilientProjectSetupGateway : IProjectSetupGateway
{
    private readonly IProjectSetupGateway _inner;
    private readonly CloudCallPipeline _pipeline;

    public ResilientProjectSetupGateway(IProjectSetupGateway inner, CloudCallPipeline pipeline)
    {
        _inner = inner;
        _pipeline = pipeline;
    }

    public Task<ProjectLifecycleState> GetProjectStateAsync(string projectId, CancellationToken cancellationToken)
        => _pipeline.ExecuteAsync("ProjectSetup.GetProjectState", token => _inner.GetProjectStateAsync(projectId, token), cancellationToken);

    public Task<bool> IsBillingEnabledAsync(string projectId, CancellationToken cancellationToken)
        => _pipeline.ExecuteAsync("ProjectSetup.IsBillingEnabled", token => _inner.IsBillingEnabledAsync(projectId, token), cancellationToken);

    public Task<bool> IsComputeApiEnabledAsync(string projectId, CancellationToken cancellationToken)
        => _pipeline.ExecuteAsync("ProjectSetup.IsComputeApiEnabled", token => _inner.IsComputeApiEnabledAsync(projectId, token), cancellationToken);

    public Task EnableComputeApiAsync(string projectId, CancellationToken cancellationToken)
        => _pipeline.ExecuteAsync("ProjectSetup.EnableComputeApi", token => _inner.EnableComputeApiAsync(projectId, token), cancellationToken);
}

public sealed class ResilientQuotaGateway : IQuotaGateway
{
    private readonly IQuotaGateway _inner;
    private readonly CloudCallPipeline _pipeline;

    public ResilientQuotaGateway(IQuotaGateway inner, CloudCallPipeline pipeline)
    {
        _inner = inner;
        _pipeline = pipeline;
    }

    public Task<int> GetGpuQuotaAsync(string projectId, string region, string acceleratorType, CancellationToken cancellationToken)
        => _pipeline.ExecuteAsync("Quota.GetGpuQuota", token => _inner.GetGpuQuotaAsync(projectId, region, acceleratorType, token), cancellationToken);
}
