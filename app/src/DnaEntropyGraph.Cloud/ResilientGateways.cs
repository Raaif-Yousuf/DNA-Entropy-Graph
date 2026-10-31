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

    public Task<VmDescriptor> CreateVmAsync(CloudScope scope, VmSpec spec, string zone, CancellationToken cancellationToken)
        => _pipeline.ExecuteAsync("Compute.CreateVm", token => _inner.CreateVmAsync(scope, spec, zone, token), cancellationToken, accountSub: scope.AccountSub);

    public Task<IReadOnlyList<string>> ListAcceleratorZonesAsync(CloudScope scope, string acceleratorType, CancellationToken cancellationToken)
        => _pipeline.ExecuteAsync("Compute.ListAcceleratorZones", token => _inner.ListAcceleratorZonesAsync(scope, acceleratorType, token), cancellationToken, accountSub: scope.AccountSub);

    public Task<VmDescriptor> ReuseVmAsync(CloudScope scope, VmSpec spec, string existingVmName, string zone, CancellationToken cancellationToken)
        // A composite (claim, scheduling, metadata, start): replaying it after a transient failure of its last step meets a VM that already
        // started. replayable: false gives it the breaker but neither retries nor a 401 replay; the provisioner looks by label on a Network failure.
        => _pipeline.ExecuteAsync("Compute.ReuseVm", token => _inner.ReuseVmAsync(scope, spec, existingVmName, zone, token), cancellationToken, replayable: false, accountSub: scope.AccountSub);

    // Issue #970: undoing a reuse's claim is cleanup for a VM that is already stopped (it protects a keep-alive's expiry, never starts anything), so it bypasses the breaker. It is a read-then-write
    // with a fingerprint and treats "labels already equal the donor's" as done (on the first read and after a 412 on the instance or the disk), so a replay after a lost answer is safe.
    public Task<LabelRestoreState> RestoreVmLabelsAsync(CloudScope scope, string vmName, string zone, string fromJobId, IReadOnlyDictionary<string, string> donorLabels, CancellationToken cancellationToken)
        => _pipeline.ExecuteAsync("Compute.RestoreVmLabels", token => _inner.RestoreVmLabelsAsync(scope, vmName, zone, fromJobId, donorLabels, token), cancellationToken, bypassBreaker: true, accountSub: scope.AccountSub);

    // Issue #105. A start is idempotent (starting a running VM is accepted) and is new work, so it keeps the breaker; listing and deleting our
    // disks is cleanup that stops the meter, so it bypasses it like stop and delete of a VM.
    public Task StartVmAsync(CloudScope scope, string vmName, string zone, CancellationToken cancellationToken)
        => _pipeline.ExecuteAsync("Compute.StartVm", token => _inner.StartVmAsync(scope, vmName, zone, token), cancellationToken, accountSub: scope.AccountSub);

    public Task<IReadOnlyList<DiskDescriptor>> ListDisksByInstallationAsync(CloudScope scope, string installationId, CancellationToken cancellationToken)
        => _pipeline.ExecuteAsync("Compute.ListDisksByInstallation", token => _inner.ListDisksByInstallationAsync(scope, installationId, token), cancellationToken, bypassBreaker: true, accountSub: scope.AccountSub);

    public Task DeleteDiskAsync(CloudScope scope, string diskName, string zone, CancellationToken cancellationToken)
        => _pipeline.ExecuteAsync("Compute.DeleteDisk", token => _inner.DeleteDiskAsync(scope, diskName, zone, token), cancellationToken, bypassBreaker: true, accountSub: scope.AccountSub);

    public Task<VmDescriptor?> GetVmAsync(CloudScope scope, string vmName, string zone, CancellationToken cancellationToken)
        => _pipeline.ExecuteAsync("Compute.GetVm", token => _inner.GetVmAsync(scope, vmName, zone, token), cancellationToken, accountSub: scope.AccountSub);

    public Task<bool> WasPreemptedAsync(CloudScope scope, string vmName, string zone, VmIncarnation incarnation, CancellationToken cancellationToken)
        => _pipeline.ExecuteAsync("Compute.WasPreempted", token => _inner.WasPreemptedAsync(scope, vmName, zone, incarnation, token), cancellationToken, bypassBreaker: true, accountSub: scope.AccountSub);

    public Task StopVmAsync(CloudScope scope, string vmName, string zone, CancellationToken cancellationToken)
        => _pipeline.ExecuteAsync("Compute.StopVm", token => _inner.StopVmAsync(scope, vmName, zone, token), cancellationToken, bypassBreaker: true, accountSub: scope.AccountSub);

    public Task DeleteVmAsync(CloudScope scope, string vmName, string zone, CancellationToken cancellationToken)
        => _pipeline.ExecuteAsync("Compute.DeleteVm", token => _inner.DeleteVmAsync(scope, vmName, zone, token), cancellationToken, bypassBreaker: true, accountSub: scope.AccountSub);

    // Issue #113: stop/delete that says when the request was accepted. Like StopVm and DeleteVm it is cleanup that stops the meter, so it bypasses the breaker;
    // a replayed attempt re-sends an idempotent stop or delete and may call onAccepted again (it must be idempotent).
    public Task EndVmAsync(CloudScope scope, string vmName, string zone, bool delete, Action? onAccepted, CancellationToken cancellationToken)
        => _pipeline.ExecuteAsync("Compute.EndVm", token => _inner.EndVmAsync(scope, vmName, zone, delete, onAccepted, token), cancellationToken, bypassBreaker: true, accountSub: scope.AccountSub);

    public Task<IReadOnlyList<VmDescriptor>> FindByJobIdAsync(CloudScope scope, string jobId, CancellationToken cancellationToken)
        => _pipeline.ExecuteAsync("Compute.FindByJobId", token => _inner.FindByJobIdAsync(scope, jobId, token), cancellationToken, bypassBreaker: true, accountSub: scope.AccountSub);

    public Task<IReadOnlyList<VmDescriptor>> ListByInstallationAsync(CloudScope scope, string installationId, CancellationToken cancellationToken)
        => _pipeline.ExecuteAsync("Compute.ListByInstallation", token => _inner.ListByInstallationAsync(scope, installationId, token), cancellationToken, bypassBreaker: true, accountSub: scope.AccountSub);

    public Task<IReadOnlyList<VmDescriptor>> ListAppVmsAsync(CloudScope scope, CancellationToken cancellationToken)
        => _pipeline.ExecuteAsync("Compute.ListAppVms", token => _inner.ListAppVmsAsync(scope, token), cancellationToken, bypassBreaker: true, accountSub: scope.AccountSub);
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

    public Task<string> EnsureBucketAsync(CloudScope scope, CancellationToken cancellationToken)
        => _pipeline.ExecuteAsync("Storage.EnsureBucket", token => _inner.EnsureBucketAsync(scope, token), cancellationToken, accountSub: scope.AccountSub);

    public Task UploadAsync(CloudScope scope, string bucket, string objectKey, Stream content, CancellationToken cancellationToken)
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

                return _inner.UploadAsync(scope, bucket, objectKey, content, token);
            },
            cancellationToken,
            replayable: content.CanSeek,
            accountSub: scope.AccountSub);
    }

    public Task<Stream> DownloadAsync(CloudScope scope, string bucket, string objectKey, CancellationToken cancellationToken)
        => _pipeline.ExecuteAsync("Storage.Download", token => _inner.DownloadAsync(scope, bucket, objectKey, token), cancellationToken, accountSub: scope.AccountSub);

    public Task<Stream?> TryDownloadAsync(CloudScope scope, string bucket, string objectKey, CancellationToken cancellationToken)
        => _pipeline.ExecuteAsync("Storage.TryDownload", token => _inner.TryDownloadAsync(scope, bucket, objectKey, token), cancellationToken, accountSub: scope.AccountSub);

    public Task<bool> TryDownloadToFileAsync(CloudScope scope, string bucket, string objectKey, string destinationPath, CancellationToken cancellationToken)
        => _pipeline.ExecuteAsync("Storage.TryDownloadToFile", token => _inner.TryDownloadToFileAsync(scope, bucket, objectKey, destinationPath, token), cancellationToken, accountSub: scope.AccountSub);

    public Task<IReadOnlyList<BucketDescriptor>> ListBucketsAsync(CloudScope scope, CancellationToken cancellationToken)
        => _pipeline.ExecuteAsync("Storage.ListBuckets", token => _inner.ListBucketsAsync(scope, token), cancellationToken, accountSub: scope.AccountSub);

    // Issue #105: the Cloud page's reads and the cleanup. All are idempotent (a replayed prefix delete finds fewer objects), and the cleanup bypasses the breaker.
    public Task<PrefixUsage> GetPrefixUsageAsync(CloudScope scope, string bucket, string prefix, CancellationToken cancellationToken)
        => _pipeline.ExecuteAsync("Storage.GetPrefixUsage", token => _inner.GetPrefixUsageAsync(scope, bucket, prefix, token), cancellationToken, bypassBreaker: true, accountSub: scope.AccountSub);

    public Task<IReadOnlyList<string>> ListChildPrefixesAsync(CloudScope scope, string bucket, string prefix, CancellationToken cancellationToken)
        => _pipeline.ExecuteAsync("Storage.ListChildPrefixes", token => _inner.ListChildPrefixesAsync(scope, bucket, prefix, token), cancellationToken, bypassBreaker: true, accountSub: scope.AccountSub);

    public Task DeleteObjectAsync(CloudScope scope, string bucket, string objectKey, CancellationToken cancellationToken)
        => _pipeline.ExecuteAsync("Storage.DeleteObject", token => _inner.DeleteObjectAsync(scope, bucket, objectKey, token), cancellationToken, bypassBreaker: true, accountSub: scope.AccountSub);

    public Task<int> DeletePrefixAsync(CloudScope scope, string bucket, string prefix, CancellationToken cancellationToken)
    {
        StoragePrefixes.RequireDeletable(prefix);
        // A sweep: the real gateway lists and deletes object by object, each one a pipeline call of its own (retries, the 401 replay and the
        // per-attempt deadline). Wrapping the sweep in one more retrying call would multiply those retries and replay a whole sweep for one
        // blip, so this layer gives it the single attempt (replayable: false) and leaves the retrying to the per-request calls inside.
        return _pipeline.ExecuteAsync("Storage.DeletePrefix", token => _inner.DeletePrefixAsync(scope, bucket, prefix, token), cancellationToken, replayable: false, bypassBreaker: true, accountSub: scope.AccountSub);
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

    public Task<ProjectLifecycleState> GetProjectStateAsync(CloudScope scope, CancellationToken cancellationToken)
        => _pipeline.ExecuteAsync("ProjectSetup.GetProjectState", token => _inner.GetProjectStateAsync(scope, token), cancellationToken, accountSub: scope.AccountSub);

    public Task<bool> IsBillingEnabledAsync(CloudScope scope, CancellationToken cancellationToken)
        => _pipeline.ExecuteAsync("ProjectSetup.IsBillingEnabled", token => _inner.IsBillingEnabledAsync(scope, token), cancellationToken, accountSub: scope.AccountSub);

    public Task<bool> IsComputeApiEnabledAsync(CloudScope scope, CancellationToken cancellationToken)
        => _pipeline.ExecuteAsync("ProjectSetup.IsComputeApiEnabled", token => _inner.IsComputeApiEnabledAsync(scope, token), cancellationToken, accountSub: scope.AccountSub);

    public Task EnableComputeApiAsync(CloudScope scope, CancellationToken cancellationToken)
        => _pipeline.ExecuteAsync("ProjectSetup.EnableComputeApi", token => _inner.EnableComputeApiAsync(scope, token), cancellationToken, accountSub: scope.AccountSub);
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

    public Task<int> GetGpuQuotaAsync(CloudScope scope, string region, string acceleratorType, CancellationToken cancellationToken)
        => _pipeline.ExecuteAsync("Quota.GetGpuQuota", token => _inner.GetGpuQuotaAsync(scope, region, acceleratorType, token), cancellationToken, accountSub: scope.AccountSub);

    public Task<QuotaReadResult> ReadQuotaAsync(CloudScope scope, CancellationToken cancellationToken)
        => _pipeline.ExecuteAsync("Quota.ReadQuota", token => _inner.ReadQuotaAsync(scope, token), cancellationToken, accountSub: scope.AccountSub);

    // Issue #99. The create carries a caller-chosen preference id, so a replay after a lost answer is an already-exists answer, never a second request.
    public Task<QuotaEligibility> GetQuotaIncreaseEligibilityAsync(CloudScope scope, string metric, CancellationToken cancellationToken)
        => _pipeline.ExecuteAsync("Quota.GetQuotaIncreaseEligibility", token => _inner.GetQuotaIncreaseEligibilityAsync(scope, metric, token), cancellationToken, accountSub: scope.AccountSub);

    public Task<QuotaIncreaseResult> RequestQuotaIncreaseAsync(CloudScope scope, QuotaIncreaseRequest request, CancellationToken cancellationToken)
        => _pipeline.ExecuteAsync("Quota.RequestQuotaIncrease", token => _inner.RequestQuotaIncreaseAsync(scope, request, token), cancellationToken, accountSub: scope.AccountSub);
}
