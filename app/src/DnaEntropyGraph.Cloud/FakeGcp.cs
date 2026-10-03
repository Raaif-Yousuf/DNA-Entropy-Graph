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
    private readonly HashSet<(string Name, string Zone)> _alreadyExistingVmKeys = new();
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
    private readonly ConcurrentDictionary<(string Name, string Zone), int> _stoppingPollsLeft = new();
    private int _hangsRemaining;
    private bool _internalStop;
    private bool _hangsIgnoreCancellation;
    private int _getVmFailuresRemaining;
    private CloudError? _getVmError;
    private int _bootPolls;
    private readonly ConcurrentDictionary<(string Name, string Zone), int> _bootPollsLeft = new();
    private bool _enforceMaxRunDuration;
    private readonly ConcurrentDictionary<(string Name, string Zone), TimeSpan> _maxRunByVm = new();
    private bool _partialFilesOnFailedInputs;
    private TimeSpan _createDelay = TimeSpan.Zero;
    private bool _stopRejectedUnlessRunning;
    private int _findFailuresRemaining;
    private CloudError? _findError;
    private int _tryDownloadFailuresRemaining;
    private CloudError? _tryDownloadError;
    private string _workerFailureCode = "MODEL_OOM";
    private bool _notConnected;

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
    /// The platform deletes a VM once its <c>maxRunDuration</c> has passed since it was created (Hard Rule 10's
    /// <c>instanceTerminationAction=DELETE</c>); off by default. A VM past its limit is gone at the next look,
    /// by <see cref="GetVmAsync"/> or <see cref="FindByJobIdAsync"/>, measured on this fake's <see cref="TimeProvider"/>.
    /// </summary>
    public FakeGcp WithMaxRunDurationEnforced()
    {
        _enforceMaxRunDuration = true;
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
            if (vm.CreatedAt is { } created && _maxRunByVm.TryGetValue(key, out var limit) && now >= created + limit)
            {
                _vms.TryRemove(key, out _);
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

    /// <summary>The error code <see cref="WithCloudNotConnected"/> throws under; the runner maps it to <c>cloud_not_connected</c>.</summary>
    public const string NotConnectedErrorCode = RunErrorCodes.NotConnectedGatewayCode;

    private int _hungCalls;

    /// <summary>How many calls have been parked by <see cref="WithHungCalls"/> so far: a test waits on this to know a caller is really inside the call.</summary>
    public int HungCalls => Volatile.Read(ref _hungCalls);

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
                Interlocked.Increment(ref _hungCalls);
                return true;
            }
        }
    }

    private async Task<T> HangAsync<T>(CancellationToken cancellationToken)
    {
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
        var gate = _createGate;
        if (_createDelay > TimeSpan.Zero)
        {
            return CreateAfterDelayAsync(spec, zone);
        }

        return gate is null ? CreateVmCore(spec, zone, cancellationToken) : CreateAfterGateAsync(gate, spec, zone, cancellationToken);
    }

    private async Task<VmDescriptor> CreateAfterDelayAsync(VmSpec spec, string zone)
    {
        await Task.Delay(_createDelay).ConfigureAwait(false);
        return await CreateVmCore(spec, zone, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task<VmDescriptor> CreateAfterGateAsync(CreateGate gate, VmSpec spec, string zone, CancellationToken cancellationToken)
    {
        gate.Entered.TrySetResult();
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

        var vm = new VmDescriptor(spec.VmName, zone, _bootPolls > 0 ? "PROVISIONING" : "RUNNING", null, _timeProvider.GetUtcNow());
        _vms[key] = vm;
        _maxRunByVm[key] = spec.MaxRunDuration;
        if (_bootPolls > 0)
        {
            _bootPollsLeft[key] = _bootPolls;
        }

        RunSimulatedWorker(spec, zone);
        return Task.FromResult(vm);
    }

    public Task<VmDescriptor?> GetVmAsync(string vmName, string zone, CancellationToken cancellationToken)
    {
        if (ConsumeHang())
        {
            return HangAsync<VmDescriptor?>(cancellationToken);
        }

        ThrowIfScriptedTransient();
        ExpireOverdueVms();
        if (_getVmFailuresRemaining > 0 && _getVmError is not null)
        {
            _getVmFailuresRemaining--;
            throw new CloudOperationException(_getVmError, CloudErrorClassifier.Classify(_getVmError));
        }

        var key = (vmName, zone);
        if (!_vms.TryGetValue(key, out var vm))
        {
            return Task.FromResult<VmDescriptor?>(null);
        }

        if (vm.Status == "PROVISIONING" && _bootPollsLeft.TryGetValue(key, out var bootLeft))
        {
            if (bootLeft <= 0)
            {
                _bootPollsLeft.TryRemove(key, out _);
                vm = vm with { Status = "RUNNING" };
                _vms[key] = vm;
            }
            else
            {
                _bootPollsLeft[key] = bootLeft - 1;
            }
        }

        if (vm.Status == "STOPPING" && _stoppingPollsLeft.TryGetValue(key, out var left))
        {
            if (left <= 0)
            {
                _stoppingPollsLeft.TryRemove(key, out _);
                vm = vm with { Status = _stoppedStatus };
                _vms[key] = vm;
            }
            else
            {
                _stoppingPollsLeft[key] = left - 1;
            }
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
        ThrowIfScriptedTransient();
        StopCore(vmName, zone);
        return Task.CompletedTask;
    }

    private void StopInternal(string vmName, string zone)
    {
        _internalStop = true;
        try
        {
            StopCore(vmName, zone);
        }
        finally
        {
            _internalStop = false;
        }
    }

    private void StopCore(string vmName, string zone)
    {
        var key = (vmName, zone);
        if (!_vms.TryGetValue(key, out var vm))
        {
            return;
        }

        if (_stopRejectedUnlessRunning && vm.Status != "RUNNING" && !_internalStop)
        {
            throw Build(CloudErrorKind.Other, "FAILED_PRECONDITION", 400, $"The instance '{vmName}' is not running, so it cannot be stopped.");
        }

        if (_stoppingPolls > 0)
        {
            _vms[key] = vm with { Status = "STOPPING", StatusReason = null };
            _stoppingPollsLeft[key] = _stoppingPolls;
        }
        else
        {
            _vms[key] = vm with { Status = _stoppedStatus, StatusReason = null };
        }
    }

    public Task DeleteVmAsync(string vmName, string zone, CancellationToken cancellationToken)
    {
        ThrowIfScriptedTransient();
        var existed = _vms.TryRemove((vmName, zone), out _);
        if (existed && _nextDeleteRacesAWorkerDelete)
        {
            _nextDeleteRacesAWorkerDelete = false;
            throw Build(CloudErrorKind.Other, "NOT_FOUND", 404, $"The resource 'projects/fake/zones/{zone}/instances/{vmName}' was not found");
        }

        if (!existed && _deleteOfMissingVmIsNotFound)
        {
            throw Build(CloudErrorKind.Other, "NOT_FOUND", 404, $"The resource 'projects/fake/zones/{zone}/instances/{vmName}' was not found");
        }

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
        IReadOnlyList<VmDescriptor> matches = _vms.Values.Where(v => v.Name == name).ToList();
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
        return Task.FromResult(BucketName(projectId));
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
        using var copy = new MemoryStream();
        await content.CopyToAsync(copy, cancellationToken).ConfigureAwait(false);
        _objects[(bucket, objectKey)] = copy.ToArray();
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
    private void RunSimulatedWorker(VmSpec spec, string zone, FakeWorkerMode? modeOverride = null)
    {
        var workerMode = modeOverride ?? _workerMode;
        if (workerMode is FakeWorkerMode.Never or FakeWorkerMode.NoHeartbeat)
        {
            return;
        }

        var bucket = BucketName(spec.ProjectId);
        var prefix = $"jobs/{spec.JobId}/";
        if (!_objects.TryGetValue((bucket, prefix + "manifest.json"), out var manifestBytes))
        {
            return;
        }

        var vmKey = (spec.VmName, zone);
        var resultKey = (bucket, prefix + "result.json");

        if (workerMode is FakeWorkerMode.HeartbeatStopsAfterRunning or FakeWorkerMode.NoGpuFirstLine or FakeWorkerMode.HeartbeatingThenDone)
        {
            // Issue #498: a worker that is up (its status.json carries worker.version) but never reaches result.json by itself.
            _objects[(bucket, prefix + "status.json")] = Encoding.UTF8.GetBytes(WorkerStatusJson(spec.JobId, 1));
            _objects[(bucket, prefix + "progress.jsonl")] = Encoding.UTF8.GetBytes(WorkerFirstProgressLine(workerMode != FakeWorkerMode.NoGpuFirstLine));
            if (workerMode == FakeWorkerMode.HeartbeatingThenDone)
            {
                _liveWorkers[(bucket, prefix + "status.json")] = new LiveWorker(spec.JobId, 1, ReadsLeft: 100, Finish: () => RunSimulatedWorker(spec, zone, FakeWorkerMode.Done));
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
                ["stage"] = "failed",
                ["error"] = new JsonObject { ["code"] = bootCode, ["retriable"] = false },
            };
            _objects[(bucket, prefix + "status.json")] = Encoding.UTF8.GetBytes(status.ToJsonString());
            _pendingWorkerEnds[resultKey] = bootCode == "GPU_NOT_VISIBLE"
                ? () => _vms.TryRemove(vmKey, out _)
                : () => StopInternal(vmKey.Item1, vmKey.Item2);
            return;
        }

        if (workerMode == FakeWorkerMode.GarbageResult)
        {
            _objects[resultKey] = Encoding.UTF8.GetBytes("this is not json");
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
                    _objects[(bucket, prefix + partialPath)] = partialBytes;
                    entry["outputs"] = new JsonArray(partialPath);
                    entry["files"] = new JsonArray(new JsonObject { ["path"] = partialPath, ["sha256"] = Convert.ToHexString(SHA256.HashData(partialBytes)).ToLowerInvariant(), ["bytes"] = partialBytes.Length });
                }

                entry["error"] = new JsonObject { ["code"] = _workerFailureCode, ["message"] = "out of memory", ["retriable"] = true };
            }
            else
            {
                var path = workerMode == FakeWorkerMode.UnsafePath ? "output/../evil.txt" : $"output/{name}/{name}.bedgraph";
                var bytes = TrackBytes(name);
                _objects[(bucket, prefix + path)] = bytes;
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
        _objects[resultKey] = Encoding.UTF8.GetBytes(result.ToJsonString());
        if (_workerSelfEnd == WorkerSelfEnd.Stop)
        {
            _pendingWorkerEnds[resultKey] = () => StopInternal(vmKey.Item1, vmKey.Item2);
        }
        else if (_workerSelfEnd == WorkerSelfEnd.Delete)
        {
            _pendingWorkerEnds[resultKey] = () => _vms.TryRemove(vmKey, out _);
        }
    }

    private sealed record LiveWorker(string JobId, long Seq, int ReadsLeft, Action Finish);

    private static string WorkerStatusJson(string jobId, long seq)
        => new JsonObject
        {
            ["schema"] = 1,
            ["jobId"] = jobId,
            ["stage"] = "running",
            ["heartbeatSeq"] = seq,
            ["updatedAt"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture),
            ["vm"] = new JsonObject { ["gpu"] = "NVIDIA L4" },
            ["worker"] = new JsonObject { ["version"] = "1.0.0", ["image"] = "sha256:fake" },
            ["error"] = null,
        }.ToJsonString();

    private static string WorkerFirstProgressLine(bool gpu)
        => new JsonObject
        {
            ["seq"] = 1,
            ["stage"] = "restoring-cache",
            ["level"] = "notice",
            ["message"] = $"worker starting; GPU: {(gpu ? "NVIDIA L4 (driver 580.82.07)" : "no GPU detected")}; free disk 90 GB",
        }.ToJsonString() + "\n";

    /// <summary>One look at a live worker's status.json: it has moved on; after enough looks it finishes.</summary>
    private void TickLiveWorker(string bucket, string statusKey, LiveWorker live)
    {
        var next = live with { Seq = live.Seq + 1, ReadsLeft = live.ReadsLeft - 1 };
        _objects[(bucket, statusKey)] = Encoding.UTF8.GetBytes(WorkerStatusJson(live.JobId, next.Seq));
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
        return Task.FromResult(!_billingOffProjects.Contains(projectId));
    }

    public Task<bool> IsComputeApiEnabledAsync(string projectId, CancellationToken cancellationToken)
    {
        ThrowIfScriptedTransient();
        return Task.FromResult(!_apiDisabledProjects.Contains(projectId));
    }

    public Task EnableComputeApiAsync(string projectId, CancellationToken cancellationToken)
    {
        ThrowIfScriptedTransient();
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
        ThrowIfScriptedTransient();
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
