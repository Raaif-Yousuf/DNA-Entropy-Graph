using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Cloud;

namespace DnaEntropyGraph.Cloud;

// Issue #830: the scoped face of the fake. Every interface method takes the CloudScope the real gateways take, records the (project, account)
// it was called with (Calls), enforces what the real gateways enforce (an account that is signed out fails with AccountAuthException, a project
// the account cannot reach is a 403, a VM of another project is not found) and then runs the one body the fake always had. The unscoped public
// overloads in FakeGcp.cs stay for tests that do not care which scope they act in: they see every project and record nothing.
public sealed partial class FakeGcp
{
    /// <summary>The account the unscoped fake always was; its calls reach every project unless <see cref="WithAccount"/> restricts others.</summary>
    public const string DefaultAccountSub = "fake-sub";

    /// <summary>One gateway call as the fake saw it: which method, in which project, as which account.</summary>
    public sealed record FakeCall(string Method, string ProjectId, string? AccountSub);

    private readonly object _callLock = new();
    private readonly List<FakeCall> _calls = [];
    private readonly Dictionary<string, ModeledAccount> _modeled = new(StringComparer.Ordinal);
    private string _activeSub = DefaultAccountSub;
    private static readonly AsyncLocal<CloudScope?> CallScope = new();

    private sealed class ModeledAccount(AccountInfo info, HashSet<string> projects)
    {
        public AccountInfo Info { get; } = info;

        public HashSet<string> Projects { get; } = projects;

        public bool SignedIn { get; set; } = true;

        /// <summary>When set, every call scoped to this account throws <see cref="AccountAuthException"/> with this code (a failure that is not an expired sign-in).</summary>
        public string? FailureCode { get; set; }

        public string? SelectedProject { get; set; }
    }

    /// <summary>Every scoped call so far, in order. A test reads it to prove WHICH project and account an action ran as.</summary>
    public IReadOnlyList<FakeCall> Calls
    {
        get
        {
            lock (_callLock)
            {
                return [.. _calls];
            }
        }
    }

    /// <summary>The recorded calls of one method (for example <c>DeleteVm</c>).</summary>
    public IReadOnlyList<FakeCall> CallsTo(string method) => [.. Calls.Where(c => c.Method == method)];

    /// <summary>
    /// Models a second signed-in Google account that can reach only <paramref name="reachableProjects"/> (a call as it in any other project is a 403).
    /// It becomes selectable through <see cref="SwitchAccountAsync"/> and is listed in <see cref="Accounts"/>.
    /// </summary>
    public FakeGcp WithAccount(string sub, string email, params string[] reachableProjects)
    {
        _modeled[sub] = new ModeledAccount(new AccountInfo(sub, email), [.. reachableProjects]);
        return this;
    }

    /// <summary>The modeled account's sign-in expired: every call scoped to it throws <see cref="AccountAuthException"/> naming it.</summary>
    public FakeGcp WithAccountSignedOut(string sub)
    {
        _modeled[sub].SignedIn = false;
        return this;
    }

    /// <summary>The modeled account cannot be used for another reason than an expired sign-in (for example <see cref="AuthErrorCodes.NetworkUnavailable"/>): every call scoped to it throws <see cref="AccountAuthException"/> with <paramref name="code"/>, naming it.</summary>
    public FakeGcp WithAccountFailure(string sub, string code)
    {
        _modeled[sub].FailureCode = code;
        return this;
    }

    /// <summary>The project a modeled account has selected (what <see cref="SelectedScope"/> returns while it is the current account).</summary>
    public FakeGcp WithAccountProject(string sub, string projectId)
    {
        _modeled[sub].SelectedProject = projectId;
        return this;
    }

    private bool ActiveIsDefault => _activeSub == DefaultAccountSub || !_modeled.ContainsKey(_activeSub);

    public CloudScope? SelectedScope()
    {
        var project = SelectedProjectId;
        var account = CurrentAccount;
        return IsSignedIn && project is { Length: > 0 } && account is not null ? new CloudScope(project, account.Sub) : null;
    }

    /// <summary>The scope the unscoped overloads stand for: the selected project (or "fake-project") as the default account.</summary>
    public CloudScope DefaultScope => new(_selectedProjectId ?? "fake-project", DefaultAccountSub);

