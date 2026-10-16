using DnaEntropyGraph.Core.Cloud;
using DnaEntropyGraph.Core.Cloud.Audit;

namespace DnaEntropyGraph.Cloud;

// Issue #245: one decorator per gateway interface that has a mutating call. Each mutating method goes through CloudAuditor (one entry per
// logical call, however many retries the pipeline made inside it); every read passes straight through and is never logged. They wrap the
// gateway the container hands out (the Resilient* wrapper over FakeGcp, or the real Google gateway that runs the pipeline itself), so the
// audit sits at the one seam every caller already uses and is the same in the fake and the real build. The pipeline stays untouched.
// AuditedGatewayTests.Every_mutating_method_of_every_gateway_is_audited fails if a method is added to an interface and not routed here.

/// <summary>Implemented by every audit decorator, so a guard can see through it to the gateway it wraps.</summary>
public interface IAuditedGateway
{
    object Inner { get; }
}

public sealed class AuditedComputeGateway(IComputeGateway inner, CloudAuditor auditor) : IComputeGateway, IAuditedGateway
{
    public object Inner => inner;

    private static string Vm(string zone, string name) => $"zones/{zone}/instances/{name}";

    public Task<VmDescriptor> CreateVmAsync(VmSpec spec, string zone, CancellationToken cancellationToken)
        => auditor.RunAsync("Compute.CreateVm", Vm(zone, spec.VmName), spec.JobId, () => inner.CreateVmAsync(spec, zone, cancellationToken));

    public Task<VmDescriptor> ReuseVmAsync(VmSpec spec, string existingVmName, string zone, CancellationToken cancellationToken)
        => auditor.RunAsync("Compute.ReuseVm", Vm(zone, existingVmName), spec.JobId, () => inner.ReuseVmAsync(spec, existingVmName, zone, cancellationToken));

    public Task StopVmAsync(string vmName, string zone, CancellationToken cancellationToken)
        => auditor.RunAsync("Compute.StopVm", Vm(zone, vmName), CloudAuditor.JobIdFromVmName(vmName), () => inner.StopVmAsync(vmName, zone, cancellationToken));

    public Task DeleteVmAsync(string vmName, string zone, CancellationToken cancellationToken)
        => auditor.RunAsync("Compute.DeleteVm", Vm(zone, vmName), CloudAuditor.JobIdFromVmName(vmName), () => inner.DeleteVmAsync(vmName, zone, cancellationToken));

    public Task<IReadOnlyList<string>> ListAcceleratorZonesAsync(string acceleratorType, CancellationToken cancellationToken)
        => inner.ListAcceleratorZonesAsync(acceleratorType, cancellationToken);

    public Task<VmDescriptor?> GetVmAsync(string vmName, string zone, CancellationToken cancellationToken)
        => inner.GetVmAsync(vmName, zone, cancellationToken);

    public Task<IReadOnlyList<VmDescriptor>> FindByJobIdAsync(string jobId, CancellationToken cancellationToken)
        => inner.FindByJobIdAsync(jobId, cancellationToken);

    public Task<IReadOnlyList<VmDescriptor>> ListByInstallationAsync(string installationId, CancellationToken cancellationToken)
        => inner.ListByInstallationAsync(installationId, cancellationToken);

    public Task<IReadOnlyList<VmDescriptor>> ListAppVmsAsync(CancellationToken cancellationToken)
        => inner.ListAppVmsAsync(cancellationToken);
}

public sealed class AuditedStorageGateway(IStorageGateway inner, CloudAuditor auditor) : IStorageGateway, IAuditedGateway
{
    public object Inner => inner;

    public Task<string> EnsureBucketAsync(string projectId, CancellationToken cancellationToken)
        => auditor.RunAsync("Storage.EnsureBucket", $"projects/{projectId}", null, () => inner.EnsureBucketAsync(projectId, cancellationToken), bucket => $"buckets/{bucket}");

    public Task UploadAsync(string bucket, string objectKey, Stream content, CancellationToken cancellationToken)
    {
        // Only the job prefix is named: the part of the key below it can carry the user's own file name.
        var (prefix, jobId) = CloudAuditor.ObjectPrefix(objectKey);
        return auditor.RunAsync("Storage.Upload", $"buckets/{bucket}/{prefix}", jobId, () => inner.UploadAsync(bucket, objectKey, content, cancellationToken));
    }

    public Task<Stream> DownloadAsync(string bucket, string objectKey, CancellationToken cancellationToken)
        => inner.DownloadAsync(bucket, objectKey, cancellationToken);

