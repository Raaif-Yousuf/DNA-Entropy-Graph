using System.Collections.Concurrent;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Cloud;

namespace DnaEntropyGraph.Cloud;

/// <summary>
/// The in-memory double every other test project depends on instead of a
/// real Google Cloud project (Hard Rule 7, CLAUDE.md). Issue #49: the
/// always-succeeds baseline is not the point - the scripted failures are.
/// A test scripts exactly one failure shape with a fluent <c>With*</c> call
/// (e.g. <see cref="WithBillingOff"/>), and every gateway call after that
/// throws the same <see cref="CloudOperationException"/> shape a real
/// gateway mapping a real Google error would throw: a
/// <see cref="CloudError"/> plus the <see cref="CloudErrorKind"/> issue
/// #57's <see cref="CloudErrorClassifier"/> would independently derive from
/// it (see <c>FakeGcpScriptedFailureTests</c>'s round-trip tests, which
/// assert exactly that agreement).
///
/// Issue #257: VMs are keyed by <c>(Name, Zone)</c>, not name alone, because
/// that is what real Compute Engine actually enforces - a name is unique
/// only within a zone. That means this fake does NOT stop a second,
/// different zone's create for the same name from succeeding independently
/// (that is the real risk issue #389 describes; the fix is the caller-side
/// reconciliation in <see cref="FindByJobIdAsync"/>, not a fake that hides
/// the danger by pretending names are globally unique). What the fake DOES
/// enforce is the narrower, real guarantee: a retried create for the exact
/// same <c>(spec, zone)</c> - which, since <c>requestId == JobId</c> is
/// deterministic per spec, is indistinguishable from Compute Engine's own
/// request-id-based idempotent replay - returns the ORIGINAL successful
/// result rather than erroring or creating a second entry.
///
/// <see cref="FakeGcp"/> keeps a public, parameterless constructor (DI in
/// <c>ServiceRegistration.cs</c> resolves it that way); every scripted
/// failure is armed afterward through the fluent setters below, never
/// through a constructor argument.
/// </summary>
public sealed class FakeGcp : IComputeGateway, IStorageGateway, IProjectSetupGateway, IQuotaGateway, IGcpAccount
{
    private readonly TimeProvider _timeProvider;

    private readonly ConcurrentDictionary<(string Name, string Zone), VmDescriptor> _vms = new();

    // --- Account ---
    // Issue #424: a fresh profile is not signed in to any Google account and
    // has no selected project - the always-signed-in default here
    // contradicted that (MEASURED: the shell's status pill read a signed-in
    // project name on a fresh profile instead of "Not signed in"). A test
    // that needs a signed-in fake arms it explicitly through
    // WithSelectedProject/WithSignedOut below, the same fluent-setter
    // convention every other scripted state in this class already follows.
    private bool _signedIn;
    private string? _selectedProjectId;

    // --- Project-wide scripted failures (abort a zone ladder immediately) ---
    private readonly HashSet<string> _billingOffProjects = new(StringComparer.Ordinal);
    private readonly HashSet<string> _apiDisabledProjects = new(StringComparer.Ordinal);
    private readonly HashSet<string> _permissionDeniedProjects = new(StringComparer.Ordinal);
    private readonly HashSet<string> _orgPolicyBlockedProjects = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ProjectLifecycleState> _projectStates = new(StringComparer.Ordinal);

    // --- Per-attempt scripted failures (worth continuing past) ---
    private readonly Dictionary<string, int> _stockoutRemainingByZone = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _quotaExceededRemainingByZone = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<(string Name, string Zone)> _alreadyExistingVmKeys = new();
    private int _networkFailuresRemaining;

    // --- Quota table (IQuotaGateway) ---
    private readonly Dictionary<(string Region, string Accelerator), int> _regionalQuota = new();
    private int? _allRegionsGpuCap;
    private const int DefaultRegionalQuota = 1;

    // --- Preemption (a running VM discovered TERMINATED after a deadline) ---
    private readonly Dictionary<(string Name, string Zone), DateTimeOffset> _preemptAt = new();

