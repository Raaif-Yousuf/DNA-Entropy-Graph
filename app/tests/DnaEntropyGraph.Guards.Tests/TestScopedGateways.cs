// Issue #830: test-only default scope for call sites that do not care which scope they act in (an instance method takes a CloudScope first, so these bind only when none is given).
using DnaEntropyGraph.Core.Cloud;

namespace DnaEntropyGraph.Guards.Tests;

internal static class TestScopedGateways
{
    internal static readonly CloudScope Default = new(DnaEntropyGraph.Cloud.FakeGcp.AnyProject);

    internal static Task<VmDescriptor> CreateVmAsync(this IComputeGateway gateway, VmSpec spec, string zone, CancellationToken cancellationToken) => gateway.CreateVmAsync(Default, spec, zone, cancellationToken);

    internal static Task<VmDescriptor> ReuseVmAsync(this IComputeGateway gateway, VmSpec spec, string existingVmName, string zone, CancellationToken cancellationToken) => gateway.ReuseVmAsync(Default, spec, existingVmName, zone, cancellationToken);

    internal static Task<IReadOnlyList<string>> ListAcceleratorZonesAsync(this IComputeGateway gateway, string acceleratorType, CancellationToken cancellationToken) => gateway.ListAcceleratorZonesAsync(Default, acceleratorType, cancellationToken);

    internal static Task StartVmAsync(this IComputeGateway gateway, string vmName, string zone, CancellationToken cancellationToken) => gateway.StartVmAsync(Default, vmName, zone, cancellationToken);

    internal static Task<IReadOnlyList<DiskDescriptor>> ListDisksByInstallationAsync(this IComputeGateway gateway, string installationId, CancellationToken cancellationToken) => gateway.ListDisksByInstallationAsync(Default, installationId, cancellationToken);

    internal static Task DeleteDiskAsync(this IComputeGateway gateway, string diskName, string zone, CancellationToken cancellationToken) => gateway.DeleteDiskAsync(Default, diskName, zone, cancellationToken);

    internal static Task<VmDescriptor?> GetVmAsync(this IComputeGateway gateway, string vmName, string zone, CancellationToken cancellationToken) => gateway.GetVmAsync(Default, vmName, zone, cancellationToken);

    internal static Task<bool> WasPreemptedAsync(this IComputeGateway gateway, string vmName, string zone, VmIncarnation incarnation, CancellationToken cancellationToken) => gateway.WasPreemptedAsync(Default, vmName, zone, incarnation, cancellationToken);

    internal static Task StopVmAsync(this IComputeGateway gateway, string vmName, string zone, CancellationToken cancellationToken) => gateway.StopVmAsync(Default, vmName, zone, cancellationToken);

    internal static Task DeleteVmAsync(this IComputeGateway gateway, string vmName, string zone, CancellationToken cancellationToken) => gateway.DeleteVmAsync(Default, vmName, zone, cancellationToken);

    internal static Task EndVmAsync(this IComputeGateway gateway, string vmName, string zone, bool delete, Action? onAccepted, CancellationToken cancellationToken) => gateway.EndVmAsync(Default, vmName, zone, delete, onAccepted, cancellationToken);

    internal static Task<IReadOnlyList<VmDescriptor>> FindByJobIdAsync(this IComputeGateway gateway, string jobId, CancellationToken cancellationToken) => gateway.FindByJobIdAsync(Default, jobId, cancellationToken);

    internal static Task<IReadOnlyList<VmDescriptor>> ListByInstallationAsync(this IComputeGateway gateway, string installationId, CancellationToken cancellationToken) => gateway.ListByInstallationAsync(Default, installationId, cancellationToken);

    internal static Task<IReadOnlyList<VmDescriptor>> ListAppVmsAsync(this IComputeGateway gateway, CancellationToken cancellationToken) => gateway.ListAppVmsAsync(Default, cancellationToken);

    internal static Task<string> EnsureBucketAsync(this IStorageGateway gateway, string projectId, CancellationToken cancellationToken) => gateway.EnsureBucketAsync(new CloudScope(projectId), cancellationToken);

    internal static Task UploadAsync(this IStorageGateway gateway, string bucket, string objectKey, Stream content, CancellationToken cancellationToken) => gateway.UploadAsync(Default, bucket, objectKey, content, cancellationToken);

