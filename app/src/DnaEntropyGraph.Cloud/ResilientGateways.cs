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

    public Task<IReadOnlyList<string>> ListAcceleratorZonesAsync(string acceleratorType, CancellationToken cancellationToken)
        => _pipeline.ExecuteAsync("Compute.ListAcceleratorZones", token => _inner.ListAcceleratorZonesAsync(acceleratorType, token), cancellationToken);

    public Task<VmDescriptor> ReuseVmAsync(VmSpec spec, string existingVmName, string zone, CancellationToken cancellationToken)
        // A composite (claim, scheduling, metadata, start): replaying it after a transient failure of its last step meets a VM that already
        // started. replayable: false gives it the breaker but neither retries nor a 401 replay; the provisioner looks by label on a Network failure.
        => _pipeline.ExecuteAsync("Compute.ReuseVm", token => _inner.ReuseVmAsync(spec, existingVmName, zone, token), cancellationToken, replayable: false);

    // Issue #105. A start is idempotent (starting a running VM is accepted) and is new work, so it keeps the breaker; listing and deleting our
    // disks is cleanup that stops the meter, so it bypasses it like stop and delete of a VM.
    public Task StartVmAsync(string vmName, string zone, CancellationToken cancellationToken)
        => _pipeline.ExecuteAsync("Compute.StartVm", token => _inner.StartVmAsync(vmName, zone, token), cancellationToken);

    public Task<IReadOnlyList<DiskDescriptor>> ListDisksByInstallationAsync(string installationId, CancellationToken cancellationToken)
        => _pipeline.ExecuteAsync("Compute.ListDisksByInstallation", token => _inner.ListDisksByInstallationAsync(installationId, token), cancellationToken, bypassBreaker: true);

    public Task DeleteDiskAsync(string diskName, string zone, CancellationToken cancellationToken)
        => _pipeline.ExecuteAsync("Compute.DeleteDisk", token => _inner.DeleteDiskAsync(diskName, zone, token), cancellationToken, bypassBreaker: true);

    public Task<VmDescriptor?> GetVmAsync(string vmName, string zone, CancellationToken cancellationToken)
        => _pipeline.ExecuteAsync("Compute.GetVm", token => _inner.GetVmAsync(vmName, zone, token), cancellationToken);

    public Task StopVmAsync(string vmName, string zone, CancellationToken cancellationToken)
        => _pipeline.ExecuteAsync("Compute.StopVm", token => _inner.StopVmAsync(vmName, zone, token), cancellationToken, bypassBreaker: true);

    public Task DeleteVmAsync(string vmName, string zone, CancellationToken cancellationToken)
        => _pipeline.ExecuteAsync("Compute.DeleteVm", token => _inner.DeleteVmAsync(vmName, zone, token), cancellationToken, bypassBreaker: true);

    public Task<IReadOnlyList<VmDescriptor>> FindByJobIdAsync(string jobId, CancellationToken cancellationToken)
        => _pipeline.ExecuteAsync("Compute.FindByJobId", token => _inner.FindByJobIdAsync(jobId, token), cancellationToken, bypassBreaker: true);

    public Task<IReadOnlyList<VmDescriptor>> ListByInstallationAsync(string installationId, CancellationToken cancellationToken)
        => _pipeline.ExecuteAsync("Compute.ListByInstallation", token => _inner.ListByInstallationAsync(installationId, token), cancellationToken, bypassBreaker: true);

    public Task<IReadOnlyList<VmDescriptor>> ListAppVmsAsync(CancellationToken cancellationToken)
        => _pipeline.ExecuteAsync("Compute.ListAppVms", token => _inner.ListAppVmsAsync(token), cancellationToken, bypassBreaker: true);
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

    public Task<IReadOnlyList<BucketDescriptor>> ListBucketsAsync(string projectId, CancellationToken cancellationToken)
        => _pipeline.ExecuteAsync("Storage.ListBuckets", token => _inner.ListBucketsAsync(projectId, token), cancellationToken);

    // Issue #105: the Cloud page's reads and the cleanup. All are idempotent (a replayed prefix delete finds fewer objects), and the cleanup bypasses the breaker.
    public Task<PrefixUsage> GetPrefixUsageAsync(string bucket, string prefix, CancellationToken cancellationToken)
        => _pipeline.ExecuteAsync("Storage.GetPrefixUsage", token => _inner.GetPrefixUsageAsync(bucket, prefix, token), cancellationToken, bypassBreaker: true);

    public Task<IReadOnlyList<string>> ListChildPrefixesAsync(string bucket, string prefix, CancellationToken cancellationToken)
        => _pipeline.ExecuteAsync("Storage.ListChildPrefixes", token => _inner.ListChildPrefixesAsync(bucket, prefix, token), cancellationToken, bypassBreaker: true);

    public Task<int> DeletePrefixAsync(string bucket, string prefix, CancellationToken cancellationToken)
    {
        StoragePrefixes.RequireDeletable(prefix);
        // A sweep: the real gateway lists and deletes object by object, each one a pipeline call of its own (retries, the 401 replay and the
        // per-attempt deadline). Wrapping the sweep in one more retrying call would multiply those retries and replay a whole sweep for one
        // blip, so this layer gives it the single attempt (replayable: false) and leaves the retrying to the per-request calls inside.
        return _pipeline.ExecuteAsync("Storage.DeletePrefix", token => _inner.DeletePrefixAsync(bucket, prefix, token), cancellationToken, replayable: false, bypassBreaker: true);
    }
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

