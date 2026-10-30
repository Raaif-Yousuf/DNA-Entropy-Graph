using DnaEntropyGraph.Core.Cloud;
using DnaEntropyGraph.Core.Cloud.Audit;

namespace DnaEntropyGraph.Cloud;

// Issue #245: one decorator per gateway interface that has a mutating call. Each mutating method goes through CloudAuditor (one entry per
// logical call, however many retries the pipeline made inside it); every read passes straight through and is never logged. They wrap the
// gateway the container hands out (the Resilient* wrapper over FakeGcp, or the real Google gateway that runs the pipeline itself), so the
// audit sits at the one seam every caller already uses and is the same in the fake and the real build. The pipeline stays untouched.
// CloudAuditTests.Every_classified_mutation_has_an_audited_decorator_that_records_it (driven from CloudAuditTests.Classified, which a
// reflection scan of every I*Gateway in Core keeps complete) fails if a method is added to an interface and not routed here.
// A started line (written before the call, same correlation id as the finished line) is kept for the calls that span several API calls
// and cannot be replayed: Compute create/reuse/start/delete, Storage.EnsureBucket, Storage.DeletePrefix and Iam.EnsureWorkerIdentity.
// Short single-request writes (stop VM, delete disk, upload, enable API/services, link billing, create project, quota request) write only
// the finished line.

/// <summary>Implemented by every audit decorator, so a guard can see through it to the gateway it wraps.</summary>
public interface IAuditedGateway
{
    object Inner { get; }
}

public sealed class AuditedComputeGateway(IComputeGateway inner, CloudAuditor auditor) : IComputeGateway, IAuditedGateway
{
    public object Inner => inner;

    private static string Vm(string zone, string name) => $"zones/{zone}/instances/{name}";

    public Task<VmDescriptor> CreateVmAsync(CloudScope scope, VmSpec spec, string zone, CancellationToken cancellationToken)
        => auditor.RunAsync("Compute.CreateVm", () => (Vm(zone, spec.VmName), spec.JobId), () => inner.CreateVmAsync(scope, spec, zone, cancellationToken), started: true, mintRequestId: true, cancellationToken: cancellationToken);

    public Task<VmDescriptor> ReuseVmAsync(CloudScope scope, VmSpec spec, string existingVmName, string zone, CancellationToken cancellationToken)
        => auditor.RunAsync("Compute.ReuseVm", () => (Vm(zone, existingVmName), spec.JobId), () => inner.ReuseVmAsync(scope, spec, existingVmName, zone, cancellationToken), started: true, cancellationToken: cancellationToken);

    public Task StopVmAsync(CloudScope scope, string vmName, string zone, CancellationToken cancellationToken)
        => auditor.RunAsync("Compute.StopVm", () => (Vm(zone, vmName), CloudAuditor.JobIdFromVmName(vmName)), () => inner.StopVmAsync(scope, vmName, zone, cancellationToken), cancellationToken: cancellationToken);

    public Task DeleteVmAsync(CloudScope scope, string vmName, string zone, CancellationToken cancellationToken)
        => auditor.RunAsync("Compute.DeleteVm", () => (Vm(zone, vmName), CloudAuditor.JobIdFromVmName(vmName)), () => inner.DeleteVmAsync(scope, vmName, zone, cancellationToken), started: true, cancellationToken: cancellationToken);

    // Issue #113 meets #245: IComputeGateway.EndVmAsync has a default body that only reports acceptance when the work is done, and says a gateway that can do better MUST override it.
    // Without this override the audit decorator would use that default and hide the real gateway's earlier acceptance report from the caller. It is audited under the same operation
    // names as the plain stop and delete, so the log reads the same whichever way the computer was ended.
    public Task EndVmAsync(CloudScope scope, string vmName, string zone, bool delete, Action? onAccepted, CancellationToken cancellationToken)
        => auditor.RunAsync(delete ? "Compute.DeleteVm" : "Compute.StopVm", () => (Vm(zone, vmName), CloudAuditor.JobIdFromVmName(vmName)), () => inner.EndVmAsync(scope, vmName, zone, delete, onAccepted, cancellationToken), started: delete, cancellationToken: cancellationToken);

    public Task StartVmAsync(CloudScope scope, string vmName, string zone, CancellationToken cancellationToken)
        => auditor.RunAsync("Compute.StartVm", () => (Vm(zone, vmName), CloudAuditor.JobIdFromVmName(vmName)), () => inner.StartVmAsync(scope, vmName, zone, cancellationToken), started: true, cancellationToken: cancellationToken);