    internal static Task<Stream> DownloadAsync(this IStorageGateway gateway, string bucket, string objectKey, CancellationToken cancellationToken) => gateway.DownloadAsync(Default, bucket, objectKey, cancellationToken);

    internal static Task<Stream?> TryDownloadAsync(this IStorageGateway gateway, string bucket, string objectKey, CancellationToken cancellationToken) => gateway.TryDownloadAsync(Default, bucket, objectKey, cancellationToken);

    internal static Task<bool> TryDownloadToFileAsync(this IStorageGateway gateway, string bucket, string objectKey, string destinationPath, CancellationToken cancellationToken) => gateway.TryDownloadToFileAsync(Default, bucket, objectKey, destinationPath, cancellationToken);

    internal static Task<IReadOnlyList<BucketDescriptor>> ListBucketsAsync(this IStorageGateway gateway, string projectId, CancellationToken cancellationToken) => gateway.ListBucketsAsync(new CloudScope(projectId), cancellationToken);

    internal static Task<PrefixUsage> GetPrefixUsageAsync(this IStorageGateway gateway, string bucket, string prefix, CancellationToken cancellationToken) => gateway.GetPrefixUsageAsync(Default, bucket, prefix, cancellationToken);

    internal static Task<IReadOnlyList<string>> ListChildPrefixesAsync(this IStorageGateway gateway, string bucket, string prefix, CancellationToken cancellationToken) => gateway.ListChildPrefixesAsync(Default, bucket, prefix, cancellationToken);

    internal static Task<int> DeletePrefixAsync(this IStorageGateway gateway, string bucket, string prefix, CancellationToken cancellationToken) => gateway.DeletePrefixAsync(Default, bucket, prefix, cancellationToken);

    internal static Task DeleteObjectAsync(this IStorageGateway gateway, string bucket, string objectKey, CancellationToken cancellationToken) => gateway.DeleteObjectAsync(Default, bucket, objectKey, cancellationToken);

    internal static Task<ProjectLifecycleState> GetProjectStateAsync(this IProjectSetupGateway gateway, string projectId, CancellationToken cancellationToken) => gateway.GetProjectStateAsync(new CloudScope(projectId), cancellationToken);

    internal static Task<bool> IsBillingEnabledAsync(this IProjectSetupGateway gateway, string projectId, CancellationToken cancellationToken) => gateway.IsBillingEnabledAsync(new CloudScope(projectId), cancellationToken);

    internal static Task<bool> IsComputeApiEnabledAsync(this IProjectSetupGateway gateway, string projectId, CancellationToken cancellationToken) => gateway.IsComputeApiEnabledAsync(new CloudScope(projectId), cancellationToken);

    internal static Task EnableComputeApiAsync(this IProjectSetupGateway gateway, string projectId, CancellationToken cancellationToken) => gateway.EnableComputeApiAsync(new CloudScope(projectId), cancellationToken);

    internal static Task<int> GetGpuQuotaAsync(this IQuotaGateway gateway, string projectId, string region, string acceleratorType, CancellationToken cancellationToken) => gateway.GetGpuQuotaAsync(new CloudScope(projectId), region, acceleratorType, cancellationToken);

    internal static Task<QuotaReadResult> ReadQuotaAsync(this IQuotaGateway gateway, string projectId, CancellationToken cancellationToken) => gateway.ReadQuotaAsync(new CloudScope(projectId), cancellationToken);

    internal static Task<QuotaEligibility> GetQuotaIncreaseEligibilityAsync(this IQuotaGateway gateway, string projectId, string metric, CancellationToken cancellationToken) => gateway.GetQuotaIncreaseEligibilityAsync(new CloudScope(projectId), metric, cancellationToken);

    internal static Task<QuotaIncreaseResult> RequestQuotaIncreaseAsync(this IQuotaGateway gateway, string projectId, QuotaIncreaseRequest request, CancellationToken cancellationToken) => gateway.RequestQuotaIncreaseAsync(new CloudScope(projectId), request, cancellationToken);

    internal static Task<WorkerIdentity> EnsureWorkerIdentityAsync(this IWorkerIdentityGateway gateway, string projectId, string bucket, CancellationToken cancellationToken) => gateway.EnsureWorkerIdentityAsync(new CloudScope(projectId), bucket, cancellationToken);

}
