using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
public sealed partial class FakeGcp : IComputeGateway, IStorageGateway, IProjectSetupGateway, IQuotaGateway, IGcpAccount, ICloudTokenRefresher
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
    // name -> the incarnation WithAlreadyExists put there: the create is refused only while that VM is the one under the name (#918)
    private readonly ConcurrentDictionary<(string Name, string Zone), ulong> _alreadyExistingVmKeys = new();
    private int _networkFailuresRemaining;

    // --- Transient / auth failures (issue #258): consumed by the next calls to ANY gateway method ---
    private readonly ConcurrentQueue<VmSpec> _createdSpecs = new();
    private int _timeoutsRemaining;
    private int _createAttempts;
    private CloudError? _createFailure;
    private int _createFailureRemaining;
    private CreateGate? _createGate;
    private int _transientFailuresRemaining;
    private CloudError? _transientError;
    private int _unauthorizedRemaining;

    // --- Bucket objects (IStorageGateway) and the simulated worker (issue #460) ---
    private readonly ConcurrentDictionary<(string Bucket, string Key), byte[]> _objects = new();
    private FakeWorkerMode _workerMode = FakeWorkerMode.Done;
    private WorkerSelfEnd _workerSelfEnd = WorkerSelfEnd.None;

    // A worker whose heartbeat moves on every look at status.json (FakeWorkerMode.HeartbeatingThenDone), issue #498.
    private readonly ConcurrentDictionary<(string Bucket, string Key), LiveWorker> _liveWorkers = new();

    // What the simulated worker does to its own VM, applied when result.json (or, for a worker that
    // never wrote one, its place) is first looked for: a real worker acts a moment AFTER it writes.
    private readonly ConcurrentDictionary<(string Bucket, string Key), Action> _pendingWorkerEnds = new();

    // --- Compute behaviours a real instance has and a synchronous fake hides ---
    private bool _deleteOfMissingVmIsNotFound;
    private bool _nextDeleteRacesAWorkerDelete;
    private int _stoppingPolls;
    private string _stoppedStatus = "STOPPED";
    // #918: the per-VM state below is keyed by the VM incarnation (InstanceId), never by name: a VM made again under the name starts with none of its predecessor's.
    private readonly ConcurrentDictionary<ulong, int> _stoppingPollsLeft = new();
    private int _hangsRemaining;
    private bool _hangsIgnoreCancellation;
    private int _getVmFailuresRemaining;
    private CloudError? _getVmError;
    private int _bootPolls;
    private readonly ConcurrentDictionary<ulong, int> _bootPollsLeft = new();
    private bool _enforceMaxRunDuration;
    private readonly ConcurrentDictionary<ulong, TimeSpan> _maxRunByVm = new();
    private bool _partialFilesOnFailedInputs;
    private TimeSpan _createDelay = TimeSpan.Zero;
    private long _firstDeleteDelayTicks;
    private bool _stopRejectedUnlessRunning;
    private int _findFailuresRemaining;
    private CloudError? _findError;
    private int _tryDownloadFailuresRemaining;
    private CloudError? _tryDownloadError;
    private string _workerFailureCode = "MODEL_OOM";
    private bool _notConnected;

    // --- Quota table (IQuotaGateway) ---
    private readonly ConcurrentDictionary<(string Region, string Accelerator), int> _regionalQuota = new();
    private int? _allRegionsGpuCap;
    private readonly ConcurrentDictionary<(string Region, string Accelerator), int> _regionalUsed = new();
    private CloudErrorKind? _quotaReadFailure;
    private const int DefaultRegionalQuota = 1;

    // --- Preemption (a running VM discovered TERMINATED after a deadline) ---
    private readonly ConcurrentDictionary<(string Name, string Zone), PreemptPlan> _preemptPlans = new();

    /// <summary>A preemption scheduled for ONE VM incarnation (null until the VM it was scheduled for exists); <paramref name="Records"/> is true for a Spot VM Compute takes back (it then records the preemption).</summary>
    private sealed record PreemptPlan(ulong? InstanceId, DateTimeOffset At, bool Records)
    {
        public bool AppliesTo(ulong? instanceId) => InstanceId is null || InstanceId == instanceId;
    }

    /// <summary>Drops the scheduled preemption of the incarnation <paramref name="instanceId"/> only: a VM made again under the same name keeps its own.</summary>
    private void ClearPlan((string Name, string Zone) key, ulong? instanceId)
    {
        BeforePlanCleanup?.Invoke();
        if (_preemptPlans.TryGetValue(key, out var plan) && plan.InstanceId is not null && plan.InstanceId == instanceId)
        {
            _preemptPlans.TryRemove(new KeyValuePair<(string Name, string Zone), PreemptPlan>(key, plan));
        }
    }

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

    /// <summary>
    /// Issue #424's other half: WizardViewModel.SignInAsync reads
    /// IGcpAccount.IsSignedIn right after awaiting this call to decide
    /// whether to navigate on - a no-op here would leave that flow wired to
    /// nothing against this fake. Signs in to "fake-project", the same
    /// project id this fake always reported before it defaulted to signed
    /// out; a test that needs a specific project id still arms it
    /// explicitly through <see cref="WithSelectedProject"/>.
    /// </summary>
    public Task SignInAsync(CancellationToken cancellationToken)
    {
        if (_signInErrorCode is not null)
        {
            throw new AccountAuthException(_signInErrorCode);
        }

        _signedIn = true;
        _selectedProjectId = "fake-project";
        AccountChanged?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    private static readonly AccountInfo FakeAccount = new("fake-sub", "fake-user@example.test");

    private string? _signInErrorCode;

    public AccountInfo? CurrentAccount => _signedIn ? FakeAccount : null;

    public IReadOnlyList<AccountInfo> Accounts => _signedIn ? [FakeAccount] : [];

    public event EventHandler? AccountChanged;

    /// <summary>The fake keeps no file, so its account list is never locked.</summary>
    public bool IsAccountsFileLocked => false;

    public void RecheckAccountsFile()
    {
    }

    /// <summary>Every <see cref="SignInAsync"/> fails with this <see cref="AuthErrorCodes"/> code, as the real account service does for a missing client file or a declined consent page.</summary>
    public FakeGcp WithSignInError(string code)
    {
        _signInErrorCode = code;
        return this;
    }

    public Task<bool> SignOutAsync(CancellationToken cancellationToken)
    {
        _signedIn = false;
        _selectedProjectId = null;
        AccountChanged?.Invoke(this, EventArgs.Empty);
        return Task.FromResult(true);
    }

    public Task SelectProjectAsync(string projectId, CancellationToken cancellationToken)
    {
        if (!ProjectIdGenerator.IsValid(projectId))
        {
            throw new AccountAuthException(AuthErrorCodes.ProjectInvalid);
        }

        if (!_signedIn)
        {
            throw new AccountAuthException(AuthErrorCodes.SigninExpired);
        }

        _selectedProjectId = projectId;
        AccountChanged?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    public Task SwitchAccountAsync(string sub, CancellationToken cancellationToken)
    {
        if (!_signedIn || sub != FakeAccount.Sub)
        {
            throw new AccountAuthException(AuthErrorCodes.AccountNotFound);
        }

        AccountChanged?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    // ----------------------------------------------------------------
    // Scripting API - one line per scenario, as issue #49 asks for.
    // ----------------------------------------------------------------

    // One lock for the whole billing-off set: the catalogue and billing fakes (FakeGcp.ProjectCatalog.cs) share it with the runner fake.
    private bool IsBillingOff(string projectId)
    {
        lock (_catalogGate)
        {
            return _billingOffProjects.Contains(projectId);
        }
    }

    /// <summary>The project's billing account is off. Every <see cref="CreateVmAsync"/> for it throws <see cref="CloudErrorKind.Billing"/>; preflight's <see cref="IsBillingEnabledAsync"/> reports it too.</summary>
    public FakeGcp WithBillingOff(string projectId)
    {
        lock (_catalogGate)
        {
            _billingOffProjects.Add(projectId);
        }

        return this;
    }

    /// <summary>The Compute Engine API is disabled on the project. <see cref="EnableComputeApiAsync"/> clears this, matching the real API's idempotent enable.</summary>
    public FakeGcp WithComputeApiOff(string projectId)
    {
        lock (_catalogGate)
        {
            _apiDisabledProjects.Add(projectId);
        }

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
        var id = (ulong)Interlocked.Increment(ref _nextInstanceId);
        var displaced = _vms.TryGetValue(key, out var live) ? live : null;
        // vm-row-create: inserts a NEW row (the #875 guard allows a bare assignment only here)
        _vms[key] = new VmDescriptor(vmName, zone, status, InstanceId: id);
        if (displaced is not null)
        {
            ForgetIncarnation(displaced); // the row it replaces is gone, and so is what belonged to it (#918)
        }

        ResetVmDeleted(vmName);
        _alreadyExistingVmKeys[key] = id;
        return this;
    }

    /// <summary>The next <paramref name="count"/> <see cref="CreateVmAsync"/> calls (any project, any zone) fail with <see cref="CloudErrorKind.Network"/>, simulating a request that never reached Google at all.</summary>
    public FakeGcp WithNetworkFailures(int count)
    {
        _networkFailuresRemaining = count;
        return this;
    }

    /// <summary>
    /// The next <paramref name="count"/> calls to ANY gateway method fail with
    /// HTTP <paramref name="httpStatus"/> (issue #258: 503 for the retry
    /// policy, 429 for rate limiting), then calls succeed again. The
    /// <see cref="CloudErrorKind"/> is whatever <see cref="CloudErrorClassifier"/>
    /// derives from the error, exactly as a real gateway would set it.
    /// </summary>
    public FakeGcp WithTransientFailures(int httpStatus, int count, string message = "The service is currently unavailable.", string? code = null)
    {
        _transientError = new CloudError(code, httpStatus, message);
        _transientFailuresRemaining = count;
        return this;
    }

    /// <summary>The next <paramref name="count"/> calls to ANY gateway method fail with HTTP 401 (an expired token). <see cref="RefreshAsync"/> does not clear them: they are consumed by calls.</summary>
    public FakeGcp WithUnauthorized(int count)
    {
        _unauthorizedRemaining = count;
        return this;
    }

    /// <summary>Every spec that passed <see cref="VmSpec.EnsurePreconditions"/> in <see cref="CreateVmAsync"/>, in order, including a replayed create (tests assert what was actually sent, e.g. the startup metadata, issue #261).</summary>
    public IReadOnlyList<VmSpec> CreatedSpecs => _createdSpecs.ToList();

    /// <summary>
    /// The next <paramref name="count"/> calls to ANY gateway method throw a
    /// <see cref="TaskCanceledException"/> while the caller's token is NOT
    /// cancelled: what an HttpClient or gRPC deadline looks like.
    /// </summary>
    public FakeGcp WithCallTimeouts(int count)
    {
        _timeoutsRemaining = count;
        return this;
    }

    /// <summary>
    /// The next <paramref name="count"/> <see cref="GetVmAsync"/> or <see cref="TryDownloadAsync"/> calls never
    /// answer until their token is cancelled: what a hung socket looks like (issue #460 review, finding 3).
    /// </summary>
    public FakeGcp WithHungCalls(int count, bool ignoreCancellation = false)
    {
        _hangsRemaining = count;
        _hangsIgnoreCancellation = ignoreCancellation;
        return this;
    }

    /// <summary>
    /// The first <see cref="DeleteVmAsync"/> takes <paramref name="delay"/> (honouring its token: a caller that gives up first leaves the VM
    /// in place), like a real delete operation that is still running. Later deletes are immediate.
    /// </summary>
    public FakeGcp WithFirstDeleteDelay(TimeSpan delay)
    {
        Interlocked.Exchange(ref _firstDeleteDelayTicks, delay.Ticks);
        return this;
    }

    /// <summary>
    /// Every <see cref="CreateVmAsync"/> answers only after <paramref name="delay"/>, ignoring its token, and the VM exists from
    /// then on: an insert the API accepted whose answer was slow (the caller may have given up by the time it lands).
    /// </summary>
    public FakeGcp WithCreateDelay(TimeSpan delay)
    {
        _createDelay = delay;
        return this;
    }

    /// <summary>
    /// <see cref="StopVmAsync"/> of a VM that is not RUNNING is rejected with a 400 precondition error.
    /// THEORY (unverified): <c>instances.stop</c> on a PROVISIONING, STAGING or STOPPING instance is rejected; nobody
    /// here has measured it. Off by default.
    /// </summary>
    public FakeGcp WithStopRejectedUnlessRunning()
    {
        _stopRejectedUnlessRunning = true;
        return this;
    }

    /// <summary>The next <paramref name="count"/> <see cref="FindByJobIdAsync"/> calls fail with exactly <paramref name="error"/>.</summary>
    public FakeGcp WithFindByJobIdFailure(CloudError error, int count)
    {
        _findError = error;
        _findFailuresRemaining = count;
        return this;
    }

    /// <summary>The next <paramref name="count"/> <see cref="TryDownloadAsync"/> calls fail with exactly <paramref name="error"/>.</summary>
    public FakeGcp WithTryDownloadFailure(CloudError error, int count)
    {
        _tryDownloadError = error;
        _tryDownloadFailuresRemaining = count;
        return this;
    }

    /// <summary>The next <paramref name="count"/> <see cref="GetVmAsync"/> calls (and only those) fail with HTTP <paramref name="httpStatus"/>.</summary>
    public FakeGcp WithGetVmFailures(int httpStatus, int count)
    {
        _getVmError = new CloudError(null, httpStatus, "The service is currently unavailable.");
        _getVmFailuresRemaining = count;
        return this;
    }

    /// <summary>The next <paramref name="count"/> <see cref="GetVmAsync"/> calls fail with exactly <paramref name="error"/>, classified as a real gateway would.</summary>
    public FakeGcp WithGetVmFailure(CloudError error, int count)
    {
        _getVmError = error;
        _getVmFailuresRemaining = count;
        return this;
    }

    /// <summary>
    /// A new VM reads <c>PROVISIONING</c> for <paramref name="polls"/> calls of <see cref="GetVmAsync"/> before it reads
    /// <c>RUNNING</c>. THEORY (unverified): a real instance is PROVISIONING then STAGING for a short while right after
    /// insert; nobody here has measured how long, or whether the first read can still show it.
    /// </summary>
    public FakeGcp WithBootStatusPolls(int polls)
    {
        _bootPolls = polls;
        return this;
    }

    /// <summary>
    /// The platform deletes a running VM once its <c>maxRunDuration</c> has passed since it last started (Hard Rule 10's
    /// <c>instanceTerminationAction=DELETE</c>); off by default. A VM past its limit is gone at the next look,
    /// by <see cref="GetVmAsync"/> or <see cref="FindByJobIdAsync"/>, measured on this fake's <see cref="TimeProvider"/>.
    /// </summary>
    public FakeGcp WithMaxRunDurationEnforced()
    {
        _enforceMaxRunDuration = true;
        return this;
    }

    private bool _runClockFromCreation;

    /// <summary>
    /// The other reading of the platform's <c>maxRunDuration</c>: its clock counts from the instance's CREATION and never restarts, so a VM
    /// reused days later is deleted soon after it starts. THEORY (unverified, ToTest #94) which reading is real; the app must end such a
    /// run in a recorded terminal state with nothing left billing under either. Needs <see cref="WithMaxRunDurationEnforced"/>.
    /// </summary>
    public FakeGcp WithRunClockFromCreation()
    {
        _runClockFromCreation = true;
        return this;
    }

    private void ExpireOverdueVms()
    {
        if (!_enforceMaxRunDuration)
        {
            return;
        }

        var now = _timeProvider.GetUtcNow();
        foreach (var (key, vm) in _vms)
        {
            // The platform limits a VM that is running. THEORY (unverified, ToTest #94): its clock restarts at each start (lastStartTimestamp),
            // not at creation. The other reading is available through WithRunClockFromCreation so the app is tested safe under either.
            var basis = _runClockFromCreation ? vm.CreatedAt : vm.RunClockStart;
            if (vm.Status is not ("STOPPED" or "TERMINATED" or "STOPPING") && basis is { } clockStart && _maxRunByVm.TryGetValue(IncarnationOf(vm), out var limit) && now >= clockStart + limit)
            {
                // Only the row inspected: a row that changed or was made again meanwhile is not expired by this look.
                if (_vms.TryRemove(new KeyValuePair<(string Name, string Zone), VmDescriptor>(key, vm)))
                {
                    ForgetIncarnation(vm);
                }
            }
        }
    }

    /// <summary>
    /// Makes the simulated worker act now for a VM that already exists, as if it had finished a moment ago: writes what
    /// <paramref name="mode"/> writes (result.json last). Needs the job's <c>manifest.json</c> in the bucket, like a real worker.
    /// </summary>
    public FakeGcp SimulateWorkerFinishing(VmSpec spec, string zone, FakeWorkerMode mode = FakeWorkerMode.Done)
    {
        var before = _workerMode;
        _workerMode = mode;
        try
        {
            RunSimulatedWorker(spec, zone);
        }
        finally
        {
            _workerMode = before;
        }

        return this;
    }

    /// <summary>A failed input in the simulated result.json still lists the partial file the real worker keeps (docs/job_contract.md section 7).</summary>
    public FakeGcp WithPartialFilesOnFailedInputs()
    {
        _partialFilesOnFailedInputs = true;
        return this;
    }

    /// <summary>The <c>error.code</c> the failure worker modes report (default <c>MODEL_OOM</c>).</summary>
    public FakeGcp WithWorkerFailureCode(string code)
    {
        _workerFailureCode = code;
        return this;
    }

    /// <summary>
    /// <see cref="DeleteVmAsync"/> of a VM that does not exist throws a not-found (HTTP 404), as real Compute
    /// Engine does. Off by default, where it is a silent no-op.
    /// </summary>
    public FakeGcp WithDeleteOfMissingVmNotFound()
    {
        _deleteOfMissingVmIsNotFound = true;
        return this;
    }

    /// <summary>
    /// The next <see cref="DeleteVmAsync"/> of an existing VM loses a race: the VM is gone by the time the request
    /// lands, so Compute Engine answers 404 for a VM the caller saw a moment ago (a worker deleting itself).
    /// </summary>
    public FakeGcp WithNextDeleteRacingAWorkerDelete()
    {
        _nextDeleteRacesAWorkerDelete = true;
        return this;
    }

    /// <summary>
    /// How a stop looks to a poller: the VM reads <c>STOPPING</c> for <paramref name="stoppingPolls"/> calls of
    /// <see cref="GetVmAsync"/>, then <paramref name="stoppedStatus"/>. THEORY (unverified): a stopped Compute Engine
    /// instance reports <c>TERMINATED</c>, nobody here has measured it, so a test can pass either spelling.
    /// </summary>
    public FakeGcp WithStopBehaviour(int stoppingPolls, string stoppedStatus = "STOPPED")
    {
        _stoppingPolls = stoppingPolls;
        _stoppedStatus = stoppedStatus;
        return this;
    }

    /// <summary>
    /// The simulated worker stops or deletes its own VM through the Compute API once it has written its result,
    /// as <c>worker/vm/startup.sh</c> does (exit codes 10 and 11). The action lands the first time anyone looks for
    /// result.json, so the runner's own health check right after create still sees a RUNNING VM.
    /// </summary>
    public FakeGcp WithWorkerEndingItsOwnVm(WorkerSelfEnd end)
    {
        _workerSelfEnd = end;
        return this;
    }

    /// <summary>
    /// This build has no real Google layer behind it: every preflight fails with a
    /// <c>CLOUD_NOT_CONNECTED</c> error before anything is uploaded or created. The production
    /// composition uses this so a run can never "complete" with simulated results.
    /// </summary>
    public FakeGcp WithCloudNotConnected()
    {
        _notConnected = true;
        return this;
    }

    /// <summary>Undoes <see cref="WithCloudNotConnected"/>: the connection "comes back" (what a reconnect test needs).</summary>
    public FakeGcp WithCloudConnected()
    {
        _notConnected = false;
        return this;
    }

    /// <summary>The error code <see cref="WithCloudNotConnected"/> throws under; the runner maps it to <c>cloud_not_connected</c>.</summary>
    public const string NotConnectedErrorCode = RunErrorCodes.NotConnectedGatewayCode;

    private int _hungCalls;

    /// <summary>How many calls have been parked by <see cref="WithHungCalls"/> so far: a test waits on this to know a caller is really inside the call.</summary>
    public int HungCalls => Volatile.Read(ref _hungCalls);

    private readonly List<(int Count, TaskCompletionSource Signal)> _hungWaiters = [];
    private readonly Dictionary<string, TaskCompletionSource> _deletedVms = [];

    /// <summary>Completes once a delete has removed the VM named <paramref name="vmName"/> (already complete if one did before the call): a signal for a test, instead of polling the VM list on a real clock.</summary>
    public Task WhenVmDeletedAsync(string vmName)
    {
        lock (_deletedVms)
        {
            if (!_deletedVms.TryGetValue(vmName, out var signal))
            {
                _deletedVms[vmName] = signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            return signal.Task;
        }
    }

    /// <summary>A VM made under a name that an earlier delete already signalled is a new VM: its delete has not happened yet, so the old signal is dropped (a test waiting on the second delete must not be let through by the first).</summary>
    private void ResetVmDeleted(string vmName)
    {
        lock (_deletedVms)
        {
            if (_deletedVms.TryGetValue(vmName, out var signal) && signal.Task.IsCompleted)
            {
                _deletedVms.Remove(vmName);
            }
        }
    }

    private void SignalVmDeleted(string vmName)
    {
        lock (_deletedVms)
        {
            if (!_deletedVms.TryGetValue(vmName, out var signal))
            {
                _deletedVms[vmName] = signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            signal.TrySetResult();
        }
    }

    /// <summary>
    /// Completes once <see cref="HungCalls"/> has reached <paramref name="count"/>: the signal a test awaits to know a caller is parked inside the call, instead of polling
    /// <see cref="HungCalls"/> on a real clock. Already complete when the count was reached before the call.
    /// </summary>
    public Task WhenHungAsync(int count = 1)
    {
        lock (_hungWaiters)
        {
            if (HungCalls >= count)
            {
                return Task.CompletedTask;
            }

            var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _hungWaiters.Add((count, signal));
            return signal.Task;
        }
    }

    private bool ConsumeHang()
    {
        while (true)
        {
            var left = Volatile.Read(ref _hangsRemaining);
            if (left <= 0)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref _hangsRemaining, left - 1, left) == left)
            {
                lock (_hungWaiters)
                {
                    var hung = Interlocked.Increment(ref _hungCalls);
                    _hungWaiters.RemoveAll(w =>
                    {
                        if (hung < w.Count)
                        {
                            return false;
                        }

                        w.Signal.TrySetResult();
                        return true;
                    });
                }

                return true;
            }
        }
    }

    /// <summary>What <see cref="OnCallParked"/> was told is parked.</summary>
    public enum ParkedCall
    {
        /// <summary>A call that never answers (<see cref="WithHungCalls"/>).</summary>
        Hang,

        /// <summary>A create that answers after its delay (<see cref="WithCreateDelay"/>).</summary>
        SlowCreate,

        /// <summary>A create that ignores its token and never answers (<see cref="WithBlockedCreate"/>): its caller then waits out the settle window before giving up on it.</summary>
        CreateThatNeverLands,
    }

    /// <summary>
    /// Called, synchronously and before the call parks, whenever a scripted call is about to wait without answering: a hung call
    /// (<see cref="WithHungCalls"/>, span <see cref="Timeout.InfiniteTimeSpan"/>) or a slow create (<see cref="WithCreateDelay"/>,
    /// its delay). A test that runs the caller on a virtual clock (#567) moves that clock from here, so a deadline fires because
    /// a call the test itself scripted is known to be parked, never because a machine was slow: a healthy call never calls this,
    /// so time cannot pass under it. Null in production, where nothing sets it.
    /// </summary>
    public Action<ParkedCall, TimeSpan>? OnCallParked { get; set; }

    private async Task<T> HangAsync<T>(CancellationToken cancellationToken)
    {
        OnCallParked?.Invoke(ParkedCall.Hang, Timeout.InfiniteTimeSpan);
        // A callee that ignores its token is what a stuck socket under a library that never checks it looks like.
        await Task.Delay(Timeout.Infinite, _hangsIgnoreCancellation ? CancellationToken.None : cancellationToken).ConfigureAwait(false);
        return default!;
    }

    /// <summary>
    /// The next <paramref name="times"/> <see cref="CreateVmAsync"/> calls (any
    /// zone) fail with <paramref name="error"/>, classified as a real gateway
    /// would. Unlike <see cref="WithTransientFailures"/> only creates consume
    /// it, so a test can script "every zone answers 503 ZONE_RESOURCE_POOL_EXHAUSTED"
    /// without preflight calls using it up.
    /// </summary>
    public FakeGcp WithCreateFailure(CloudError error, int times)
    {
        _createFailure = error;
        _createFailureRemaining = times;
        return this;
    }

    /// <summary>How many <see cref="CreateVmAsync"/> calls passed spec validation (including ones that then failed).</summary>
    public int CreateAttempts => Volatile.Read(ref _createAttempts);

    /// <summary>
    /// Makes <see cref="CreateVmAsync"/> block until <see cref="ReleaseCreate"/>
    /// (a create in flight). With <paramref name="ignoreCancellation"/> the
    /// call does not honour its token while blocked, like a request already
    /// accepted by the API: the VM then exists even though the caller gave up.
    /// </summary>
    public FakeGcp WithBlockedCreate(bool ignoreCancellation = false)
    {
        _createGate = new CreateGate(ignoreCancellation);
        return this;
    }

    /// <summary>Completes when a blocked <see cref="CreateVmAsync"/> has started waiting.</summary>
    public Task CreateVmEntered => _createGate?.Entered.Task ?? Task.CompletedTask;

    public void ReleaseCreate() => _createGate?.Release.TrySetResult();

    private sealed class CreateGate
    {
        public CreateGate(bool ignoreCancellation) => IgnoreCancellation = ignoreCancellation;

        public bool IgnoreCancellation { get; }

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>How many times the resilience pipeline asked this fake to refresh the token.</summary>
    public int TokenRefreshCount { get; private set; }

    /// <summary>A real implementation refreshes the OAuth access token; the fake only counts the request.</summary>
    public Task RefreshAsync(CancellationToken cancellationToken)
    {
        TokenRefreshCount++;
        return Task.CompletedTask;
    }

    private void ThrowIfScriptedTransient()
    {
        if (_timeoutsRemaining > 0)
        {
            _timeoutsRemaining--;
            throw new TaskCanceledException("The request was canceled due to the configured timeout.");
        }

        if (_unauthorizedRemaining > 0)
        {
            _unauthorizedRemaining--;
            var unauthorized = new CloudError("UNAUTHENTICATED", 401, "Request had invalid authentication credentials.");
            throw new CloudOperationException(unauthorized, CloudErrorClassifier.Classify(unauthorized));
        }

        if (_transientFailuresRemaining > 0 && _transientError is not null)
        {
            _transientFailuresRemaining--;
            throw new CloudOperationException(_transientError, CloudErrorClassifier.Classify(_transientError));
        }
    }

    /// <summary>Sets the regional GPU quota <see cref="GetGpuQuotaAsync"/> reports for one (region, accelerator) pair. Anything not set here reports the default of 1 (the always-succeeds baseline).</summary>
    public FakeGcp WithGpuQuota(string region, string acceleratorType, int available)
    {
        _regionalQuota[(region.ToLowerInvariant(), acceleratorType.ToLowerInvariant())] = available;
        return this;
    }

    /// <summary>Sets the regional GPU quota to a limit of <paramref name="limit"/> with <paramref name="used"/> already in use (the quota read then reports limit and usage, so the remainder is their difference).</summary>
    public FakeGcp WithGpuQuotaUsed(string region, string acceleratorType, int limit, int used)
    {
        _regionalQuota[(region.ToLowerInvariant(), acceleratorType.ToLowerInvariant())] = limit;
        _regionalUsed[(region.ToLowerInvariant(), acceleratorType.ToLowerInvariant())] = used;
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

    /// <summary>Makes <see cref="ReadQuotaAsync"/> report a failed read of the given kind (UNKNOWN quota, never zero), as a 403 or an outage would.</summary>
    public FakeGcp WithQuotaReadFailing(CloudErrorKind kind)
    {
        _quotaReadFailure = kind;
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
        // Bound to the VM that exists now; scheduled before the VM exists, it binds to the VM created next.
        _preemptPlans[(vmName, zone)] = new PreemptPlan(_vms.TryGetValue((vmName, zone), out var existing) ? existing.InstanceId : null, _timeProvider.GetUtcNow() + after, false);
        return this;
    }

    private int _spotPreemptionsLeft;
    private int _onDemandLossesLeft;
    private bool _preemptionLookupFails;
    private int _preemptionLookups;
    private readonly ConcurrentQueue<(string Name, string Zone, ulong? Id, DateTimeOffset At)> _preemptedOperations = new();
    private long _nextInstanceId = 1000;
    private int _preemptionRecordedAtLookup = 1;

    /// <summary>Issue #91: Compute records a preemption a moment after the instance is gone: a look before the <paramref name="lookup"/>th one finds nothing (default 1: visible at once).</summary>
    public FakeGcp WithPreemptionVisibleFromLookup(int lookup)
    {
        _preemptionRecordedAtLookup = lookup;
        return this;
    }

    /// <summary>Issue #91: Compute recorded a preemption of the named VM, as if it had been taken back (the id is the current incarnation's, or unknown when no such VM exists).</summary>
    public void RecordPreemptionOf(string vmName, string zone)
        => _preemptedOperations.Enqueue((vmName, zone, _vms.TryGetValue((vmName, zone), out var vm) ? vm.InstanceId : null, _timeProvider.GetUtcNow()));

    /// <summary>Issue #91: an old preemption record for a name and zone, left by an EARLIER incarnation (<paramref name="instanceId"/>), which must never count for a newer VM of the same name.</summary>
    public FakeGcp WithStalePreemptionRecord(string vmName, string zone, ulong instanceId, DateTimeOffset at)
    {
        _preemptedOperations.Enqueue((vmName, zone, instanceId, at));
        return this;
    }

    /// <summary>
    /// Issue #91: the next <paramref name="count"/> on-demand VMs created are lost at their first look (<c>TERMINATED</c>, never answering) and Compute records NO
    /// preemption for them, as for an admin delete or a zone outage.
    /// </summary>
    public FakeGcp WithOnDemandVmLosses(int count)
    {
        _onDemandLossesLeft = count;
        return this;
    }

    /// <summary>Issue #91: <see cref="WasPreemptedAsync"/> fails with a network error, as a failed zone-operations read would.</summary>
    public FakeGcp WithPreemptionLookupFailure()
    {
        _preemptionLookupFails = true;
        return this;
    }

    /// <summary>How many times <see cref="WasPreemptedAsync"/> was asked (issue #91).</summary>
    public int PreemptionLookups => Volatile.Read(ref _preemptionLookups);

    public Task<bool> WasPreemptedAsync(string vmName, string zone, VmIncarnation incarnation, CancellationToken cancellationToken)
    {
        ThrowIfScriptedTransient();
        var lookup = Interlocked.Increment(ref _preemptionLookups);
        if (_preemptionLookupFails)
        {
            throw Build(CloudErrorKind.Network, null, null, "Could not reach the server; the zone operations could not be read");
        }

        if (lookup < _preemptionRecordedAtLookup || (incarnation.InstanceId is null && incarnation.CreatedAt is null))
        {
            return Task.FromResult(false);
        }

        // The same rule as the real gateway: the instance id when known (form-free), else the name and an operation no older than the VM's creation.
        var found = _preemptedOperations.Any(op => incarnation.InstanceId is { } id
            ? op.Id == id
            : op.Name == vmName && op.Zone == zone && incarnation.CreatedAt is { } created && op.At >= created);
        return Task.FromResult(found);
    }

    public Task DeleteObjectAsync(string bucket, string objectKey, CancellationToken cancellationToken)
    {
        ThrowIfScriptedTransient();
        _objects.TryRemove((bucket, objectKey), out _);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Issue #91: the next <paramref name="count"/> Spot VMs created are taken back by Google before their worker writes anything: each reads
    /// <c>TERMINATED</c> (<c>preempted</c>) at its first look and its worker never answers. An on-demand VM is never preempted by this.
    /// </summary>
    public FakeGcp WithSpotPreemptions(int count)
    {
        _spotPreemptionsLeft = count;
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
        var gate = _createGate;
        if (_createDelay > TimeSpan.Zero)
        {
            return CreateAfterDelayAsync(spec, zone);
        }

        return gate is null ? CreateVmCore(spec, zone, cancellationToken) : CreateAfterGateAsync(gate, spec, zone, cancellationToken);
    }

    private async Task<VmDescriptor> CreateAfterDelayAsync(VmSpec spec, string zone)
    {
        var landing = Task.Delay(_createDelay, _timeProvider);
        OnCallParked?.Invoke(ParkedCall.SlowCreate, _createDelay);
        await landing.ConfigureAwait(false);
        if (CreateLandingGate is { } gate)
        {
            await gate.ConfigureAwait(false);
        }

        try
        {
            return await CreateVmCore(spec, zone, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _delayedCreateLanded.TrySetResult();
        }
    }

    private readonly TaskCompletionSource _delayedCreateLanded = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// When set, a create made slow by <see cref="WithCreateDelay"/> answers only once this task completes, after its delay: a test holds
    /// the landing until the caller has given up on the create, so "lands after the deadline" is a fact of ordering and not of a few
    /// virtual milliseconds (#567).
    /// </summary>
    public Task? CreateLandingGate { get; set; }

    /// <summary>Completes once a create made slow by <see cref="WithCreateDelay"/> has landed (or failed): the signal that a late landing has happened, so a test can look for what it left behind.</summary>
    public Task WhenDelayedCreateLandedAsync() => _delayedCreateLanded.Task;

    private async Task<VmDescriptor> CreateAfterGateAsync(CreateGate gate, VmSpec spec, string zone, CancellationToken cancellationToken)
    {
        gate.Entered.TrySetResult();
        OnCallParked?.Invoke(gate.IgnoreCancellation ? ParkedCall.CreateThatNeverLands : ParkedCall.Hang, Timeout.InfiniteTimeSpan);
        if (gate.IgnoreCancellation)
        {
            await gate.Release.Task.ConfigureAwait(false);
        }
        else
        {
            await gate.Release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        return await CreateVmCore(spec, zone, cancellationToken).ConfigureAwait(false);
    }

    private Task<VmDescriptor> CreateVmCore(VmSpec spec, string zone, CancellationToken cancellationToken)
    {
        ThrowIfScriptedTransient();
        // Hard Rule 10, enforced here exactly as a real gateway must: a spec
        // missing a label, a maxRunDuration, or carrying a value the real
        // API would reject anyway, never reaches "creation" - checked
        // before any scripted failure below, so this ordering is provable
        // even when nothing is scripted at all.
        spec.EnsurePreconditions();
        _createdSpecs.Enqueue(spec);
        Interlocked.Increment(ref _createAttempts);

        if (_createFailure is not null && _createFailureRemaining > 0)
        {
            _createFailureRemaining--;
            throw new CloudOperationException(_createFailure, CloudErrorClassifier.Classify(_createFailure));
        }

        var key = (spec.VmName, zone);

        if (_alreadyExistingVmKeys.TryGetValue(key, out var markedIncarnation))
        {
            if (!_vms.TryGetValue(key, out var marked) || IncarnationOf(marked) != markedIncarnation)
            {
                // The VM the mark was made for is gone: the name is free, as on Compute.
                _alreadyExistingVmKeys.TryRemove(new KeyValuePair<(string Name, string Zone), ulong>(key, markedIncarnation));
            }
            else
            {
                throw Build(
                    CloudErrorKind.AlreadyExists,
                    "ALREADY_EXISTS",
                    409,
                    $"The resource 'projects/{spec.ProjectId}/zones/{zone}/instances/{spec.VmName}' already exists");
            }
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

        // Issue #92: a queued (flex-start) request is the answer to a stockout, so it is not turned away by one; it waits and is granted or expires.
        if (spec.FlexStart && (_flexGrantAfter is not null || _flexExpireAfter is not null || _flexHold is not null))
        {
            return CreateQueuedAsync(spec, zone, key, cancellationToken);
        }

        if (Consume(_stockoutRemainingByZone, zone))
        {
            throw Build(
                CloudErrorKind.Stockout,
                "ZONE_RESOURCE_POOL_EXHAUSTED",
                null,
                $"The zone '{zone}' does not have enough resources available to fulfill the request for accelerator.");
        }

        return Task.FromResult(GrantCreate(spec, zone, key));
    }

    private bool _noServerTime;

    /// <summary>Issue #92 round 3: <see cref="GetVmAsync"/> answers carry no <see cref="VmDescriptor.ServerNow"/> (a proxy that strips the Date header), so a caller can only judge ages on its own clock.</summary>
    public FakeGcp WithoutServerTime()
    {
        _noServerTime = true;
        return this;
    }

    private TimeSpan? _flexGrantAfter;
    private TimeSpan? _flexExpireAfter;
    private readonly TaskCompletionSource _flexQueued = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Issue #92: a flex-start create is queued (the VM exists, PROVISIONING, from the first moment, as a pending request does) and granted after
    /// <paramref name="wait"/> of real time. A create that is not flex-start is untouched.
    /// </summary>
    public FakeGcp WithFlexStartGrantedAfter(TimeSpan wait)
    {
        _flexGrantAfter = wait;
        _flexExpireAfter = null;
        return this;
    }

    /// <summary>Issue #92: a flex-start create is queued and never granted: after <paramref name="wait"/> its request expires and the create fails as a stockout, leaving no VM.</summary>
    public FakeGcp WithFlexStartExpiringAfter(TimeSpan wait)
    {
        _flexExpireAfter = wait;
        _flexGrantAfter = null;
        return this;
    }

    /// <summary>Completes when the first flex-start create has been queued.</summary>
    public Task FlexStartQueued => _flexQueued.Task;

    private TaskCompletionSource<bool>? _flexHold;
    private bool _queuedHidden;
    private readonly ConcurrentDictionary<ulong, byte> _queuedKeys = new(); // by the queued request's incarnation (#918)

    /// <summary>Issue #92: a flex-start create is queued until <see cref="GrantFlexStart"/> (the test decides when Google finds a card). The request lives on the "server": a caller that gives up does not remove it.</summary>
    public FakeGcp WithFlexStartHeld()
    {
        _flexHold = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        return this;
    }

    /// <summary>Issue #92: grants the held flex-start request.</summary>
    public void GrantFlexStart() => _flexHold?.TrySetResult(true);

    /// <summary>Issue #92: <see cref="FindByJobIdAsync"/> does not list a request that is still queued (THEORY: a real pending request may not be listable), so only a delete by name can find it.</summary>
    public FakeGcp WithQueuedRequestsHiddenFromLabelListing()
    {
        _queuedHidden = true;
        return this;
    }

    private async Task<VmDescriptor> CreateQueuedAsync(VmSpec spec, string zone, (string, string) key, CancellationToken cancellationToken)
    {
        // The pending request is on the server from now on: a caller that gives up (cancels its wait) does not remove it, only a delete or an expiry does.
        var now = _timeProvider.GetUtcNow();
        // Check and insert are ONE step (#918): a request of the same VM that landed since the exists check wins, and this one is its replay.
        var request = new VmDescriptor(spec.VmName, zone, "PROVISIONING", null, now, spec.ToLabels(), null, spec.MachineType, now, InstanceId: (ulong)Interlocked.Increment(ref _nextInstanceId));
        if (!TryInsertVm(key, request))
        {
            return _vms.TryGetValue(key, out var winner) ? winner : throw Build(CloudErrorKind.Other, "VM_NOT_FOUND", 404, $"The VM '{spec.VmName}' is gone.");
        }

        _queuedKeys[IncarnationOf(request)] = 0;
        var server = QueuedOnServerAsync(spec, zone, key, request);
        _ = server.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        _flexQueued.TrySetResult();
        return await server.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<VmDescriptor> QueuedOnServerAsync(VmSpec spec, string zone, (string, string) key, VmDescriptor request)
    {
        if (_flexHold is { } hold)
        {
            await hold.Task.ConfigureAwait(false);
        }
        else
        {
            await Task.Delay(_flexGrantAfter ?? _flexExpireAfter!.Value).ConfigureAwait(false);
        }

        _queuedKeys.TryRemove(IncarnationOf(request), out _);
        // Only THIS request's row: a request made again under the name after this one was deleted is another incarnation and is not this task's to grant or expire (#918).
        if (!_vms.TryGetValue(key, out var queued) || queued.InstanceId != request.InstanceId)
        {
            // Deleted while queued: the request is gone.
            throw Build(CloudErrorKind.Other, "FLEX_START_REQUEST_CANCELLED", null, $"The request for '{spec.VmName}' was cancelled.");
        }

        if (_flexHold is null && _flexGrantAfter is null)
        {
            BeforeGrantWrite?.Invoke();
            RemoveIncarnation(key, queued.InstanceId);
            throw Build(CloudErrorKind.Stockout, "FLEX_START_REQUEST_EXPIRED", null, $"The request for '{spec.VmName}' waited for capacity in zone '{zone}' and was not granted in time.");
        }

        return GrantCreate(spec, zone, key, queued);
    }

    /// <summary>Grants a create. <paramref name="queued"/> is the pending request's row when this is a queued grant: it is replaced by the granted row in ONE step (#910), so a poll never sees the VM missing and a delete that lands first wins.</summary>
    private VmDescriptor GrantCreate(VmSpec spec, string zone, (string, string) key, VmDescriptor? queued = null)
    {
        var vm = new VmDescriptor(spec.VmName, zone, _bootPolls > 0 ? "PROVISIONING" : "RUNNING", null, _timeProvider.GetUtcNow(), spec.ToLabels(), null, spec.MachineType, _timeProvider.GetUtcNow(), InstanceId: (ulong)Interlocked.Increment(ref _nextInstanceId));
        // The simulated worker's files first, then the VM becomes visible (#784): a fast poll must never see RUNNING with no files yet.
        // A VM the simulation is about to preempt or lose (#91) never gets a worker.
        var willBeLost = spec.Spot ? _spotPreemptionsLeft > 0 : _onDemandLossesLeft > 0;
        Action? undoWorker = null;
        if (!willBeLost)
        {
            undoWorker = RunWorkerUndoably(spec, zone, vm.InstanceId);
        }

        // The VM's own state is written BEFORE its row lands (#918): keyed by a fresh incarnation nobody else holds, it cannot be seen by another VM, and a delete or poll that
        // meets the row finds it complete (a delete in the window forgets it, instead of it being written for a VM that is gone).
        var incarnation = IncarnationOf(vm);
        _labelFingerprints[incarnation] = NextFingerprint();
        _maxRunByVm[incarnation] = spec.MaxRunDuration;
        if (_bootPolls > 0)
        {
            _bootPollsLeft[incarnation] = _bootPolls;
        }

        try
        {
            BeforeGrantWrite?.Invoke();
            if (queued is null)
            {
                // Check and insert are ONE step (#918): another create of the same VM that landed since the exists check wins, and this one is its replay.
                if (!TryInsertVm(key, vm))
                {
                    ForgetIncarnation(vm);
                    undoWorker?.Invoke();
                    return _vms.TryGetValue(key, out var winner) ? winner : throw Build(CloudErrorKind.Other, "VM_NOT_FOUND", 404, $"The VM '{spec.VmName}' is gone.");
                }
            }
            else if (!ReferenceEquals(UpdateVm(key, v => v.InstanceId == queued.InstanceId ? vm : v), vm))
            {
                // Deleted (or made again by someone else) while the grant was being made: the request is gone, and so is the worker that was to serve it.
                ForgetIncarnation(vm);
                undoWorker?.Invoke();
                throw Build(CloudErrorKind.Other, "FLEX_START_REQUEST_CANCELLED", null, $"The request for '{spec.VmName}' was cancelled.");
            }
        }
        catch (Exception ex) when (ex is not CloudOperationException)
        {
            // A hook or a swap that threw between the worker's return and the row landing (#918): the worker never served a VM, so its files and deferred end go back.
            ForgetIncarnation(vm);
            undoWorker?.Invoke();
            throw;
        }

        AfterGrantInsert?.Invoke();
        ResetVmDeleted(spec.VmName);

        // Scheduled before this VM existed, or on the queued row it replaces: it is for this VM. A compare-and-swap, so a plan another actor re-scheduled meanwhile is kept.
        if (_preemptPlans.TryGetValue(key, out var scheduledBefore) && (scheduledBefore.InstanceId is null || (queued is not null && scheduledBefore.InstanceId == queued.InstanceId)))
        {
            _preemptPlans.TryUpdate(key, scheduledBefore with { InstanceId = vm.InstanceId }, scheduledBefore);
        }

        if (spec.Spot && _spotPreemptionsLeft > 0)
        {
            _spotPreemptionsLeft--;
            _preemptPlans[key] = new PreemptPlan(vm.InstanceId, _timeProvider.GetUtcNow(), true);
            return vm;
        }

        if (!spec.Spot && _onDemandLossesLeft > 0)
        {
            _onDemandLossesLeft--;
            _preemptPlans[key] = new PreemptPlan(vm.InstanceId, _timeProvider.GetUtcNow(), false);
            return vm;
        }

        return vm;
    }

    private readonly ConcurrentDictionary<string, string[]> _acceleratorZones = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The zones that offer <paramref name="acceleratorType"/> (issue #670): what <see cref="ListAcceleratorZonesAsync"/> answers, in this order.
    /// An accelerator nobody scripted answers an empty list ("not known"), so a test that names its zones in the request keeps its ladder.
    /// </summary>
    public FakeGcp WithAcceleratorZones(string acceleratorType, params string[] zones)
    {
        _acceleratorZones[acceleratorType] = zones;
        return this;
    }

    public Task<IReadOnlyList<string>> ListAcceleratorZonesAsync(string acceleratorType, CancellationToken cancellationToken)
    {
        ThrowIfScriptedTransient();
        IReadOnlyList<string> zones = _acceleratorZones.TryGetValue(acceleratorType, out var scripted) ? scripted : [];
        return Task.FromResult(zones);
    }

    /// <summary>How many times <see cref="ReuseVmAsync"/> was called (issue #94).</summary>
    public int ReuseAttempts => Volatile.Read(ref _reuseAttempts);

    private int _reuseAttempts;
    private CloudErrorKind? _reuseStartFailure;

    /// <summary>
    /// The start of a reused VM fails with this kind of error AFTER the VM was claimed (relabelled for the new job), as the real
    /// gateway's start can (a stockout, billing turned off). Issue #94.
    /// </summary>
    public FakeGcp WithReuseStartFailure(CloudErrorKind kind)
    {
        _reuseStartFailure = kind;
        return this;
    }

    private bool _reuseResponseLost;
    private Func<VmSpec, Task>? _reuseBarrier;

    /// <summary>
    /// The start of a reused VM SUCCEEDS but the answer is lost (a 503 on the final look, a dropped connection): the VM is running for the new
    /// job and the call throws a <see cref="CloudErrorKind.Network"/> error, once. The real gateway's last step can fail this way. Issue #94.
    /// </summary>
    public FakeGcp WithReuseResponseLost()
    {
        _reuseResponseLost = true;
        return this;
    }

    /// <summary>
    /// Runs <paramref name="barrier"/> inside <see cref="ReuseVmAsync"/> after the VM was read and before anything is written, so a test can make two
    /// installations' jobs read the same stopped VM and then race to claim it. Issue #94.
    /// </summary>
    public FakeGcp WithReuseBarrier(Func<VmSpec, Task> barrier)
    {
        _reuseBarrier = barrier;
        return this;
    }

    /// <summary>The <c>maxRunDuration</c> the VM was last given (create, or a reuse's scheduling write), or null when there is no such VM.</summary>
    public TimeSpan? MaxRunDurationOf(string vmName, string zone) => _vms.TryGetValue((vmName, zone), out var vm) && _maxRunByVm.TryGetValue(IncarnationOf(vm), out var limit) ? limit : null;

    /// <summary>Test seam (#918): the labelFingerprint of the VM now under this name (0 when there is none, or it has none).</summary>
    internal long LabelFingerprintOf(string vmName, string zone) => _vms.TryGetValue((vmName, zone), out var vm) ? _labelFingerprints.GetValueOrDefault(IncarnationOf(vm)) : 0;

    /// <summary>Test seam (#918): runs right after a create's row landed, before anything else about that create is written, so a test can poll or delete the VM in that window.</summary>
    internal Action? AfterGrantInsert { get; set; }

    /// <summary>Test seam (#918): how many per-incarnation state entries (fingerprint, max run, boot and stopping countdowns) this fake holds; zero once every VM is gone.</summary>
    internal int IncarnationStateCount => _labelFingerprints.Count + _maxRunByVm.Count + _bootPollsLeft.Count + _stoppingPollsLeft.Count;

    /// <summary>The key of a VM's per-incarnation state (#918). Every row this fake makes carries an InstanceId; 0 is only a fallback for a hand-built row.</summary>
    private static ulong IncarnationOf(VmDescriptor vm) => vm.InstanceId ?? 0;

    // The real labelFingerprint is fresh when an instance is created, moves on EVERY labels write (this app's claim, or anyone
    // else's), and a deleted-and-recreated instance never shares one with its predecessor. A global counter gives the same three
    // properties; an entry is dropped with its VM.
    private readonly ConcurrentDictionary<ulong, long> _labelFingerprints = new();
    private readonly object _claimLock = new();
    private long _fingerprintClock;

    private long NextFingerprint() => Interlocked.Increment(ref _fingerprintClock);

    private static Dictionary<string, string> MergeLabels(IReadOnlyDictionary<string, string>? current, IReadOnlyDictionary<string, string> add)
    {
        var merged = new Dictionary<string, string>(current ?? new Dictionary<string, string>());
        foreach (var (k, v) in add)
        {
            merged[k] = v;
        }

        return merged;
    }

    /// <summary>
    /// Someone else (the console, gcloud, another tool) rewrote this VM's labels: merges <paramref name="labels"/> into them and moves the
    /// labelFingerprint, as any real <c>setLabels</c> does, so a reuse that read the VM before it fails its claim with a 412. Issue #94 review.
    /// </summary>
    public FakeGcp SimulateExternalRelabel(string vmName, string zone, IReadOnlyDictionary<string, string> labels)
    {
        var key = (vmName, zone);
        lock (_claimLock)
        {
            if (_vms.TryGetValue(key, out var vm))
            {
                // The merge is computed from the labels as they are when the swap applies, never from the snapshot above.
                if (UpdateVm(key, row => row with { Labels = MergeLabels(row.Labels, labels) }) is { } relabelled)
                {
                    _labelFingerprints[IncarnationOf(relabelled)] = NextFingerprint();
                }
            }
        }

        return this;
    }

    /// <summary>
    /// Issue #94: claims and starts a stopped VM for <paramref name="spec"/>'s job, as the real gateway does: refuses a VM that is
    /// not stopped before any change, CLAIMS it first (rewrites the labels, which carry a fingerprint: of two jobs that read the same VM only
    /// one passes, the other gets a 412 and has written nothing), then rewrites the lifetime limit, then starts it (the VM then reads RUNNING;
    /// its <c>creationTimestamp</c> stays and its <c>lastStartTimestamp</c> moves; THEORY (unverified, ToTest #94): its <c>maxRunDuration</c> clock restarts with it) and lets the simulated
    /// worker act for the new job. Under the VM's own, old name.
    /// </summary>
    public async Task<VmDescriptor> ReuseVmAsync(VmSpec spec, string existingVmName, string zone, CancellationToken cancellationToken)
    {
        ThrowIfScriptedTransient();
        var reuse = spec with { ExistingVmName = existingVmName };
        reuse.EnsurePreconditions();
        Interlocked.Increment(ref _reuseAttempts);
        var key = (existingVmName, zone);
        if (!_vms.TryGetValue(key, out var vm))
        {
            throw Build(CloudErrorKind.Other, "VM_NOT_FOUND", 404, $"The VM '{existingVmName}' is gone.");
        }

        if (vm.Status is not ("STOPPED" or "TERMINATED"))
        {
            throw Build(CloudErrorKind.Other, "VM_NOT_STOPPED", null, $"The VM '{existingVmName}' is '{vm.Status}', not stopped.");
        }

        var readIncarnation = IncarnationOf(vm);
        var readFingerprint = _labelFingerprints.GetValueOrDefault(readIncarnation);
        if (_reuseBarrier is { } barrier)
        {
            await barrier(spec).ConfigureAwait(false);
        }

        // The claim comes before everything else, so a start that fails leaves the VM labelled for this job (the caller ends it).
        lock (_claimLock)
        {
            if (!_vms.TryGetValue(key, out vm))
            {
                throw Build(CloudErrorKind.Other, "VM_NOT_FOUND", 404, $"The VM '{existingVmName}' is gone.");
            }

            // A VM made again under the name is another incarnation with a fingerprint of its own: the claim on the one that was read is lost either way.
            if (IncarnationOf(vm) != readIncarnation || _labelFingerprints.GetValueOrDefault(IncarnationOf(vm)) != readFingerprint)
            {
                throw Build(CloudErrorKind.Other, "CONDITION_NOT_MET", 412, "The labels fingerprint is invalid: another job claimed this VM first.");
            }

            var claimed = vm;
            vm = UpdateVm(key, v => v.InstanceId == claimed.InstanceId ? v with { Labels = reuse.ToLabels() } : v);
            if (vm is null || vm.InstanceId != claimed.InstanceId)
            {
                throw Build(CloudErrorKind.Other, "VM_NOT_FOUND", 404, $"The VM '{existingVmName}' is gone.");
            }

            _labelFingerprints[IncarnationOf(vm)] = NextFingerprint();
            _maxRunByVm[IncarnationOf(vm)] = reuse.MaxRunDuration;
        }

        if (_reuseStartFailure is { } kind)
        {
            throw kind switch
            {
                CloudErrorKind.Stockout => Build(CloudErrorKind.Stockout, "ZONE_RESOURCE_POOL_EXHAUSTED", null, $"The zone '{zone}' does not have enough resources available to fulfill the request."),
                CloudErrorKind.Billing => Build(CloudErrorKind.Billing, "BILLING_DISABLED", 403, "This API method requires billing to be enabled."),
                _ => Build(kind, null, null, "The start failed."),
            };
        }

        ThrowIfProjectWide(spec.ProjectId);
        // The worker's files first, then the VM reads RUNNING (#784); bound to this incarnation. A worker that fails leaves the VM stopped (RunWorkerUndoably).
        var undoWorker = RunWorkerUndoably(reuse, zone, vm.InstanceId);
        VmDescriptor? started;
        try
        {
            BeforeStartWrite?.Invoke();
            // A delete that landed since the claim wins, as on real Compute (a start of a deleted instance is a 404); it is never resurrected as RUNNING (#875).
            started = UpdateVm(key, v => v.InstanceId == vm.InstanceId ? v with { Status = "RUNNING", StatusReason = null, StartedAt = _timeProvider.GetUtcNow(), StoppedAt = null } : v);
        }
        catch
        {
            // A hook or a swap that threw between the worker's return and the start landing (#918): the start never happened, so the worker's files and deferred end go back.
            undoWorker();
            throw;
        }

        if (started is null || started.InstanceId != vm.InstanceId)
        {
            undoWorker(); // the start never happened: its worker never existed
            throw Build(CloudErrorKind.Other, "VM_NOT_FOUND", 404, $"The VM '{existingVmName}' is gone.");
        }

        vm = started;
        if (_reuseResponseLost)
        {
            _reuseResponseLost = false;
            throw Build(CloudErrorKind.Network, null, 503, "The service is unavailable: the answer to the start was lost.");
        }

        return vm;
    }
    public Task<VmDescriptor?> GetVmAsync(string vmName, string zone, CancellationToken cancellationToken)
    {
        if (ConsumeHang())
        {
            return HangAsync<VmDescriptor?>(cancellationToken);
        }

        if (_getVmFailure is { } getVmFailure)
        {
            throw Build(getVmFailure, "SCRIPTED_GET_VM_FAILURE", null, "The VM read was scripted to fail.");
        }

        ThrowIfScriptedTransient();
        ExpireOverdueVms();
        if (_getVmFailuresRemaining > 0 && _getVmError is not null)
        {
            _getVmFailuresRemaining--;
            throw new CloudOperationException(_getVmError, CloudErrorClassifier.Classify(_getVmError));
        }

        var key = (vmName, zone);
        if (!_vms.TryGetValue(key, out var found))
        {
            return Task.FromResult<VmDescriptor?>(null);
        }

        VmDescriptor? vm = found;
        // The boot and stopping countdowns belong to the incarnation that was read (#918): the write is bound to it, so a VM made again under the name is never finished by the old one's poll.
        if (vm.Status == "PROVISIONING" && _bootPollsLeft.TryGetValue(IncarnationOf(vm), out var bootLeft))
        {
            var booting = vm;
            if (bootLeft <= 0)
            {
                _bootPollsLeft.TryRemove(IncarnationOf(booting), out _);
                BeforePollWrite?.Invoke();
                vm = UpdateVm(key, v => v.InstanceId == booting.InstanceId && v.Status == "PROVISIONING" ? v with { Status = "RUNNING" } : v);
            }
            else
            {
                _bootPollsLeft[IncarnationOf(booting)] = bootLeft - 1;
            }
        }

        if (vm is { Status: "STOPPING" } && _stoppingPollsLeft.TryGetValue(IncarnationOf(vm), out var left))
        {
            var stopping = vm;
            if (left <= 0)
            {
                _stoppingPollsLeft.TryRemove(IncarnationOf(stopping), out _);
                BeforePollWrite?.Invoke();
                vm = UpdateVm(key, v => v.InstanceId == stopping.InstanceId && v.Status == "STOPPING" ? v with { Status = _stoppedStatus } : v);
            }
            else
            {
                _stoppingPollsLeft[IncarnationOf(stopping)] = left - 1;
            }
        }

        if (vm is { Status: "RUNNING" } && _preemptPlans.TryGetValue(key, out var plan) && plan.AppliesTo(vm.InstanceId) && _timeProvider.GetUtcNow() >= plan.At)
        {
            BeforePollWrite?.Invoke();
            var polled = vm;
            vm = UpdateVm(key, v => v.Status == "RUNNING" && v.InstanceId == polled.InstanceId ? v with { Status = "TERMINATED", StatusReason = "preempted" } : v);
            if (vm is { Status: "TERMINATED", StatusReason: "preempted" } && vm.InstanceId == polled.InstanceId
                && _preemptPlans.TryRemove(new KeyValuePair<(string Name, string Zone), PreemptPlan>(key, plan)) && plan.Records)
            {
                _preemptedOperations.Enqueue((key.vmName, key.zone, vm.InstanceId, _timeProvider.GetUtcNow())); // Compute records the preemption only for a Spot VM it took back
            }
        }

        if (vm is null)
        {
            // Deleted while this poll was updating it: the preemption scheduled for that VM goes with it, and only that one (a VM made again under the name keeps its own).
            ClearPlan(key, found.InstanceId);
            return Task.FromResult<VmDescriptor?>(null);
        }
        return Task.FromResult<VmDescriptor?>(vm with { ServerNow = _noServerTime ? null : _timeProvider.GetUtcNow() });
    }

    private TaskCompletionSource? _endVmGate;

    /// <summary>
    /// Issue #113: every <see cref="EndVmAsync"/> from now on is "accepted now, done later": it reports acceptance at once, the VM is untouched, and the stop or
    /// delete is applied only when <see cref="ReleaseEndVmCompletion"/> is called (a caller that gives up first, by its token, leaves the VM as it is), like a real
    /// stop operation that takes minutes.
    /// </summary>
    public FakeGcp HoldEndVmCompletion()
    {
        _endVmGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        return this;
    }

    /// <summary>Lets every held <see cref="EndVmAsync"/> finish (see <see cref="HoldEndVmCompletion"/>) and stops holding later ones.</summary>
    public void ReleaseEndVmCompletion()
    {
        var gate = Interlocked.Exchange(ref _endVmGate, null);
        gate?.TrySetResult();
    }

    /// <summary>Issue #113: see <see cref="IComputeGateway.EndVmAsync"/>. Without a hold the acceptance is reported when the work is done, like the default.</summary>
    public async Task EndVmAsync(string vmName, string zone, bool delete, Action? onAccepted, CancellationToken cancellationToken)
    {
        var gate = _endVmGate;
        if (gate is not null)
        {
            ThrowIfScriptedTransient();
            onAccepted?.Invoke();
            await gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        if (delete)
        {
            await DeleteVmAsync(vmName, zone, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await StopVmAsync(vmName, zone, cancellationToken).ConfigureAwait(false);
        }

        if (gate is null)
        {
            onAccepted?.Invoke();
        }
    }

    public Task StopVmAsync(string vmName, string zone, CancellationToken cancellationToken)
    {
        ThrowIfScriptedTransient();
        StopCore(vmName, zone);
        return Task.CompletedTask;
    }

    // An internal stop (a worker stopping its own VM) says so on ITS call (#918): a shared flag let one in-flight internal stop excuse an unrelated outside stop.
    private void StopInternal(string vmName, string zone, ulong? onlyIncarnation = null)
        => StopCore(vmName, zone, onlyIncarnation, internalStop: true);

    /// <summary>Test seam (#875): runs between a reuse's claim (or a plain start's read) and its start, so a test can interleave a delete there deterministically.</summary>
    internal Action? BeforeStartWrite { get; set; }

    /// <summary>
    /// The ONLY way to change an existing VM row (#875): a compare-and-swap loop that applies <paramref name="change"/> to the row as it is NOW and gives up (null) when the row
    /// has vanished, so a concurrent delete is never overwritten and a deleted VM never comes back. A guard test forbids a bare <c>_vms[key] =</c> assignment anywhere else but a create.
    /// </summary>
    private VmDescriptor? UpdateVm((string Name, string Zone) key, Func<VmDescriptor, VmDescriptor> change)
    {
        while (_vms.TryGetValue(key, out var current))
        {
            var next = change(current);
            if (ReferenceEquals(next, current))
            {
                // Nothing to change: confirm the row still exists rather than report a row a delete may have just removed.
                return _vms.TryGetValue(key, out var still) ? still : null;
            }

            if (_vms.TryUpdate(key, next, current))
            {
                return next;
            }
        }

        return null;
    }

    /// <summary>The ONLY way to add a row a create made (#918): it lands only when no row holds the name, so the exists check and the insert are one atomic step. A guard test forbids every other write of a new row but a marked setup one.</summary>
    private bool TryInsertVm((string Name, string Zone) key, VmDescriptor row) => _vms.TryAdd(key, row);

    /// <summary>Test seam (#875): runs inside <see cref="GetVmAsync"/> right before a poll writes the row (boot, stopping or preempt), so a test can interleave a delete there deterministically.</summary>
    internal Action? BeforePollWrite { get; set; }

    /// <summary>Test seam (#910): runs inside a create's grant (or the expiry of a queued request) right before the granted row replaces the queued one (or the queued row is removed), so a test can poll or delete there deterministically.</summary>
    internal Action? BeforeGrantWrite { get; set; }

    /// <summary>Test seam (#875): runs right after a delete removed the row (or a poll found it gone) and before the scheduled preemption of that VM is cleaned up, so a test can make the name again exactly there.</summary>
    internal Action? BeforePlanCleanup { get; set; }

    /// <summary>How many preemptions Compute has recorded so far (#875: a preempt that lost a race to a delete must record none).</summary>
    internal int PreemptionRecordCount => _preemptedOperations.Count;

    /// <summary>How many deferred worker ends (a worker stopping or deleting its own VM when its result is read) are still pending.</summary>
    internal int PendingWorkerEndCount => _pendingWorkerEnds.Count;

    /// <summary>Test seam (#875): runs at the end of a worker's run (its files and deferred end are written), so a test can make the worker fail AFTER writing.</summary>
    internal Action? AfterWorkerWrite { get; set; }

    /// <summary>Test seam (#875): runs between a stop's read of the VM and its write, so a test can interleave a delete there deterministically.</summary>
    internal Action? BeforeStopWrite { get; set; }

    /// <summary>The stop does not touch a row that Compute already preempted (unless the stop read it that way), nor a row that finished stopping while this stop was waiting.</summary>
    private bool StopLeavesAlone(VmDescriptor now, VmDescriptor read, bool preemptedAtRead, ulong? onlyIncarnation)
        => (onlyIncarnation is not null && now.InstanceId != onlyIncarnation)
            || (now is { Status: "TERMINATED", StatusReason: "preempted" } && !preemptedAtRead)
            || (read.Status == "STOPPING" && now.Status == _stoppedStatus);

    private void StopCore(string vmName, string zone, ulong? onlyIncarnation = null, bool internalStop = false)
    {
        var key = (vmName, zone);
        if (!_vms.TryGetValue(key, out var vm) || (onlyIncarnation is not null && vm.InstanceId != onlyIncarnation))
        {
            return;
        }

        if (_stopRejectedUnlessRunning && vm.Status != "RUNNING" && !internalStop)
        {
            throw Build(CloudErrorKind.Other, "FAILED_PRECONDITION", 400, $"The instance '{vmName}' is not running, so it cannot be stopped.");
        }

        BeforeStopWrite?.Invoke();
        var preemptedAtRead = vm is { Status: "TERMINATED", StatusReason: "preempted" };
        // A delete that landed since the read wins (real Compute deletes the instance and the stop operation then fails), it is never resurrected as STOPPED.
        // A concurrent change of state does not cancel the stop (real Compute accepts a stop of a booting VM), except a preempt that Compute took back first, which is kept.
        if (_stoppingPolls > 0)
        {
            VmDescriptor? written = null;
            var stopping = UpdateVm(key, v => StopLeavesAlone(v, vm, preemptedAtRead, onlyIncarnation) ? v : written = v with { Status = "STOPPING", StatusReason = null, StoppedAt = _timeProvider.GetUtcNow() });
            // Only a row THIS stop moved to STOPPING starts a countdown; a STOPPING row the stop left alone (another incarnation's) keeps its own.
            if (stopping is { Status: "STOPPING" } && ReferenceEquals(stopping, written))
            {
                _stoppingPollsLeft[IncarnationOf(stopping)] = _stoppingPolls;
            }
        }
        else
        {
            UpdateVm(key, v => StopLeavesAlone(v, vm, preemptedAtRead, onlyIncarnation) ? v : v with { Status = _stoppedStatus, StatusReason = null, StoppedAt = _timeProvider.GetUtcNow() });
        }
    }

    public async Task DeleteVmAsync(string vmName, string zone, CancellationToken cancellationToken)
    {
        ThrowIfScriptedTransient();
        var delay = TimeSpan.FromTicks(Interlocked.Exchange(ref _firstDeleteDelayTicks, 0));
        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }

        ThrowIfDeleteScripted(vmName);
        if (_silentNoOpDeletes)
        {
            return;
        }

        var existed = _vms.TryRemove((vmName, zone), out var removed);
        if (existed)
        {
            // What belongs to the VM that was just removed (its disks, the signal that it is gone) is settled BEFORE the plan cleanup, whose gap a VM made again under the name can land in (#918):
            // that VM's disks stay attached and its own "deleted" signal is not completed by this delete (making it again resets a signal the old one already completed).
            DetachDisksOf(vmName);
            SignalVmDeleted(vmName);
            // A preemption scheduled for the deleted VM dies with it; a VM made again under the name must not inherit it, nor lose its own (#875 review).
            ClearPlan((vmName, zone), removed!.InstanceId);
        }

        if (existed)
        {
            ForgetIncarnation(removed!);
        }

        if (existed && _nextDeleteRacesAWorkerDelete)
        {
            _nextDeleteRacesAWorkerDelete = false;
            throw Build(CloudErrorKind.Other, "NOT_FOUND", 404, $"The resource 'projects/fake/zones/{zone}/instances/{vmName}' was not found");
        }

        if (!existed && _deleteOfMissingVmIsNotFound)
        {
            throw Build(CloudErrorKind.Other, "NOT_FOUND", 404, $"The resource 'projects/fake/zones/{zone}/instances/{vmName}' was not found");
        }
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
        if (ConsumeHang())
        {
            return HangAsync<IReadOnlyList<VmDescriptor>>(cancellationToken);
        }

        ThrowIfScriptedTransient();
        if (_notConnected)
        {
            // Nothing can be listed without a connection (issue #59: the reconciler must see this as "no answer", not as "no VM").
            throw Build(CloudErrorKind.Other, NotConnectedErrorCode, null, "No Google Cloud connection is built into this version.");
        }

        if (_findFailuresRemaining > 0 && _findError is not null)
        {
            _findFailuresRemaining--;
            throw new CloudOperationException(_findError, CloudErrorClassifier.Classify(_findError));
        }

        ExpireOverdueVms();
        var name = $"deg-{jobId}";
        // By the job-id label, as the real gateway does (a reused VM keeps its old name, #94); a VM seeded without labels, such as WithAlreadyExists's, by name.
        IReadOnlyList<VmDescriptor> matches = _vms.Values
            .Where(v => !(_queuedHidden && _queuedKeys.ContainsKey(IncarnationOf(v))))
            .Where(v => v.Labels is null ? v.Name == name : v.Labels.TryGetValue("job-id", out var owner) && owner == jobId)
            .ToList();
        return Task.FromResult(matches);
    }

    /// <summary>
    /// Every VM this fake holds whose labels carry the app label and <paramref name="installationId"/> (a VM seeded without labels, such
    /// as <see cref="WithAlreadyExists"/>'s, is never listed: like the real API, no label means no match). Same scripted-failure
    /// behaviour as <see cref="FindByJobIdAsync"/>.
    /// </summary>
    public Task<IReadOnlyList<VmDescriptor>> ListByInstallationAsync(string installationId, CancellationToken cancellationToken)
    {
        if (ConsumeHang())
        {
            return HangAsync<IReadOnlyList<VmDescriptor>>(cancellationToken);
        }

        ThrowIfScriptedTransient();
        if (_notConnected)
        {
            // Nothing can be listed without a connection (issue #59: the reconciler must see this as "no answer", not as "no VM").
            throw Build(CloudErrorKind.Other, NotConnectedErrorCode, null, "No Google Cloud connection is built into this version.");
        }

        if (_findFailuresRemaining > 0 && _findError is not null)
        {
            _findFailuresRemaining--;
            throw new CloudOperationException(_findError, CloudErrorClassifier.Classify(_findError));
        }

        ExpireOverdueVms();
        IReadOnlyList<VmDescriptor> matches = _vms.Values
            .Where(v => v.Labels is not null
                && v.Labels.TryGetValue("app", out var app) && app == VmSpec.AppLabelValue
                && v.Labels.TryGetValue("installation-id", out var owner) && owner == installationId)
            .Select(v => _maxRunByVm.TryGetValue(IncarnationOf(v), out var limit) ? v with { MaxRunDuration = limit } : v)
            .ToList();
        return Task.FromResult(matches);
    }

    /// <summary>Every VM this fake holds whose labels carry the app label, whichever installation it belongs to. Same scripted-failure behaviour as <see cref="ListByInstallationAsync"/>.</summary>
    public Task<IReadOnlyList<VmDescriptor>> ListAppVmsAsync(CancellationToken cancellationToken)
    {
        if (ConsumeHang())
        {
            return HangAsync<IReadOnlyList<VmDescriptor>>(cancellationToken);
        }

        ThrowIfScriptedTransient();
        if (_notConnected)
        {
            throw Build(CloudErrorKind.Other, NotConnectedErrorCode, null, "No Google Cloud connection is built into this version.");
        }

        ExpireOverdueVms();
        IReadOnlyList<VmDescriptor> matches = _vms.Values
            .Where(v => v.Labels is not null && v.Labels.TryGetValue("app", out var app) && app == VmSpec.AppLabelValue)
            .ToList();
        return Task.FromResult(matches);
    }

    // ----------------------------------------------------------------
    // IStorageGateway
    // ----------------------------------------------------------------

    /// <summary>The bucket <see cref="EnsureBucketAsync"/> hands out for <paramref name="projectId"/>.</summary>
    public static string BucketName(string projectId) => $"deg-{projectId}-fake";

    private int _ensureBucketCalls;

    /// <summary>How many times <see cref="EnsureBucketAsync"/> ran (it creates the bucket when missing, so a read-only look must leave this at zero).</summary>
    public int EnsureBucketCalls => Volatile.Read(ref _ensureBucketCalls);

    public Task<string> EnsureBucketAsync(string projectId, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _ensureBucketCalls);
        if (ConsumeHang())
        {
            return HangAsync<string>(cancellationToken);
        }

        ThrowIfScriptedTransient();
        _bucketProjects[projectId] = 0;
        return Task.FromResult(BucketName(projectId));
    }

    private readonly ConcurrentDictionary<string, byte> _bucketProjects = new();

    /// <summary>
    /// The bucket <see cref="EnsureBucketAsync"/> made for <paramref name="projectId"/>, labelled as the real one is (the fake does not
    /// track an installation, so it carries a fixed <c>installation-id</c>); none before the first ensure. Read-only, like the real one.
    /// </summary>
    public Task<IReadOnlyList<BucketDescriptor>> ListBucketsAsync(string projectId, CancellationToken cancellationToken)
    {
        if (ConsumeHang())
        {
            return HangAsync<IReadOnlyList<BucketDescriptor>>(cancellationToken);
        }

        ThrowIfScriptedTransient();
        IReadOnlyList<BucketDescriptor> buckets = _bucketProjects.ContainsKey(projectId)
            ? [new BucketDescriptor(BucketName(projectId), ResultsBucket.DefaultLocation, ResultsBucket.Labels("fake-installation", "fake"), null, _bucketSoftDeleteDays)]
            : [];
        return Task.FromResult(buckets);
    }

    /// <summary>
    /// Stores what is read from <paramref name="content"/>'s CURRENT position to its end, exactly what
    /// a replayed HTTP body would send, so a retry that forgot to rewind the stream stores a short object.
    /// </summary>
    public async Task UploadAsync(string bucket, string objectKey, Stream content, CancellationToken cancellationToken)
    {
        if (ConsumeHang())
        {
            await HangAsync<bool>(cancellationToken).ConfigureAwait(false);
        }

        ThrowIfScriptedTransient();
        ThrowIfUploadScripted(objectKey);
        using var copy = new MemoryStream();
        await content.CopyToAsync(copy, cancellationToken).ConfigureAwait(false);
        _objects[(bucket, objectKey)] = copy.ToArray();
        OnObjectStored(bucket, objectKey);
    }

    public Task<Stream> DownloadAsync(string bucket, string objectKey, CancellationToken cancellationToken)
    {
        ThrowIfScriptedTransient();
        if (!_objects.TryGetValue((bucket, objectKey), out var bytes))
        {
            throw Build(CloudErrorKind.Other, "NOT_FOUND", 404, $"No such object: {bucket}/{objectKey}");
        }

        return Task.FromResult<Stream>(new MemoryStream(bytes, writable: false));
    }

    /// <summary>Issue #588: same observable behaviour as <see cref="TryDownloadAsync"/> (scripted failures, hangs, live workers), written to a file; a failure or cancel leaves no file.</summary>
    public async Task<bool> TryDownloadToFileAsync(string bucket, string objectKey, string destinationPath, CancellationToken cancellationToken)
    {
        var source = await TryDownloadAsync(bucket, objectKey, cancellationToken).ConfigureAwait(false);
        if (source is null)
        {
            return false;
        }

        try
        {
            await using (source.ConfigureAwait(false))
            await using (var target = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                await source.CopyToAsync(target, 81920, cancellationToken).ConfigureAwait(false);
            }

            return true;
        }
        catch (Exception)
        {
            try
            {
                File.Delete(destinationPath);
            }
            catch (IOException)
            {
            }

            throw;
        }
    }

    public Task<Stream?> TryDownloadAsync(string bucket, string objectKey, CancellationToken cancellationToken)
    {
        if (ConsumeHang())
        {
            return HangAsync<Stream?>(cancellationToken);
        }

        ThrowIfScriptedTransient();
        if (_tryDownloadFailuresRemaining > 0 && _tryDownloadError is not null)
        {
            _tryDownloadFailuresRemaining--;
            throw new CloudOperationException(_tryDownloadError, CloudErrorClassifier.Classify(_tryDownloadError));
        }

        if (_liveWorkers.TryGetValue((bucket, objectKey), out var live))
        {
            TickLiveWorker(bucket, objectKey, live);
        }

        if (_pendingWorkerEnds.TryRemove((bucket, objectKey), out var workerEnds))
        {
            workerEnds();
        }

        return Task.FromResult<Stream?>(_objects.TryGetValue((bucket, objectKey), out var bytes) ? new MemoryStream(bytes, writable: false) : null);
    }

    /// <summary>What is in the fake bucket, for a test to assert on (the observable for "something was really uploaded").</summary>
    public IReadOnlyList<string> ObjectKeys(string bucket)
        => _objects.Keys.Where(k => k.Bucket == bucket).Select(k => k.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();

    public byte[] GetObjectBytes(string bucket, string objectKey)
        => _objects.TryGetValue((bucket, objectKey), out var bytes) ? bytes : throw new KeyNotFoundException($"No such object: {bucket}/{objectKey}");

    /// <summary>Puts an object in the bucket directly, as if another party (the worker, an earlier run) had written it.</summary>
    public void PutObject(string bucket, string objectKey, byte[] content) => _objects[(bucket, objectKey)] = content;

    /// <summary>How the simulated worker behaves for every VM created after this call. The default is <see cref="FakeWorkerMode.Done"/>.</summary>
    public FakeGcp WithWorker(FakeWorkerMode mode)
    {
        _workerMode = mode;
        return this;
    }

    /// <summary>The bytes the simulated worker uploads as the track of an input named <paramref name="inputName"/>.</summary>
    public static byte[] TrackBytes(string inputName) => Encoding.UTF8.GetBytes($"fake-track:{inputName}\n");

    /// <summary>
    /// Writes what the real worker writes, per <see cref="_workerMode"/>: outputs first, result.json
    /// LAST (docs/job_contract.md section 7). Does nothing when no manifest is in the bucket, since a
    /// worker with no manifest has nothing to run.
    /// </summary>
    private void RunSimulatedWorker(VmSpec spec, string zone, FakeWorkerMode? modeOverride = null, ulong? instanceId = null, List<Action>? journal = null)
    {
        // Every write the worker makes goes through here, so a caller that must take the worker back undoes exactly these entries (their prior values) and nothing another actor wrote.
        void Put<T>(ConcurrentDictionary<(string Bucket, string Key), T> map, (string Bucket, string Key) k, T value)
        {
            if (journal is null)
            {
                map[k] = value;
                return;
            }

            // The prior value is read and replaced in ONE compare-and-swap (#918), so the journal records exactly the value this write replaced; the undo puts it back only while
            // this write is still what is there, and never replaces a key another actor (a heartbeat, a cancel marker) wrote since.
            while (true)
            {
                if (map.TryGetValue(k, out var prior))
                {
                    if (map.TryUpdate(k, value, prior))
                    {
                        journal.Add(() => map.TryUpdate(k, prior, value));
                        return;
                    }
                }
                else if (map.TryAdd(k, value))
                {
                    journal.Add(() => map.TryRemove(new KeyValuePair<(string Bucket, string Key), T>(k, value)));
                    return;
                }
            }
        }

        var workerMode = modeOverride ?? _workerMode;
        if (workerMode is FakeWorkerMode.Never or FakeWorkerMode.NoHeartbeat)
        {
            return;
        }

        var bucket = BucketName(spec.ProjectId);
        var prefix = $"jobs/{spec.JobId}/";
        if (!_objects.TryGetValue((bucket, prefix + ResultsBucket.ManifestObject), out var manifestBytes))
        {
            return;
        }

        var vmKey = (spec.VmName, zone);
        // The deferred ends below act on the VM incarnation this worker ran on, never on a later VM that has the same name.
        var runsOn = instanceId ?? (_vms.TryGetValue(vmKey, out var current) ? current.InstanceId : null);
        var resultKey = (bucket, prefix + "result.json");

        if (workerMode is FakeWorkerMode.HeartbeatStopsAfterRunning or FakeWorkerMode.NoGpuFirstLine or FakeWorkerMode.HeartbeatingThenDone or FakeWorkerMode.StuckInModelLoading or FakeWorkerMode.StuckInRunning)
        {
            // Issue #498: a worker that is up (its status.json carries worker.version) but never reaches result.json by itself.
            var stuck = workerMode == FakeWorkerMode.StuckInModelLoading;
            var wedged = workerMode == FakeWorkerMode.StuckInRunning;
            Put(_objects, (bucket, prefix + "status.json"), Encoding.UTF8.GetBytes(WorkerStatusJson(spec.JobId, 1, stuck ? WorkerStages.ModelLoading : WorkerStages.Running, windowsDone: wedged ? 3 : null)));
            if (wedged)
            {
                // Issue #723: the heartbeat thread ticks on every look; the compute thread is wedged, so windowsDone and lastProgressAt never move.
                Put(_liveWorkers, (bucket, prefix + "status.json"), new LiveWorker(spec.JobId, 1, ReadsLeft: int.MaxValue, Finish: () => { }, WindowsDone: 3, Id: NextLiveWorkerId()));
            }

            if (stuck)
            {
                Put(_liveWorkers, (bucket, prefix + "status.json"), new LiveWorker(spec.JobId, 1, ReadsLeft: int.MaxValue, Finish: () => { }, Stage: WorkerStages.ModelLoading, Id: NextLiveWorkerId()));
            }

            Put(_objects, (bucket, prefix + "progress.jsonl"), Encoding.UTF8.GetBytes(_workerGpuLineMissing ? WorkerWarmupProgressLine() : WorkerFirstProgressLine(workerMode != FakeWorkerMode.NoGpuFirstLine)));
            if (workerMode == FakeWorkerMode.HeartbeatingThenDone)
            {
                Put(_liveWorkers, (bucket, prefix + "status.json"), new LiveWorker(spec.JobId, 1, ReadsLeft: 100, Finish: () => RunSimulatedWorker(spec, zone, FakeWorkerMode.Done, runsOn), Id: NextLiveWorkerId()));
            }

            return;
        }

        if (BootFailureCode(workerMode) is { } bootCode)
        {
            // worker/vm/startup.sh: write status.json with the error, never result.json, then end the VM
            // (a box whose GPU never came up is deleted; the other failures apply the lifecycle, stop here).
            var status = new JsonObject
            {
                ["schema"] = 1,
                ["jobId"] = spec.JobId,
                ["stage"] = WorkerStages.Failed,
                ["error"] = new JsonObject { ["code"] = bootCode, ["retriable"] = false },
            };
            Put(_objects, (bucket, prefix + "status.json"), Encoding.UTF8.GetBytes(status.ToJsonString()));
            Put(_pendingWorkerEnds, resultKey, bootCode == "GPU_NOT_VISIBLE"
                ? () => RemoveIncarnation(vmKey, runsOn)
                : () => StopIncarnation(vmKey, runsOn));
            return;
        }

        if (workerMode == FakeWorkerMode.GarbageResult)
        {
            Put(_objects, resultKey, Encoding.UTF8.GetBytes("this is not json"));
            return;
        }

        var names = new List<string>();
        using (var manifest = JsonDocument.Parse(manifestBytes))
        {
            foreach (var input in manifest.RootElement.GetProperty("inputs").EnumerateArray())
            {
                names.Add(input.GetProperty("name").GetString() ?? "input");
            }
        }

        var results = new JsonArray();
        for (var i = 0; i < names.Count; i++)
        {
            var name = names[i];
            var failed = workerMode == FakeWorkerMode.AllInputsFailed || (workerMode == FakeWorkerMode.SecondInputFailed && i > 0);
            if (workerMode == FakeWorkerMode.DoneWithNoFiles)
            {
                results.Add(new JsonObject { ["id"] = $"in{i + 1}", ["status"] = "done", ["outputs"] = new JsonArray(), ["files"] = new JsonArray(), ["notices"] = new JsonArray(), ["stats"] = null, ["error"] = null });
                continue;
            }

            var entry = new JsonObject { ["id"] = $"in{i + 1}", ["status"] = failed ? "failed" : "done", ["notices"] = new JsonArray(), ["stats"] = null };
            if (failed)
            {
                entry["outputs"] = new JsonArray();
                entry["files"] = new JsonArray();
                if (_partialFilesOnFailedInputs)
                {
                    var partialPath = $"output/{name}/{name}.partial.bedgraph";
                    var partialBytes = TrackBytes(name + "-partial");
                    Put(_objects, (bucket, prefix + partialPath), partialBytes);
                    entry["outputs"] = new JsonArray(partialPath);
                    entry["files"] = new JsonArray(new JsonObject { ["path"] = partialPath, ["sha256"] = Convert.ToHexString(SHA256.HashData(partialBytes)).ToLowerInvariant(), ["bytes"] = partialBytes.Length });
                }

                entry["error"] = new JsonObject { ["code"] = _workerFailureCode, ["message"] = "out of memory", ["retriable"] = true };
            }
            else
            {
                var path = workerMode == FakeWorkerMode.UnsafePath ? "output/../evil.txt" : $"output/{name}/{name}.bedgraph";
                var bytes = TrackBytes(name);
                Put(_objects, (bucket, prefix + path), bytes);
                var sha = workerMode == FakeWorkerMode.ChecksumMismatch
                    ? new string('0', 64)
                    : Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                entry["outputs"] = new JsonArray(path);
                entry["files"] = new JsonArray(new JsonObject { ["path"] = path, ["sha256"] = sha, ["bytes"] = bytes.Length });
                entry["error"] = null;
            }

            results.Add(entry);
        }

        var wholeFailure = workerMode == FakeWorkerMode.WholeJobFailed;
        var result = new JsonObject
        {
            ["schema"] = 1,
            ["jobId"] = spec.JobId,
            ["status"] = wholeFailure ? "failed" : "done",
            ["inputs"] = wholeFailure ? new JsonArray() : results,
            ["error"] = wholeFailure ? new JsonObject { ["code"] = _workerFailureCode, ["message"] = "out of memory", ["retriable"] = true } : null,
        };
        Put(_objects, resultKey, Encoding.UTF8.GetBytes(result.ToJsonString()));
        if (_workerSelfEnd == WorkerSelfEnd.Stop)
        {
            Put(_pendingWorkerEnds, resultKey, () => StopIncarnation(vmKey, runsOn));
        }
        else if (_workerSelfEnd == WorkerSelfEnd.Delete)
        {
            Put(_pendingWorkerEnds, resultKey, () => RemoveIncarnation(vmKey, runsOn));
        }

        AfterWorkerWrite?.Invoke();
    }

    /// <summary>
    /// Runs the simulated worker and returns what undoes it: everything it wrote for the job (objects, live worker, deferred end) goes back to how it was. A worker that
    /// throws is undone before the exception leaves, so a failed start never leaves a worker behind.
    /// </summary>
    private Action RunWorkerUndoably(VmSpec spec, string zone, ulong? instanceId)
    {
        // The worker records each key it writes with the value it replaced; undoing restores those keys one by one, newest first, and never touches anything else under the job prefix.
        var journal = new List<Action>();

        void Undo()
        {
            for (var i = journal.Count - 1; i >= 0; i--)
            {
                journal[i]();
            }

            journal.Clear();
        }

        try
        {
            RunSimulatedWorker(spec, zone, instanceId: instanceId, journal: journal);
        }
        catch
        {
            Undo();
            throw;
        }

        return Undo;
    }
    /// <summary>The per-incarnation state of a VM that is gone (#918): it is that incarnation's alone, so a VM made again under the name keeps its own.</summary>
    private void ForgetIncarnation(VmDescriptor gone)
    {
        var id = IncarnationOf(gone);
        _labelFingerprints.TryRemove(id, out _);
        _maxRunByVm.TryRemove(id, out _);
        _bootPollsLeft.TryRemove(id, out _);
        _stoppingPollsLeft.TryRemove(id, out _);
        _alreadyExistingVmKeys.TryRemove(new KeyValuePair<(string Name, string Zone), ulong>((gone.Name, gone.Zone), id));
    }

    /// <summary>A worker deletes its own VM: only the incarnation it ran on (when known), so a later VM made under the same name survives.</summary>
    private void RemoveIncarnation((string Name, string Zone) key, ulong? instanceId)
    {
        // The live row of that incarnation, however many CAS changes it went through since it was read (#875 review).
        while (_vms.TryGetValue(key, out var row) && (instanceId is null || row.InstanceId == instanceId))
        {
            if (_vms.TryRemove(new KeyValuePair<(string Name, string Zone), VmDescriptor>(key, row)))
            {
                ForgetIncarnation(row);
                return;
            }
        }
    }

    private void StopIncarnation((string Name, string Zone) key, ulong? instanceId)
    {
        if (_vms.TryGetValue(key, out var row) && (instanceId is null || row.InstanceId == instanceId))
        {
            StopInternal(key.Name, key.Zone, instanceId);
        }
    }

    private sealed record LiveWorker(string JobId, long Seq, int ReadsLeft, Action Finish, string Stage = WorkerStages.Running, bool CancelSeen = false, int? WindowsDone = null, long Id = 0);

    private long _liveWorkerIds;

    // Each worker is its own value (#918): two simulated workers of one job that differ in nothing but their delegates are otherwise EQUAL records, and an undo that compares by value
    // takes the other worker with it.
    private long NextLiveWorkerId() => Interlocked.Increment(ref _liveWorkerIds);

    private static string WorkerStatusJson(string jobId, long seq, string stage = WorkerStages.Running, bool cancelSeen = false, int? windowsDone = null)
    {
        var doc = new JsonObject
        {
            ["schema"] = 1,
            ["jobId"] = jobId,
            ["stage"] = stage,
            ["heartbeatSeq"] = seq,
            ["updatedAt"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture),
            ["vm"] = new JsonObject { ["gpu"] = "NVIDIA L4" },
            ["worker"] = new JsonObject { ["version"] = "1.0.0", ["image"] = "sha256:fake" },
            ["error"] = null,
            ["cancelSeen"] = cancelSeen,
        };
        if (windowsDone is { } done)
        {
            doc["windowsDone"] = done;
            doc["lastProgressAt"] = "2026-09-18T15:19:58Z"; // frozen on purpose: the wedged compute thread never advances it
        }

        return doc.ToJsonString();
    }

    private static string WorkerFirstProgressLine(bool gpu)
        => new JsonObject
        {
            ["seq"] = 1,
            ["stage"] = WorkerStages.RestoringCache,
            ["level"] = "notice",
            ["message"] = $"worker starting; GPU: {(gpu ? "NVIDIA L4 (driver 580.82.07)" : "no GPU detected")}; free disk 90 GB",
        }.ToJsonString() + "\n";

    /// <summary>One look at a live worker's status.json: it has moved on; after enough looks it finishes.</summary>
    private void TickLiveWorker(string bucket, string statusKey, LiveWorker live)
    {
        var next = live with { Seq = live.Seq + 1, ReadsLeft = live.ReadsLeft - 1 };
        _objects[(bucket, statusKey)] = Encoding.UTF8.GetBytes(WorkerStatusJson(live.JobId, next.Seq, live.Stage, live.CancelSeen, live.WindowsDone));
        if (next.ReadsLeft <= 0)
        {
            _liveWorkers.TryRemove((bucket, statusKey), out _);
            live.Finish();
        }
        else
        {
            _liveWorkers[(bucket, statusKey)] = next;
        }
    }

    private static string? BootFailureCode(FakeWorkerMode mode) => mode switch
    {
        FakeWorkerMode.GpuNotVisible => "GPU_NOT_VISIBLE",
        FakeWorkerMode.ImagePullFailed => "IMAGE_PULL_FAILED",
        FakeWorkerMode.ManifestInvalid => "MANIFEST_INVALID",
        FakeWorkerMode.WorkerCrash => "WORKER_CRASH",
        _ => null,
    };

    // ----------------------------------------------------------------
    // IProjectSetupGateway
    // ----------------------------------------------------------------

    public Task<ProjectLifecycleState> GetProjectStateAsync(string projectId, CancellationToken cancellationToken)
    {
        ThrowIfScriptedTransient();
        if (_notConnected)
        {
            throw Build(CloudErrorKind.Other, NotConnectedErrorCode, null, "No Google Cloud connection is built into this version.");
        }

        return Task.FromResult(_projectStates.TryGetValue(projectId, out var state) ? state : ProjectLifecycleState.Active);
    }

    public Task<bool> IsBillingEnabledAsync(string projectId, CancellationToken cancellationToken)
    {
        ThrowIfScriptedTransient();
        return Task.FromResult(!IsBillingOff(projectId));
    }

    public Task<bool> IsComputeApiEnabledAsync(string projectId, CancellationToken cancellationToken)
    {
        ThrowIfScriptedTransient();
        lock (_catalogGate)
        {
            return Task.FromResult(!_apiDisabledProjects.Contains(projectId));
        }
    }

    public Task EnableComputeApiAsync(string projectId, CancellationToken cancellationToken)
    {
        ThrowIfScriptedTransient();
        // Real `gcloud services enable` is idempotent; scripting it as an
        // immediate, unconditional clear matches that, minus the real
        // API's ~30-60s LRO delay (tracked as a known gap in issue #390).
        lock (_catalogGate)
        {
            _apiDisabledProjects.Remove(projectId);
        }

        return Task.CompletedTask;
    }

    // ----------------------------------------------------------------
    // IQuotaGateway
    // ----------------------------------------------------------------

    public Task<int> GetGpuQuotaAsync(string projectId, string region, string acceleratorType, CancellationToken cancellationToken)
    {
        ThrowIfScriptedTransient();
        var metric = MetricOf(acceleratorType);

        // The same table ReadQuotaAsync answers, so the two agree: a scripted read failure throws the classified error here (the real
        // gateway does the same, never a zero), and a region or accelerator nobody scripted is the always-succeeds default of 1.
        var table = BuildQuotaTable(extraRegion: region, extraMetric: metric);
        if (table.Failures.Count > 0)
        {
            throw new CloudOperationException(table.Failures[0].Error, table.Failures[0].Kind);
        }

        // CLAUDE.md: "GPUS_ALL_REGIONS=0 overrides a regional 1" - the project-wide cap is a ceiling under every region's own number.
        var remaining = table.GpuRemaining(region, metric) ?? DefaultRegionalQuota;
        return Task.FromResult((int)Math.Floor(remaining));
    }

    public Task<QuotaReadResult> ReadQuotaAsync(string projectId, CancellationToken cancellationToken)
    {
        ThrowIfScriptedTransient();
        return Task.FromResult(BuildQuotaTable(extraRegion: null, extraMetric: null));
    }

    private static readonly string[] DefaultQuotaRegions = ["us-central1", "us-east1", "us-west1", "europe-west4"];

    private static readonly string[] GpuQuotaMetricNames = [QuotaMetrics.NvidiaL4Gpus, QuotaMetrics.NvidiaA100Gpus, QuotaMetrics.NvidiaA100Gpus80Gb, QuotaMetrics.NvidiaH100Gpus];

    private static string MetricOf(string acceleratorType) => QuotaMetrics.ForAccelerator(acceleratorType) ?? acceleratorType.ToUpperInvariant();

    /// <summary>
    /// The one quota table both quota methods answer from. Every GPU metric of every scripted region and of the default regions
    /// (<see cref="DefaultQuotaRegions"/>) is <see cref="DefaultRegionalQuota"/> unless <see cref="WithGpuQuota"/> says otherwise, so a
    /// region nobody scripted reads the same through either method. A <see cref="WithQuotaReadFailing"/> read is a failed read: UNKNOWN, never zero.
    /// </summary>
    private QuotaReadResult BuildQuotaTable(string? extraRegion, string? extraMetric)
    {
        if (_quotaReadFailure is { } kind)
        {
            return QuotaReadResult.Failed(new QuotaReadFailure("regions.list", new CloudError("QUOTA_READ_FAILED", null, "The quota could not be read."), kind));
        }

        var scripted = new Dictionary<(string Region, string Metric), int>();
        var used = new Dictionary<(string Region, string Metric), int>();
        foreach (var ((region, accelerator), available) in _regionalQuota)
        {
            scripted[(region, MetricOf(accelerator))] = available;
        }

        foreach (var ((region, accelerator), inUse) in _regionalUsed)
        {
            used[(region, MetricOf(accelerator))] = inUse;
        }

        var regionNames = new HashSet<string>(DefaultQuotaRegions, StringComparer.OrdinalIgnoreCase);
        regionNames.UnionWith(_regionalQuota.Keys.Select(k => k.Region));
        if (extraRegion is not null)
        {
            regionNames.Add(extraRegion.ToLowerInvariant());
        }

        var table = new Dictionary<string, IReadOnlyList<QuotaMetric>>(StringComparer.OrdinalIgnoreCase);
        foreach (var region in regionNames)
        {
            var metricNames = new HashSet<string>(GpuQuotaMetricNames, StringComparer.Ordinal);
            metricNames.UnionWith(scripted.Keys.Where(k => string.Equals(k.Region, region, StringComparison.OrdinalIgnoreCase)).Select(k => k.Metric));
            if (extraMetric is not null)
            {
                metricNames.Add(extraMetric);
            }

            table[region] = metricNames
                .Select(m => new QuotaMetric(m, scripted.TryGetValue((region.ToLowerInvariant(), m), out var configured) ? configured : DefaultRegionalQuota, used.GetValueOrDefault((region.ToLowerInvariant(), m))))
                .ToList();
        }

        // No cap configured is a project that never hits one: a ceiling far above any regional number.
        return new QuotaReadResult(table, new QuotaMetric(QuotaMetrics.GpusAllRegions, _allRegionsGpuCap ?? 1000, 0), []);
    }

    // ----------------------------------------------------------------

    private void ThrowIfProjectWide(string projectId)
    {
        // Order matches docs/cloud_design.md section 2's preflight order
        // (billing before API before permission/org policy) so a project
        // scripted with more than one project-wide failure still reports
        // the one a real preflight run would surface first.
        if (IsBillingOff(projectId))
        {
            throw Build(CloudErrorKind.Billing, "BILLING_DISABLED", 403, $"The billing account for the owning project '{projectId}' is disabled.");
        }

        bool apiDisabled;
        lock (_catalogGate)
        {
            apiDisabled = _apiDisabledProjects.Contains(projectId);
        }

        if (apiDisabled)
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
        // Parallel ladder attempts (#670) consume from the same dictionary.
        lock (remainingByKey)
        {
            if (!remainingByKey.TryGetValue(key, out var remaining) || remaining <= 0)
            {
                return false;
            }

            remainingByKey[key] = remaining - 1;
            return true;
        }
    }

    private static CloudOperationException Build(CloudErrorKind kind, string? code, int? httpStatus, string message)
        => new(new CloudError(code, httpStatus, message), kind);
}