    public Task DeleteDiskAsync(CloudScope scope, string diskName, string zone, CancellationToken cancellationToken)
        => auditor.RunAsync("Compute.DeleteDisk", () => ($"zones/{zone}/disks/{diskName}", null), () => inner.DeleteDiskAsync(scope, diskName, zone, cancellationToken), cancellationToken: cancellationToken);

    public Task<IReadOnlyList<DiskDescriptor>> ListDisksByInstallationAsync(CloudScope scope, string installationId, CancellationToken cancellationToken)
        => inner.ListDisksByInstallationAsync(scope, installationId, cancellationToken);

    public Task<IReadOnlyList<string>> ListAcceleratorZonesAsync(CloudScope scope, string acceleratorType, CancellationToken cancellationToken)
        => inner.ListAcceleratorZonesAsync(scope, acceleratorType, cancellationToken);

    public Task<VmDescriptor?> GetVmAsync(CloudScope scope, string vmName, string zone, CancellationToken cancellationToken)
        => inner.GetVmAsync(scope, vmName, zone, cancellationToken);

    public Task<IReadOnlyList<VmDescriptor>> FindByJobIdAsync(CloudScope scope, string jobId, CancellationToken cancellationToken)
        => inner.FindByJobIdAsync(scope, jobId, cancellationToken);

    public Task<IReadOnlyList<VmDescriptor>> ListByInstallationAsync(CloudScope scope, string installationId, CancellationToken cancellationToken)
        => inner.ListByInstallationAsync(scope, installationId, cancellationToken);

    public Task<IReadOnlyList<VmDescriptor>> ListAppVmsAsync(CloudScope scope, CancellationToken cancellationToken)
        => inner.ListAppVmsAsync(scope, cancellationToken);

    // Issue #91: a read, forwarded to the real gateway's own lookup (the interface default says "not preempted", which would silently disable the Spot retry).
    public Task<bool> WasPreemptedAsync(CloudScope scope, string vmName, string zone, VmIncarnation incarnation, CancellationToken cancellationToken)
        => inner.WasPreemptedAsync(scope, vmName, zone, incarnation, cancellationToken);
}

public sealed class AuditedStorageGateway(IStorageGateway inner, CloudAuditor auditor) : IStorageGateway, IAuditedGateway
{
    public object Inner => inner;

    public Task<string> EnsureBucketAsync(CloudScope scope, CancellationToken cancellationToken)
        => auditor.RunAsync("Storage.EnsureBucket", () => ($"projects/{scope.ProjectId}", null), () => inner.EnsureBucketAsync(scope, cancellationToken), bucket => $"buckets/{bucket}", started: true, cancellationToken: cancellationToken);

    public Task UploadAsync(CloudScope scope, string bucket, string objectKey, Stream content, CancellationToken cancellationToken)
        // Only the job prefix is named: the part of the key below it can carry the user's own file name.
        => auditor.RunAsync("Storage.Upload", () => PrefixOf(bucket, objectKey), () => inner.UploadAsync(scope, bucket, objectKey, content, cancellationToken), cancellationToken: cancellationToken);

    public Task<int> DeletePrefixAsync(CloudScope scope, string bucket, string prefix, CancellationToken cancellationToken)
        => auditor.RunAsync("Storage.DeletePrefix", () => PrefixOf(bucket, prefix.TrimEnd('/') + "/_"), () => inner.DeletePrefixAsync(scope, bucket, prefix, cancellationToken), started: true, cancellationToken: cancellationToken);

    // Issue #91: the Spot retry clears the preempted attempt's status and progress files. The interface default refuses, so the audit must forward to the real delete.
    public Task DeleteObjectAsync(CloudScope scope, string bucket, string objectKey, CancellationToken cancellationToken)
        => auditor.RunAsync("Storage.DeleteObject", () => PrefixOf(bucket, objectKey), () => inner.DeleteObjectAsync(scope, bucket, objectKey, cancellationToken), cancellationToken: cancellationToken);

    public Task<PrefixUsage> GetPrefixUsageAsync(CloudScope scope, string bucket, string prefix, CancellationToken cancellationToken)
        => inner.GetPrefixUsageAsync(scope, bucket, prefix, cancellationToken);

    public Task<IReadOnlyList<string>> ListChildPrefixesAsync(CloudScope scope, string bucket, string prefix, CancellationToken cancellationToken)
        => inner.ListChildPrefixesAsync(scope, bucket, prefix, cancellationToken);