    public FakeGcp()
        : this(TimeProvider.System)
    {
    }

    public FakeGcp(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
    }

    public bool IsSignedIn => _signedIn;

    public string? SelectedProjectId => _selectedProjectId;

    public Task SignInAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    // ----------------------------------------------------------------
    // Scripting API - one line per scenario, as issue #49 asks for.
    // ----------------------------------------------------------------

    /// <summary>The project's billing account is off. Every <see cref="CreateVmAsync"/> for it throws <see cref="CloudErrorKind.Billing"/>; preflight's <see cref="IsBillingEnabledAsync"/> reports it too.</summary>
    public FakeGcp WithBillingOff(string projectId)
    {
        _billingOffProjects.Add(projectId);
        return this;
    }

    /// <summary>The Compute Engine API is disabled on the project. <see cref="EnableComputeApiAsync"/> clears this, matching the real API's idempotent enable.</summary>
    public FakeGcp WithComputeApiOff(string projectId)
    {
        _apiDisabledProjects.Add(projectId);
        return this;
    }

    /// <summary>The signed-in identity lacks <c>compute.instances.create</c> (or <c>iam.serviceAccounts.actAs</c>) on the project - a 403, not a billing or API problem.</summary>
    public FakeGcp WithPermissionDenied(string projectId)
    {
        _permissionDeniedProjects.Add(projectId);
        return this;
    }

    /// <summary>An org policy constraint blocks the request (HTTP 412 / <c>CONDITION_NOT_MET</c>) - project-wide, aborts like billing/API/permission, but is not any of those three.</summary>
    public FakeGcp WithOrgPolicyBlocked(string projectId)
    {
        _orgPolicyBlockedProjects.Add(projectId);
        return this;
    }

    /// <summary>The project's lifecycle state <see cref="GetProjectStateAsync"/> reports (issue #388) - preflight step 1. Unset projects report <see cref="ProjectLifecycleState.Active"/>.</summary>
    public FakeGcp WithProjectState(string projectId, ProjectLifecycleState state)
    {
        _projectStates[projectId] = state;
        return this;
    }

    /// <summary>
    /// The next <paramref name="times"/> <see cref="CreateVmAsync"/> attempts
    /// in <paramref name="zone"/> fail with <see cref="CloudErrorKind.Stockout"/>
    /// (per-zone, transient); the attempt after that succeeds. Enables "the
    /// first two zones stock out and the third succeeds" as one line per
    /// zone, e.g. <c>.WithZoneStockout("us-central1-a").WithZoneStockout("us-central1-b")</c>
    /// (each defaults to failing forever) or an explicit count to let a
    /// retry against the *same* zone eventually succeed.
    /// </summary>
    public FakeGcp WithZoneStockout(string zone, int times = int.MaxValue)
    {
        _stockoutRemainingByZone[zone] = times;
        return this;
    }

    /// <summary>
    /// The next <paramref name="times"/> <see cref="CreateVmAsync"/> attempts
    /// in <paramref name="zone"/> fail with <see cref="CloudErrorKind.Quota"/>
    /// instead of <see cref="CloudErrorKind.Stockout"/> - the two are checked
    /// separately here on purpose (CLAUDE.md: "quota is not stockout").
    /// </summary>
    public FakeGcp WithQuotaExceededOnCreate(string zone, int times = int.MaxValue)
    {
        _quotaExceededRemainingByZone[zone] = times;
        return this;
    }

    /// <summary>
    /// A VM named <paramref name="vmName"/> already exists in
    /// <paramref name="zone"/> for a reason OTHER than this fake's own
    /// idempotent-replay tracking (e.g. adopted from a previous session).
    /// <see cref="CreateVmAsync"/> for that exact (name, zone) throws
    /// <see cref="CloudErrorKind.AlreadyExists"/> every time, and
    /// <see cref="GetVmAsync"/> finds it immediately, so a caller can adopt
    /// it rather than treat the create as a failure (docs/cloud_design.md
    /// section 3, step 4). Contrast this with a plain repeated
    /// <see cref="CreateVmAsync"/> call for a (spec, zone) THIS fake already
    /// created, which is treated as an idempotent replay (see this class's
    /// own doc comment) and returns success, not this error.
    /// </summary>
    public FakeGcp WithAlreadyExists(string vmName, string zone, string status = "RUNNING")
    {
        var key = (vmName, zone);
        _vms[key] = new VmDescriptor(vmName, zone, status);
        _alreadyExistingVmKeys.Add(key);
        return this;
    }