    public Task<Stream?> TryDownloadAsync(string bucket, string objectKey, CancellationToken cancellationToken)
        => inner.TryDownloadAsync(bucket, objectKey, cancellationToken);

    public Task<IReadOnlyList<BucketDescriptor>> ListBucketsAsync(string projectId, CancellationToken cancellationToken)
        => inner.ListBucketsAsync(projectId, cancellationToken);
}

public sealed class AuditedProjectSetupGateway(IProjectSetupGateway inner, CloudAuditor auditor) : IProjectSetupGateway, IAuditedGateway
{
    public object Inner => inner;

    public Task EnableComputeApiAsync(string projectId, CancellationToken cancellationToken)
        => auditor.RunAsync("ProjectSetup.EnableComputeApi", $"projects/{projectId}/services/compute.googleapis.com", null, () => inner.EnableComputeApiAsync(projectId, cancellationToken));

    public Task<ProjectLifecycleState> GetProjectStateAsync(string projectId, CancellationToken cancellationToken)
        => inner.GetProjectStateAsync(projectId, cancellationToken);

    public Task<bool> IsBillingEnabledAsync(string projectId, CancellationToken cancellationToken)
        => inner.IsBillingEnabledAsync(projectId, cancellationToken);

    public Task<bool> IsComputeApiEnabledAsync(string projectId, CancellationToken cancellationToken)
        => inner.IsComputeApiEnabledAsync(projectId, cancellationToken);
}

public sealed class AuditedWorkerIdentityGateway(IWorkerIdentityGateway inner, CloudAuditor auditor) : IWorkerIdentityGateway, IAuditedGateway
{
    public object Inner => inner;

    // Creates the worker service account and its role, and writes the project and bucket bindings: the one IAM write seam.
    public Task<WorkerIdentity> EnsureWorkerIdentityAsync(string projectId, string bucket, CancellationToken cancellationToken)
        => auditor.RunAsync("Iam.EnsureWorkerIdentity", $"projects/{projectId}/buckets/{bucket}", null, () => inner.EnsureWorkerIdentityAsync(projectId, bucket, cancellationToken));
}

public sealed class AuditedServiceEnablementGateway(IServiceEnablementGateway inner, CloudAuditor auditor) : IServiceEnablementGateway, IAuditedGateway
{
    public object Inner => inner;

    public Task EnableServicesAsync(string projectId, IReadOnlyList<string> serviceIds, CancellationToken cancellationToken)
        => auditor.RunAsync("ServiceUsage.EnableServices", $"projects/{projectId}/services/{string.Join(',', serviceIds)}", null, () => inner.EnableServicesAsync(projectId, serviceIds, cancellationToken));

    public Task<bool> IsServiceEnabledAsync(string projectId, string serviceId, CancellationToken cancellationToken)
        => inner.IsServiceEnabledAsync(projectId, serviceId, cancellationToken);
}

public sealed class AuditedBillingGateway(IBillingGateway inner, CloudAuditor auditor) : IBillingGateway, IAuditedGateway
{
    public object Inner => inner;

    // The billing account id is not logged: the resource is the project it was linked to.
    public Task LinkProjectAsync(string projectId, string billingAccountId, CancellationToken cancellationToken)
        => auditor.RunAsync("Billing.LinkProject", $"projects/{projectId}", null, () => inner.LinkProjectAsync(projectId, billingAccountId, cancellationToken));

    public Task<BillingStatus> GetBillingStatusAsync(string projectId, CancellationToken cancellationToken)
        => inner.GetBillingStatusAsync(projectId, cancellationToken);

    public Task<IReadOnlyList<BillingAccountSummary>> ListOpenBillingAccountsAsync(CancellationToken cancellationToken)
        => inner.ListOpenBillingAccountsAsync(cancellationToken);
}

public sealed class AuditedProjectCatalogGateway(IProjectCatalogGateway inner, CloudAuditor auditor) : IProjectCatalogGateway, IAuditedGateway
{
    public object Inner => inner;

    public Task<ProjectSummary> CreateProjectAsync(string projectId, string displayName, string installationId, CancellationToken cancellationToken)
        => auditor.RunAsync("ProjectCatalog.CreateProject", $"projects/{projectId}", null, () => inner.CreateProjectAsync(projectId, displayName, installationId, cancellationToken));

    public Task<IReadOnlyList<ProjectSummary>> ListActiveProjectsAsync(CancellationToken cancellationToken)
        => inner.ListActiveProjectsAsync(cancellationToken);

    public Task<ProjectSummary?> GetProjectAsync(string projectId, CancellationToken cancellationToken)
        => inner.GetProjectAsync(projectId, cancellationToken);
}