    private IDisposable EnterCall(CloudScope scope, string method)
    {
        ArgumentNullException.ThrowIfNull(scope);
        lock (_callLock)
        {
            _calls.Add(new FakeCall(method, scope.ProjectId, scope.AccountSub));
        }

        if (scope.AccountSub is { } sub && sub != DefaultAccountSub)
        {
            if (!_modeled.TryGetValue(sub, out var account) || !account.SignedIn)
            {
                throw new AccountAuthException(AuthErrorCodes.SigninExpired, "the account of this call is signed out", accountSub: sub);
            }

            if (account.FailureCode is { } failure)
            {
                throw new AccountAuthException(failure, "the account of this call cannot be used right now", accountSub: sub);
            }

            if (!account.Projects.Contains(scope.ProjectId))
            {
                throw Build(CloudErrorKind.Permission, "PERMISSION_DENIED", 403, "The signed-in account cannot use this project.");
            }
        }

        var previous = CallScope.Value;
        CallScope.Value = scope;
        return new Restore(previous);
    }

    private sealed class Restore(CloudScope? previous) : IDisposable
    {
        public void Dispose() => CallScope.Value = previous;
    }

    /// <summary>
    /// A scope naming this "project" makes the FAKE list and act across every project (test call sites that do not care which project they act in). It is a fake-only
    /// wildcard: it does not skip the account check above, and <see cref="CallAccount.Use"/> blocks only a blank project id, so a real gateway handed "*" would address a project
    /// literally named "*" (Google refuses it), never every project.
    /// </summary>
    public const string AnyProject = "*";

    private static bool InProject(VmDescriptor vm, CloudScope scope) => scope.ProjectId is AnyProject or "" || vm.Scope is null || string.Equals(vm.Scope.ProjectId, scope.ProjectId, StringComparison.Ordinal);

    private static IReadOnlyList<VmDescriptor> InProject(IReadOnlyList<VmDescriptor> vms, CloudScope scope) => [.. vms.Where(vm => InProject(vm, scope))];

    /// <summary>True when a VM named so exists but belongs to another project: to this scope it is not there.</summary>
    private bool ElsewhereVm(CloudScope scope, string vmName, string zone) => _vms.TryGetValue((vmName, zone), out var vm) && !InProject(vm, scope);

    // ---------------------------------------------------------------- IComputeGateway

    public Task<VmDescriptor> CreateVmAsync(CloudScope scope, VmSpec spec, string zone, CancellationToken cancellationToken)
    {
        using var _ = EnterCall(scope, "CreateVm");
        if (scope.ProjectId != AnyProject && !string.Equals(scope.ProjectId, spec.ProjectId, StringComparison.Ordinal))
        {
            throw Build(CloudErrorKind.Other, "PROJECT_MISMATCH", null, "The VM spec names a different Google Cloud project from the scope, so no VM was created.");
        }

        return CreateVmAsync(spec, zone, cancellationToken);
    }

    public Task<VmDescriptor> ReuseVmAsync(CloudScope scope, VmSpec spec, string existingVmName, string zone, CancellationToken cancellationToken)
    {
        using var _ = EnterCall(scope, "ReuseVm");
        return ElsewhereVm(scope, existingVmName, zone)
            ? throw Build(CloudErrorKind.Other, "VM_NOT_FOUND", 404, $"The VM '{existingVmName}' is not in this project.")
            : ReuseVmAsync(spec, existingVmName, zone, cancellationToken);
    }

    public Task<IReadOnlyList<string>> ListAcceleratorZonesAsync(CloudScope scope, string acceleratorType, CancellationToken cancellationToken)
    {
        using var _ = EnterCall(scope, "ListAcceleratorZones");
        return ListAcceleratorZonesAsync(acceleratorType, cancellationToken);
    }

    public Task StartVmAsync(CloudScope scope, string vmName, string zone, CancellationToken cancellationToken)
    {
        using var _ = EnterCall(scope, "StartVm");
        return ElsewhereVm(scope, vmName, zone)
            ? throw Build(CloudErrorKind.Other, "VM_NOT_FOUND", 404, $"The VM '{vmName}' is not in this project.")
            : StartVmAsync(vmName, zone, cancellationToken);
    }

    public async Task<IReadOnlyList<DiskDescriptor>> ListDisksByInstallationAsync(CloudScope scope, string installationId, CancellationToken cancellationToken)
    {
        using var _ = EnterCall(scope, "ListDisksByInstallation");
        return await ListDisksByInstallationAsync(installationId, cancellationToken).ConfigureAwait(false);
    }