    /// <summary>The next <paramref name="count"/> <see cref="CreateVmAsync"/> calls (any project, any zone) fail with <see cref="CloudErrorKind.Network"/>, simulating a request that never reached Google at all.</summary>
    public FakeGcp WithNetworkFailures(int count)
    {
        _networkFailuresRemaining = count;
        return this;
    }

    /// <summary>Sets the regional GPU quota <see cref="GetGpuQuotaAsync"/> reports for one (region, accelerator) pair. Anything not set here reports the default of 1 (the always-succeeds baseline).</summary>
    public FakeGcp WithGpuQuota(string region, string acceleratorType, int available)
    {
        _regionalQuota[(region.ToLowerInvariant(), acceleratorType.ToLowerInvariant())] = available;
        return this;
    }

    /// <summary>
    /// Sets the project-wide <c>GPUS_ALL_REGIONS</c> cap, which
    /// <see cref="GetGpuQuotaAsync"/> then applies as a ceiling under every
    /// regional value (CLAUDE.md: "<c>GPUS_ALL_REGIONS=0</c> overrides a
    /// regional 1"). A cap of 0 here makes every region report 0 GPUs
    /// available regardless of <see cref="WithGpuQuota"/>.
    /// </summary>
    public FakeGcp WithAllRegionsGpuCap(int cap)
    {
        _allRegionsGpuCap = cap;
        return this;
    }

    /// <summary>
    /// The VM named <paramref name="vmName"/> in <paramref name="zone"/> is
    /// discovered <c>TERMINATED</c> (with <see cref="VmDescriptor.StatusReason"/>
    /// <c>"preempted"</c>) the first time <see cref="GetVmAsync"/> is polled
    /// at or after <paramref name="after"/> has elapsed on this instance's
    /// <see cref="TimeProvider"/>. Requires the <see cref="FakeGcp(TimeProvider)"/>
    /// constructor with a controllable time source to be deterministic in a
    /// test.
    /// </summary>
    public FakeGcp WithPreemption(string vmName, string zone, TimeSpan after)
    {
        _preemptAt[(vmName, zone)] = _timeProvider.GetUtcNow() + after;
        return this;
    }

    /// <summary>Explicitly the fresh-profile default (issue #424): signed out, with no selected project. Kept as an explicit setter, not just the implicit default, so a test can restate the state it depends on.</summary>
    public FakeGcp WithSignedOut()
    {
        _signedIn = false;
        _selectedProjectId = null;
        return this;
    }

    /// <summary>Arms this fake as signed in to <paramref name="projectId"/> (issue #424: the default is signed out, so a test that needs a signed-in account arms it explicitly here rather than relying on a constructor default).</summary>
    public FakeGcp WithSelectedProject(string projectId)
    {
        _signedIn = true;
        _selectedProjectId = projectId;
        return this;
    }

    // ----------------------------------------------------------------
    // IComputeGateway
    // ----------------------------------------------------------------