public sealed class ResilientBillingGateway : IBillingGateway
{
    private readonly IBillingGateway _inner;
    private readonly CloudCallPipeline _pipeline;

    public ResilientBillingGateway(IBillingGateway inner, CloudCallPipeline pipeline)
    {
        _inner = inner;
        _pipeline = pipeline;
    }

    public Task<BillingStatus> GetBillingStatusAsync(string projectId, CancellationToken cancellationToken)
        => _pipeline.ExecuteAsync("Billing.GetBillingStatus", token => _inner.GetBillingStatusAsync(projectId, token), cancellationToken);

    public Task<IReadOnlyList<BillingAccountSummary>> ListOpenBillingAccountsAsync(CancellationToken cancellationToken)
        => _pipeline.ExecuteAsync("Billing.ListOpenBillingAccounts", token => _inner.ListOpenBillingAccountsAsync(token), cancellationToken);

    public Task LinkProjectAsync(string projectId, string billingAccountId, CancellationToken cancellationToken)
        => _pipeline.ExecuteAsync("Billing.LinkProject", token => _inner.LinkProjectAsync(projectId, billingAccountId, token), cancellationToken);
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

    public Task<QuotaReadResult> ReadQuotaAsync(string projectId, CancellationToken cancellationToken)
        => _pipeline.ExecuteAsync("Quota.ReadQuota", token => _inner.ReadQuotaAsync(projectId, token), cancellationToken);

    // Issue #99. The create carries a caller-chosen preference id, so a replay after a lost answer is an already-exists answer, never a second request.
    public Task<QuotaEligibility> GetQuotaIncreaseEligibilityAsync(string projectId, string metric, CancellationToken cancellationToken)
        => _pipeline.ExecuteAsync("Quota.GetQuotaIncreaseEligibility", token => _inner.GetQuotaIncreaseEligibilityAsync(projectId, metric, token), cancellationToken);

    public Task<QuotaIncreaseResult> RequestQuotaIncreaseAsync(string projectId, QuotaIncreaseRequest request, CancellationToken cancellationToken)
        => _pipeline.ExecuteAsync("Quota.RequestQuotaIncrease", token => _inner.RequestQuotaIncreaseAsync(projectId, request, token), cancellationToken);
}