    private static (string Resource, string? JobId) PrefixOf(string bucket, string objectKey)
    {
        var (prefix, jobId) = CloudAuditor.ObjectPrefix(objectKey);
        return ($"buckets/{bucket}/{prefix}", jobId);
    }

    public Task<Stream> DownloadAsync(CloudScope scope, string bucket, string objectKey, CancellationToken cancellationToken)
        => inner.DownloadAsync(scope, bucket, objectKey, cancellationToken);

    public Task<Stream?> TryDownloadAsync(CloudScope scope, string bucket, string objectKey, CancellationToken cancellationToken)
        => inner.TryDownloadAsync(scope, bucket, objectKey, cancellationToken);

    public Task<bool> TryDownloadToFileAsync(CloudScope scope, string bucket, string objectKey, string destinationPath, CancellationToken cancellationToken)
        => inner.TryDownloadToFileAsync(scope, bucket, objectKey, destinationPath, cancellationToken);

    public Task<IReadOnlyList<BucketDescriptor>> ListBucketsAsync(CloudScope scope, CancellationToken cancellationToken)
        => inner.ListBucketsAsync(scope, cancellationToken);
}

public sealed class AuditedProjectSetupGateway(IProjectSetupGateway inner, CloudAuditor auditor) : IProjectSetupGateway, IAuditedGateway
{
    public object Inner => inner;

    public Task EnableComputeApiAsync(CloudScope scope, CancellationToken cancellationToken)
        => auditor.RunAsync("ProjectSetup.EnableComputeApi", () => ($"projects/{scope.ProjectId}/services/compute.googleapis.com", null), () => inner.EnableComputeApiAsync(scope, cancellationToken), cancellationToken: cancellationToken);

    public Task<ProjectLifecycleState> GetProjectStateAsync(CloudScope scope, CancellationToken cancellationToken)
        => inner.GetProjectStateAsync(scope, cancellationToken);

    public Task<bool> IsBillingEnabledAsync(CloudScope scope, CancellationToken cancellationToken)
        => inner.IsBillingEnabledAsync(scope, cancellationToken);

    public Task<bool> IsComputeApiEnabledAsync(CloudScope scope, CancellationToken cancellationToken)
        => inner.IsComputeApiEnabledAsync(scope, cancellationToken);
}

public sealed class AuditedWorkerIdentityGateway(IWorkerIdentityGateway inner, CloudAuditor auditor) : IWorkerIdentityGateway, IAuditedGateway
{
    public object Inner => inner;

    // Creates the worker service account and its role, and writes the project and bucket bindings: the one IAM write seam.
    public Task<WorkerIdentity> EnsureWorkerIdentityAsync(CloudScope scope, string bucket, CancellationToken cancellationToken)
        => auditor.RunAsync("Iam.EnsureWorkerIdentity", () => ($"projects/{scope.ProjectId}/buckets/{bucket}", null), () => inner.EnsureWorkerIdentityAsync(scope, bucket, cancellationToken), started: true, cancellationToken: cancellationToken);
}

public sealed class AuditedServiceEnablementGateway(IServiceEnablementGateway inner, CloudAuditor auditor) : IServiceEnablementGateway, IAuditedGateway
{
    public object Inner => inner;

    public Task EnableServicesAsync(string projectId, IReadOnlyList<string> serviceIds, CancellationToken cancellationToken)
        => auditor.RunAsync("ServiceUsage.EnableServices", () => ($"projects/{projectId}/services/{string.Join(',', serviceIds)}", null), () => inner.EnableServicesAsync(projectId, serviceIds, cancellationToken), cancellationToken: cancellationToken);

    public Task<bool> IsServiceEnabledAsync(string projectId, string serviceId, CancellationToken cancellationToken)
        => inner.IsServiceEnabledAsync(projectId, serviceId, cancellationToken);
}

public sealed class AuditedBillingGateway(IBillingGateway inner, CloudAuditor auditor) : IBillingGateway, IAuditedGateway
{
    public object Inner => inner;

    // The billing account id is not logged: the resource is the project it was linked to.
    public Task LinkProjectAsync(string projectId, string billingAccountId, CancellationToken cancellationToken)
        => auditor.RunAsync("Billing.LinkProject", () => ($"projects/{projectId}", null), () => inner.LinkProjectAsync(projectId, billingAccountId, cancellationToken), cancellationToken: cancellationToken);

    public Task<BillingStatus> GetBillingStatusAsync(string projectId, CancellationToken cancellationToken)
        => inner.GetBillingStatusAsync(projectId, cancellationToken);