    public Task<VmDescriptor> CreateVmAsync(VmSpec spec, string zone, CancellationToken cancellationToken)
    {
        // Hard Rule 10, enforced here exactly as a real gateway must: a spec
        // missing a label, a maxRunDuration, or carrying a value the real
        // API would reject anyway, never reaches "creation" - checked
        // before any scripted failure below, so this ordering is provable
        // even when nothing is scripted at all.
        spec.EnsurePreconditions();

        var key = (spec.VmName, zone);

        if (_alreadyExistingVmKeys.Contains(key))
        {
            throw Build(
                CloudErrorKind.AlreadyExists,
                "ALREADY_EXISTS",
                409,
                $"The resource 'projects/{spec.ProjectId}/zones/{zone}/instances/{spec.VmName}' already exists");
        }

        if (_vms.TryGetValue(key, out var alreadyCreatedByUs))
        {
            // Issue #257: requestId == JobId is deterministic per (spec,
            // zone), so a repeated call here is indistinguishable from
            // Compute Engine's own request-id-based idempotent replay -
            // the original result comes back, not a second VM and not an
            // error. Real project-wide/quota/stockout checks are NOT
            // re-run, matching a real replayed operation.
            return Task.FromResult(alreadyCreatedByUs);
        }

        if (_networkFailuresRemaining > 0)
        {
            _networkFailuresRemaining--;
            throw Build(CloudErrorKind.Network, null, null, "Could not reach the server; connection timed out");
        }

        ThrowIfProjectWide(spec.ProjectId);

        if (Consume(_quotaExceededRemainingByZone, zone))
        {
            throw Build(
                CloudErrorKind.Quota,
                "QUOTA_EXCEEDED",
                null,
                $"Quota exceeded for quota metric in region of zone '{zone}'. Limit: 0.0.");
        }

        if (Consume(_stockoutRemainingByZone, zone))
        {
            throw Build(
                CloudErrorKind.Stockout,
                "ZONE_RESOURCE_POOL_EXHAUSTED",
                null,
                $"The zone '{zone}' does not have enough resources available to fulfill the request for accelerator.");
        }

        var vm = new VmDescriptor(spec.VmName, zone, "RUNNING");
        _vms[key] = vm;
        return Task.FromResult(vm);
    }

    public Task<VmDescriptor?> GetVmAsync(string vmName, string zone, CancellationToken cancellationToken)
    {
        var key = (vmName, zone);
        if (!_vms.TryGetValue(key, out var vm))
        {
            return Task.FromResult<VmDescriptor?>(null);
        }

        if (vm.Status == "RUNNING" && _preemptAt.TryGetValue(key, out var preemptAt) && _timeProvider.GetUtcNow() >= preemptAt)
        {
            vm = vm with { Status = "TERMINATED", StatusReason = "preempted" };
            _vms[key] = vm;
        }

        return Task.FromResult<VmDescriptor?>(vm);
    }

    public Task StopVmAsync(string vmName, string zone, CancellationToken cancellationToken)
    {
        var key = (vmName, zone);
        if (_vms.TryGetValue(key, out var vm))
        {
            _vms[key] = vm with { Status = "STOPPED", StatusReason = null };
        }

        return Task.CompletedTask;
    }