    public Task DeleteDiskAsync(CloudScope scope, string diskName, string zone, CancellationToken cancellationToken)
    {
        using var _ = EnterCall(scope, "DeleteDisk");
        return DeleteDiskAsync(diskName, zone, cancellationToken);
    }

    public async Task<VmDescriptor?> GetVmAsync(CloudScope scope, string vmName, string zone, CancellationToken cancellationToken)
    {
        using var _ = EnterCall(scope, "GetVm");
        var vm = await GetVmAsync(vmName, zone, cancellationToken).ConfigureAwait(false);
        return vm is not null && InProject(vm, scope) ? vm : null;
    }

    public Task<bool> WasPreemptedAsync(CloudScope scope, string vmName, string zone, VmIncarnation incarnation, CancellationToken cancellationToken)
    {
        using var _ = EnterCall(scope, "WasPreempted");
        return ElsewhereVm(scope, vmName, zone) ? Task.FromResult(false) : WasPreemptedAsync(vmName, zone, incarnation, cancellationToken);
    }

    public Task StopVmAsync(CloudScope scope, string vmName, string zone, CancellationToken cancellationToken)
    {
        using var _ = EnterCall(scope, "StopVm");
        return ElsewhereVm(scope, vmName, zone) ? Task.CompletedTask : StopVmAsync(vmName, zone, cancellationToken);
    }

    public Task DeleteVmAsync(CloudScope scope, string vmName, string zone, CancellationToken cancellationToken)
    {
        using var _ = EnterCall(scope, "DeleteVm");
        return ElsewhereVm(scope, vmName, zone) ? Task.CompletedTask : DeleteVmAsync(vmName, zone, cancellationToken);
    }

    public Task EndVmAsync(CloudScope scope, string vmName, string zone, bool delete, Action? onAccepted, CancellationToken cancellationToken)
    {
        using var _ = EnterCall(scope, delete ? "DeleteVm" : "StopVm");
        if (ElsewhereVm(scope, vmName, zone))
        {
            onAccepted?.Invoke();
            return Task.CompletedTask;
        }

        return EndVmAsync(vmName, zone, delete, onAccepted, cancellationToken);
    }

    public async Task<IReadOnlyList<VmDescriptor>> FindByJobIdAsync(CloudScope scope, string jobId, CancellationToken cancellationToken)
    {
        using var _ = EnterCall(scope, "FindByJobId");
        return InProject(await FindByJobIdAsync(jobId, cancellationToken).ConfigureAwait(false), scope);
    }

    public async Task<IReadOnlyList<VmDescriptor>> ListByInstallationAsync(CloudScope scope, string installationId, CancellationToken cancellationToken)
    {
        using var _ = EnterCall(scope, "ListByInstallation");
        return InProject(await ListByInstallationAsync(installationId, cancellationToken).ConfigureAwait(false), scope);
    }

    public async Task<IReadOnlyList<VmDescriptor>> ListAppVmsAsync(CloudScope scope, CancellationToken cancellationToken)
    {
        using var _ = EnterCall(scope, "ListAppVms");
        return InProject(await ListAppVmsAsync(cancellationToken).ConfigureAwait(false), scope);
    }

    // ---------------------------------------------------------------- IStorageGateway

    public Task<string> EnsureBucketAsync(CloudScope scope, CancellationToken cancellationToken)
    {
        using var _ = EnterCall(scope, "EnsureBucket");
        return EnsureBucketAsync(scope.ProjectId, cancellationToken);
    }

    public Task<IReadOnlyList<BucketDescriptor>> ListBucketsAsync(CloudScope scope, CancellationToken cancellationToken)
    {
        using var _ = EnterCall(scope, "ListBuckets");
        return ListBucketsAsync(scope.ProjectId, cancellationToken);
    }

    public Task UploadAsync(CloudScope scope, string bucket, string objectKey, Stream content, CancellationToken cancellationToken)
    {
        using var _ = EnterCall(scope, "Upload");
        return UploadAsync(bucket, objectKey, content, cancellationToken);
    }

    public Task<Stream> DownloadAsync(CloudScope scope, string bucket, string objectKey, CancellationToken cancellationToken)
    {
        using var _ = EnterCall(scope, "Download");
        return DownloadAsync(bucket, objectKey, cancellationToken);
    }

    public Task<Stream?> TryDownloadAsync(CloudScope scope, string bucket, string objectKey, CancellationToken cancellationToken)
    {
        using var _ = EnterCall(scope, "TryDownload");
        return TryDownloadAsync(bucket, objectKey, cancellationToken);
    }