    public Task<IReadOnlyList<BillingAccountSummary>> ListOpenBillingAccountsAsync(CancellationToken cancellationToken)
        => inner.ListOpenBillingAccountsAsync(cancellationToken);
}

public sealed class AuditedProjectCatalogGateway(IProjectCatalogGateway inner, CloudAuditor auditor) : IProjectCatalogGateway, IAuditedGateway
{
    public object Inner => inner;

    public Task<ProjectSummary> CreateProjectAsync(string projectId, string displayName, string installationId, CancellationToken cancellationToken)
        => auditor.RunAsync("ProjectCatalog.CreateProject", () => ($"projects/{projectId}", null), () => inner.CreateProjectAsync(projectId, displayName, installationId, cancellationToken), cancellationToken: cancellationToken);

    public Task<IReadOnlyList<ProjectSummary>> ListActiveProjectsAsync(CancellationToken cancellationToken)
        => inner.ListActiveProjectsAsync(cancellationToken);

    public Task<ProjectSummary?> GetProjectAsync(string projectId, CancellationToken cancellationToken)
        => inner.GetProjectAsync(projectId, cancellationToken);
}

public sealed class AuditedQuotaGateway(IQuotaGateway inner, CloudAuditor auditor) : IQuotaGateway, IAuditedGateway
{
    public object Inner => inner;

    // The resource is the project and the quota metric (and region); the justification text and contact email in the request are never logged.
    public Task<QuotaIncreaseResult> RequestQuotaIncreaseAsync(CloudScope scope, QuotaIncreaseRequest request, CancellationToken cancellationToken)
        => auditor.RunAsync(
            "Quota.RequestQuotaIncrease",
            () => ($"projects/{scope.ProjectId}/quotas/{request.Metric}" + (request.Region is { Length: > 0 } region ? $"/regions/{region}" : string.Empty), null),
            () => inner.RequestQuotaIncreaseAsync(scope, request, cancellationToken), cancellationToken: cancellationToken);

    public Task<int> GetGpuQuotaAsync(CloudScope scope, string region, string acceleratorType, CancellationToken cancellationToken)
        => inner.GetGpuQuotaAsync(scope, region, acceleratorType, cancellationToken);

    public Task<QuotaReadResult> ReadQuotaAsync(CloudScope scope, CancellationToken cancellationToken)
        => inner.ReadQuotaAsync(scope, cancellationToken);

    public Task<QuotaEligibility> GetQuotaIncreaseEligibilityAsync(CloudScope scope, string metric, CancellationToken cancellationToken)
        => inner.GetQuotaIncreaseEligibilityAsync(scope, metric, cancellationToken);
}

public sealed class AuditedPricingCatalogGateway : IPricingCatalogGateway, IAuditedGateway
{
    private readonly IPricingCatalogGateway _inner;

    // The auditor is taken like every decorator's (the registration and the guard build all of them the same way) but not used: a pure read of the public price catalog (#214) changes nothing in the user's project, so nothing is logged.
    public AuditedPricingCatalogGateway(IPricingCatalogGateway inner, CloudAuditor auditor) => _inner = inner;

    public object Inner => _inner;

    public Task<IReadOnlyList<CatalogSku>> ListComputeSkusAsync(CancellationToken cancellationToken)
        => _inner.ListComputeSkusAsync(cancellationToken);
}

public sealed class AuditedBudgetGateway(IBudgetGateway inner, CloudAuditor auditor) : IBudgetGateway, IAuditedGateway
{
    public object Inner => inner;

    public Task<BudgetInfo?> FindAsync(string billingAccountId, string installationId, string projectId, CancellationToken cancellationToken)
        => inner.FindAsync(billingAccountId, installationId, projectId, cancellationToken);

    // The billing account id is not logged: an upsert names the project the budget watches, a delete just says "budget" (its name carries the account id).
    public Task<BudgetInfo> UpsertAsync(BudgetSpec spec, CancellationToken cancellationToken)
        => auditor.RunAsync("Billing.UpsertBudget", () => ($"projects/{spec.ProjectId}", null), () => inner.UpsertAsync(spec, cancellationToken), cancellationToken: cancellationToken);

    public Task DeleteAsync(string budgetName, CancellationToken cancellationToken)
        => auditor.RunAsync("Billing.DeleteBudget", () => ("budget", null), () => inner.DeleteAsync(budgetName, cancellationToken), cancellationToken: cancellationToken);
}