    public Task DeleteVmAsync(string vmName, string zone, CancellationToken cancellationToken)
    {
        _vms.TryRemove((vmName, zone), out _);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Issue #257/#389: scans every zone this fake knows about for a VM
    /// named <c>deg-&lt;jobId&gt;</c>, the same computation
    /// <see cref="VmSpec.VmName"/> uses. A real gateway would instead query
    /// by the <c>job-id</c> label via <c>aggregatedList</c> (Hard Rule 9);
    /// this fake does not store labels per VM, so it simulates the same
    /// observable behaviour - "every VM for this job, regardless of which
    /// zone it landed in" - by the one thing the fake DOES track precisely:
    /// the deterministic name. More than one result is not a fake bug, it
    /// is this fake correctly exposing #389's real risk when a caller
    /// creates in two zones without reconciling first.
    /// </summary>
    public Task<IReadOnlyList<VmDescriptor>> FindByJobIdAsync(string jobId, CancellationToken cancellationToken)
    {
        var name = $"deg-{jobId}";
        IReadOnlyList<VmDescriptor> matches = _vms.Values.Where(v => v.Name == name).ToList();
        return Task.FromResult(matches);
    }

    // ----------------------------------------------------------------
    // IStorageGateway
    // ----------------------------------------------------------------

    public Task<string> EnsureBucketAsync(string projectId, CancellationToken cancellationToken)
        => Task.FromResult($"deg-{projectId}-fake");

    public Task UploadAsync(string bucket, string objectKey, Stream content, CancellationToken cancellationToken)
        => Task.CompletedTask;

    public Task<Stream> DownloadAsync(string bucket, string objectKey, CancellationToken cancellationToken)
        => Task.FromResult<Stream>(new MemoryStream());

    // ----------------------------------------------------------------
    // IProjectSetupGateway
    // ----------------------------------------------------------------

    public Task<ProjectLifecycleState> GetProjectStateAsync(string projectId, CancellationToken cancellationToken)
        => Task.FromResult(_projectStates.TryGetValue(projectId, out var state) ? state : ProjectLifecycleState.Active);

    public Task<bool> IsBillingEnabledAsync(string projectId, CancellationToken cancellationToken)
        => Task.FromResult(!_billingOffProjects.Contains(projectId));

    public Task<bool> IsComputeApiEnabledAsync(string projectId, CancellationToken cancellationToken)
        => Task.FromResult(!_apiDisabledProjects.Contains(projectId));

    public Task EnableComputeApiAsync(string projectId, CancellationToken cancellationToken)
    {
        // Real `gcloud services enable` is idempotent; scripting it as an
        // immediate, unconditional clear matches that, minus the real
        // API's ~30-60s LRO delay (tracked as a known gap in issue #390).
        _apiDisabledProjects.Remove(projectId);
        return Task.CompletedTask;
    }

    // ----------------------------------------------------------------
    // IQuotaGateway
    // ----------------------------------------------------------------

    public Task<int> GetGpuQuotaAsync(string projectId, string region, string acceleratorType, CancellationToken cancellationToken)
    {
        var regional = _regionalQuota.TryGetValue((region.ToLowerInvariant(), acceleratorType.ToLowerInvariant()), out var configured)
            ? configured
            : DefaultRegionalQuota;

        // CLAUDE.md: "GPUS_ALL_REGIONS=0 overrides a regional 1" - the
        // project-wide cap is a ceiling under every region's own number.
        var effective = _allRegionsGpuCap.HasValue ? Math.Min(regional, _allRegionsGpuCap.Value) : regional;
        return Task.FromResult(effective);
    }

    // ----------------------------------------------------------------

    private void ThrowIfProjectWide(string projectId)
    {
        // Order matches docs/cloud_design.md section 2's preflight order
        // (billing before API before permission/org policy) so a project
        // scripted with more than one project-wide failure still reports
        // the one a real preflight run would surface first.
        if (_billingOffProjects.Contains(projectId))
        {
            throw Build(CloudErrorKind.Billing, "BILLING_DISABLED", 403, $"The billing account for the owning project '{projectId}' is disabled.");
        }

        if (_apiDisabledProjects.Contains(projectId))
        {
            throw Build(CloudErrorKind.ApiDisabled, "SERVICE_DISABLED", 403, $"Compute Engine API has not been used in project {projectId} before or it is disabled.");
        }

        if (_permissionDeniedProjects.Contains(projectId))
        {
            throw Build(CloudErrorKind.Permission, "IAM_PERMISSION_DENIED", 403, $"Required 'compute.instances.create' permission for 'projects/{projectId}'.");
        }

        if (_orgPolicyBlockedProjects.Contains(projectId))
        {
            throw Build(CloudErrorKind.OrgPolicy, "CONDITION_NOT_MET", 412, $"Operation denied by org policy on 'constraints/compute.requireOsLogin' for project '{projectId}'.");
        }
    }

    private static bool Consume(Dictionary<string, int> remainingByKey, string key)
    {
        if (!remainingByKey.TryGetValue(key, out var remaining) || remaining <= 0)
        {
            return false;
        }

        remainingByKey[key] = remaining - 1;
        return true;
    }

    private static CloudOperationException Build(CloudErrorKind kind, string? code, int? httpStatus, string message)
        => new(new CloudError(code, httpStatus, message), kind);
}