    public Task<bool> TryDownloadToFileAsync(CloudScope scope, string bucket, string objectKey, string destinationPath, CancellationToken cancellationToken)
    {
        using var _ = EnterCall(scope, "TryDownloadToFile");
        return TryDownloadToFileAsync(bucket, objectKey, destinationPath, cancellationToken);
    }

    public Task<PrefixUsage> GetPrefixUsageAsync(CloudScope scope, string bucket, string prefix, CancellationToken cancellationToken)
    {
        using var _ = EnterCall(scope, "GetPrefixUsage");
        return GetPrefixUsageAsync(bucket, prefix, cancellationToken);
    }

    public Task<IReadOnlyList<string>> ListChildPrefixesAsync(CloudScope scope, string bucket, string prefix, CancellationToken cancellationToken)
    {
        using var _ = EnterCall(scope, "ListChildPrefixes");
        return ListChildPrefixesAsync(bucket, prefix, cancellationToken);
    }

    public Task<int> DeletePrefixAsync(CloudScope scope, string bucket, string prefix, CancellationToken cancellationToken)
    {
        using var _ = EnterCall(scope, "DeletePrefix");
        return DeletePrefixAsync(bucket, prefix, cancellationToken);
    }

    public Task DeleteObjectAsync(CloudScope scope, string bucket, string objectKey, CancellationToken cancellationToken)
    {
        using var _ = EnterCall(scope, "DeleteObject");
        return DeleteObjectAsync(bucket, objectKey, cancellationToken);
    }

    // ---------------------------------------------------------------- IProjectSetupGateway, IQuotaGateway, IWorkerIdentityGateway

    public Task<ProjectLifecycleState> GetProjectStateAsync(CloudScope scope, CancellationToken cancellationToken)
    {
        using var _ = EnterCall(scope, "GetProjectState");
        return GetProjectStateAsync(scope.ProjectId, cancellationToken);
    }

    public Task<bool> IsBillingEnabledAsync(CloudScope scope, CancellationToken cancellationToken)
    {
        using var _ = EnterCall(scope, "IsBillingEnabled");
        return IsBillingEnabledAsync(scope.ProjectId, cancellationToken);
    }

    public Task<bool> IsComputeApiEnabledAsync(CloudScope scope, CancellationToken cancellationToken)
    {
        using var _ = EnterCall(scope, "IsComputeApiEnabled");
        return IsComputeApiEnabledAsync(scope.ProjectId, cancellationToken);
    }

    public Task EnableComputeApiAsync(CloudScope scope, CancellationToken cancellationToken)
    {
        using var _ = EnterCall(scope, "EnableComputeApi");
        return EnableComputeApiAsync(scope.ProjectId, cancellationToken);
    }

    public Task<int> GetGpuQuotaAsync(CloudScope scope, string region, string acceleratorType, CancellationToken cancellationToken)
    {
        using var _ = EnterCall(scope, "GetGpuQuota");
        return GetGpuQuotaAsync(scope.ProjectId, region, acceleratorType, cancellationToken);
    }

    public Task<QuotaReadResult> ReadQuotaAsync(CloudScope scope, CancellationToken cancellationToken)
    {
        using var _ = EnterCall(scope, "ReadQuota");
        return ReadQuotaAsync(scope.ProjectId, cancellationToken);
    }

    public Task<QuotaEligibility> GetQuotaIncreaseEligibilityAsync(CloudScope scope, string metric, CancellationToken cancellationToken)
    {
        using var _ = EnterCall(scope, "GetQuotaIncreaseEligibility");
        return GetQuotaIncreaseEligibilityAsync(scope.ProjectId, metric, cancellationToken);
    }

    public Task<QuotaIncreaseResult> RequestQuotaIncreaseAsync(CloudScope scope, QuotaIncreaseRequest request, CancellationToken cancellationToken)
    {
        using var _ = EnterCall(scope, "RequestQuotaIncrease");
        return RequestQuotaIncreaseAsync(scope.ProjectId, request, cancellationToken);
    }

    public Task<WorkerIdentity> EnsureWorkerIdentityAsync(CloudScope scope, string bucket, CancellationToken cancellationToken)
    {
        using var _ = EnterCall(scope, "EnsureWorkerIdentity");
        return EnsureWorkerIdentityAsync(scope.ProjectId, bucket, cancellationToken);
    }
}
