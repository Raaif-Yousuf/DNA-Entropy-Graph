using System.Collections.Concurrent;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Contract;
using DnaEntropyGraph.Core.Inputs;
using DnaEntropyGraph.Core.Runs;

namespace DnaEntropyGraph.Core.Cloud;

/// <summary>What the reconciler did with one run row.</summary>
public enum ReattachAction
{
    /// <summary>The run was handed back to <see cref="CloudJobRunner"/>, which carried it to the phase in <see cref="ReattachOutcome.FinalPhase"/>; a non-terminal phase there means the app's shutdown cut it short and the next launch resumes it (DECISION #599).</summary>
    Resumed,

    /// <summary>The run was mid-cancel; the cancel was finished.</summary>
    CancelFinished,

    /// <summary>A cancel of this run was in flight and the reattach's part was cut short (the wait for a user cancel timed out, the app began to shut down, the cancel failed, or its terminal write did not land): the row is not terminal (normally Cancelling; still Running when the cancel was cut short before it recorded Cancelling, which is #610) and the next launch looks again. A snapshot, not a final answer: a cancel's delete cannot be cancelled once started, so it may still complete after this is reported. Never reported with a terminal row (DECISION #599).</summary>
    CancelInterrupted,

    /// <summary>A cancel of this run ended and could not confirm its VM gone: the row is Failed with <see cref="RunErrorCodes.CancelFailed"/> and the user is told to delete the VM by hand (DECISION #599).</summary>
    CancelFailed,

    /// <summary>The VM is gone and no <c>result.json</c> exists: recorded Failed. A lost VM is never silently replaced.</summary>
    FailedVmMissing,

    /// <summary>The request could not be rebuilt from the row (no project, unreadable options, no input copy, no worker image): recorded Failed with the code that names the action.</summary>
    FailedUnrecoverable,

    /// <summary>The cloud could not be asked (no connection, offline). The row is untouched and the next launch looks again.</summary>
    Deferred,

    /// <summary>An error that is not a missing connection (a refusal, an unreadable row, a failing repository) stopped the look. The row is untouched, the error class is logged and in <see cref="ReattachOutcome.ErrorCode"/>, and the next launch looks again. It does not keep the reconnect probe alive (issue #559).</summary>
    Errored,

    /// <summary>Not a cloud run (a local-engine run has no cloud state to reattach).</summary>
    Skipped,

    /// <summary>Issue #830: the Google account this run was made under is signed out, so nothing could be asked about it. The row is untouched (nothing about the run was decided) and <see cref="ReattachOutcome.AccountSub"/> names the account to sign in as. Never reported as an unexpected error, and it does not keep the reconnect probe alive.</summary>
    NeedsSignIn,
}

/// <summary>One row's outcome. <paramref name="FinalPhase"/> and <paramref name="ErrorCode"/> are what the row ended as, when it ended.</summary>
public sealed record ReattachOutcome(string JobId, ReattachAction Action, JobPhase? FinalPhase = null, string? ErrorCode = null, string? AccountSub = null);

/// <summary>What the lifecycle enforcement did with one VM (issue #530).</summary>
public enum LifecycleAction
{
    /// <summary>A finished run's VM existed in a state its lifecycle label forbids (delete, or keep-alive past its expiry with afterKeepAlive delete): deleted.</summary>
    VmDeleted,

    /// <summary>A finished run's VM was running when its lifecycle label says it should be stopped (stop, or keep-alive past its expiry with afterKeepAlive stop): stopped.</summary>
    VmStopped,

    /// <summary>A stopped VM of this installation sat idle longer than <see cref="CloudHousekeepingSettings.IdleStoppedVmHoursKey"/>: deleted.</summary>
    IdleVmDeleted,

    /// <summary>The cloud could not be asked (no connection, offline, a deadline). Nothing was changed; the next launch or reconnect looks again.</summary>
    Deferred,

    /// <summary>The cloud refused the stop or delete (permission, org policy, billing). Nothing was changed; <see cref="LifecycleOutcome.ErrorCode"/> names why.</summary>
    Failed,

    /// <summary>Issue #830: the Google account this run's computer belongs to is signed out. Nothing was changed; <see cref="LifecycleOutcome.AccountSub"/> names the account to sign in as, and the pass went on with every other account's rows.</summary>
    NeedsSignIn,
}

/// <summary>One VM the lifecycle enforcement acted on, or could not. A VM it looked at and left alone has no outcome.</summary>
public sealed record LifecycleOutcome(string JobId, string VmName, LifecycleAction Action, string? ErrorCode = null, string? AccountSub = null);

/// <summary>
/// Issue #59 (docs/architecture.md section 6, "Crash-safe resumption"): on launch, every run a killed or closed app left in a
/// non-terminal phase is reattached. This class only DECIDES; the walking is the runner's. A run is handed back to
/// <see cref="CloudJobRunner.RunAsync"/>, which already resumes from the phase recorded in <see cref="IRunRepository"/>, so there
/// is exactly one resume path.
///
/// The decision table (VM state is read by job-id label, Hard Rule 9; the prototype's STOPPED means "start it" does not carry
/// over, because a stopped VM with no result is a dead job, not an idle one):
/// <list type="table">
/// <item><term>Cancelling</term><description>finish the cancel.</description></item>
/// <item><term>Draft, Validating, Uploading</term><description>no VM can exist yet: restart from the app's own copy of the input.</description></item>
/// <item><term>Provisioning, no VM, no result</term><description>the create may never have been sent: provision now.</description></item>
/// <item><term>Preparing to Downloading, VM found (any state)</term><description>the runner reads <c>result.json</c> first (done: download), else judges the VM: up and booting keeps polling, stopped or terminated without a result fails and is ended.</description></item>
/// <item><term>Preparing to Downloading, <c>result.json</c> present, no VM</term><description>download (the worker ended its own VM).</description></item>
/// <item><term>Preparing to Downloading, no VM, no result</term><description><see cref="ReattachAction.FailedVmMissing"/>.</description></item>
/// </list>
/// Only rows created before this reconciler was built are considered: a run the user starts after launch is owned by the engine.
///
/// Issue #530 adds <see cref="EnforceLifecycleAsync"/> (a finished run's VM in a state its lifecycle label forbids; keep-alive past its
/// expiry; this installation's stopped VMs idle longer than <see cref="CloudHousekeepingSettings.IdleStoppedVmHoursKey"/>) and
/// <see cref="ReconcileAsync"/>, the one entry that does both and that <see cref="ReconcileOnReconnect"/> runs again when the connection returns.
/// Every job a pass touches is registered in <see cref="ActiveRuns"/> first, so two passes (launch and a reconnect), or a pass and the engine,
/// never write the same job at once.
/// </summary>
public sealed class JobReconciler
{
    /// <summary>The longest one look at the cloud (find the VM, read result.json) may take before the run is deferred to the next launch.</summary>
    private static readonly TimeSpan DefaultLookupTimeout = TimeSpan.FromSeconds(30);

    /// <summary>The longest one delete or stop may take. Compute delete and stop are operations that run for tens of seconds, so this is far longer than a lookup; a refusal still comes back at once.</summary>
    private static readonly TimeSpan DefaultMutationTimeout = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Added to the mutation timeout to give the longest a reattach waits for a user cancel of the same run to settle: the cancel's own stop or delete
    /// may take the whole mutation timeout, and the report must not give up on the very edge of a cancel that succeeds. A cancel that never ends (a
    /// gateway call that ignores its token) then holds back only its own run's outcome, and only this long, instead of every run's until the app shuts
    /// down (issue #551).
    /// </summary>
    private static readonly TimeSpan CancelSettleMargin = TimeSpan.FromSeconds(30);

    private readonly CloudJobRunner _runner;
    private readonly IComputeGateway _compute;
    private readonly IStorageGateway _storage;
    private readonly IRunRepository _runs;
    private readonly IRunInputStore _inputs;
    private readonly IWorkerImageProvider _images;
    private readonly RunRowStore _rows;
    private readonly Func<string?> _downloadsFolder;
    private readonly ActiveRuns _active;
    private readonly ISettingsStore _settings;
    private readonly TimeProvider _time;
    private readonly DateTimeOffset _startedAt;
    private readonly TimeSpan _lookupTimeout;
    private readonly TimeSpan _mutationTimeout;
    private readonly TimeSpan _markedEndTimeout;
    private readonly TimeSpan _cancelSettleTimeout;

    /// <summary>
    /// For tests: called with the job id the moment a reattach has armed its (bounded) wait for a user cancel to settle, so a test advances virtual
    /// time only once that wait's timer exists rather than guessing from the clock's timer count.
    /// </summary>
    internal Action<string>? CancelSettleArmed { get; set; }

    /// <summary>For tests (issue #476 round 4): called with the job id and the budget the end call of a marked VM was given.</summary>
    internal Action<string, TimeSpan>? EndBudgetChosen { get; set; }

    /// <summary>For tests (issue #476 round 4): how many entries the keep-alive sweep's in-process memory holds (logged skips and confirmed-ended rows).</summary>
    internal int KeepAliveBookkeepingCount => _keepAliveSkipLogged.Count + _keepAliveDone.Count;
    private readonly IDiagnosticsLog _log;
    private readonly Action<string?>? _onSignInNeeded;

    /// <summary>Tells the app an account needs to sign in (review M3); the handler is the app's and must not be able to break a reconcile pass.</summary>
    private void SignInNeeded(string? accountSub)
    {
        try
        {
            _onSignInNeeded?.Invoke(accountSub);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Warning("reconciler", null, ex.GetType().Name);
        }
    }

    /// <summary>Jobs whose last reattach could not ask the cloud (Deferred), until a later pass judges them (issue #559).</summary>
    private readonly ConcurrentDictionary<string, byte> _deferredRuns = new();

    /// <summary>True when the last lifecycle enforcement had a row or VM it could not ask the cloud about.</summary>
    private volatile bool _lifecycleDeferred;

    /// <summary>True when the last reattach could not read the run table at all (a network-class failure), so no run was even listed to defer.</summary>
    private volatile bool _reattachDeferred;

    /// <summary>
    /// Passes can overlap (the launch pass runs outside <see cref="ReconcileOnReconnect"/>'s single flight), and the older may end last. Each pass
    /// takes a generation when it starts and only the latest-started one may write the deferred state, so an older pass finishing late never
    /// overwrites what a newer one found. One counter per kind of state, because a lone <see cref="ReattachAsync"/> and a lone
    /// <see cref="EnforceLifecycleAsync"/> are different passes over different state.
    /// </summary>
    private readonly object _generationGate = new();

    private long _lifecycleGeneration;
    private long _reattachGeneration;

    private long NextLifecycleGeneration()
    {
        lock (_generationGate)
        {
            return ++_lifecycleGeneration;
        }
    }

    private long NextReattachGeneration()
    {
        lock (_generationGate)
        {
            return ++_reattachGeneration;
        }
    }

    /// <summary>Writes <see cref="_lifecycleDeferred"/> unless a newer lifecycle pass has started since <paramref name="generation"/> began.</summary>
    private void SetLifecycleDeferred(long generation, bool value)
    {
        lock (_generationGate)
        {
            if (generation == _lifecycleGeneration)
            {
                _lifecycleDeferred = value;
            }
        }
    }

    /// <summary>Writes <see cref="_reattachDeferred"/> unless a newer reattach pass has started since <paramref name="generation"/> began.</summary>
    private void SetReattachDeferred(long generation, bool value)
    {
        lock (_generationGate)
        {
            if (generation == _reattachGeneration)
            {
                _reattachDeferred = value;
            }
        }
    }

    /// <summary>
    /// Records or clears one run's deferred mark. The pass that actually DRIVES a run owns that run's mark whatever its generation (a newer pass
    /// that was skipped because an older one drives the run must neither clear it nor have the older pass's late answer rejected).
    /// </summary>
    private void SetRunDeferred(string jobId, bool deferred)
    {
        if (deferred)
        {
            _deferredRuns[jobId] = 0;
        }
        else
        {
            _deferredRuns.TryRemove(jobId, out _);
        }
    }

    /// <summary>Clears the mark of a run that is no longer a candidate, unless a newer reattach pass has started since <paramref name="generation"/> began.</summary>
    private void ClearStaleRunDeferred(long generation, string jobId)
    {
        lock (_generationGate)
        {
            if (generation == _reattachGeneration)
            {
                _deferredRuns.TryRemove(jobId, out _);
            }
        }
    }

    /// <summary>
    /// How far back a finished run's VM is looked up by its job-id label. Older leaks are caught by the idle sweep, which lists by installation and
    /// so costs one call however long the history is; this keeps a launch from making one lookup per run row ever recorded.
    /// </summary>
    private static readonly TimeSpan LifecycleLookback = CloudScopes.LifecycleLookback;

    /// <summary>Issue #830: the scopes the installation-wide sweeps cover besides those of recent runs (the scope selected now: a VM of this installation with no history row is found there).</summary>
    private readonly Func<IEnumerable<CloudScope>> _additionalSweepScopes;

    /// <summary>Accounts (key: sub, empty for none) whose sign-out was already logged as "no obligation": the line is written when that state CHANGES for an account, not on every pass.</summary>
    private readonly HashSet<string> _loggedNoObligation = [];

    /// <param name="activeRuns">The registry the engine's Cancel looks in. Required: a private one would let a cancel miss a reattached run.</param>
    /// <param name="settings">Where the installation id (which VMs are ours, Hard Rule 9) and the idle-VM limit are read, on every pass so a changed setting applies at once.</param>
    /// <param name="onPhaseChanged">Told after the reconciler itself commits a phase (a run it fails); a run it resumes reports through the runner's own callback.</param>
    /// <param name="additionalSweepScopes">Issue #830: the scopes, besides those of every recent run row, the installation-wide sweeps (idle sweep) list in; the app passes the selected scope, so a VM no row names is still found.</param>
    /// <param name="markedEndTimeout">Issue #476: the longest the end call for a VM the Keep-alive sweep has marked may run (default <see cref="VmEnding.EndBudget"/>, under the mark's window); tests shorten it.</param>
    /// <param name="onSignInNeeded">Issue #830 review M3: told with the account's sub (null when it is not known) each time a run or computer could not be looked at because its Google account is signed out, so the shell can offer "Sign in as ...". A failing handler never stops the pass. Never given the email.</param>
    /// <param name="downloadsFolder">The default output parent for a run whose row names none.</param>
    public JobReconciler(
        CloudJobRunner runner,
        IComputeGateway compute,
        IStorageGateway storage,
        IRunRepository runs,
        IRunInputStore inputs,
        IWorkerImageProvider images,
        ActiveRuns activeRuns,
        ISettingsStore settings,
        Action<string, JobPhase>? onPhaseChanged = null,
        Func<string?>? downloadsFolder = null,
        TimeProvider? timeProvider = null,
        TimeSpan? lookupTimeout = null,
        TimeSpan? mutationTimeout = null,
        IDiagnosticsLog? log = null,
        TimeSpan? cancelSettleTimeout = null,
        Func<IEnumerable<CloudScope>>? additionalSweepScopes = null,
        Action<string?>? onSignInNeeded = null,
        TimeSpan? markedEndTimeout = null)
    {
        _markedEndTimeout = markedEndTimeout ?? VmEnding.EndBudget;
        _onSignInNeeded = onSignInNeeded;
        _additionalSweepScopes = additionalSweepScopes ?? (() => []);
        _log = log ?? NullDiagnosticsLog.Instance;
        _lookupTimeout = lookupTimeout ?? DefaultLookupTimeout;
        _mutationTimeout = mutationTimeout ?? DefaultMutationTimeout;
        _cancelSettleTimeout = cancelSettleTimeout ?? _mutationTimeout + CancelSettleMargin;
        _active = activeRuns;
        _settings = settings;
        _runner = runner;
        _compute = compute;
        _storage = storage;
        _runs = runs;
        _inputs = inputs;
        _images = images;
        _rows = new RunRowStore(runs, onPhaseChanged);
        _downloadsFolder = downloadsFolder ?? (() => null);
        _time = timeProvider ?? TimeProvider.System;
        _startedAt = _time.GetUtcNow();
    }

    /// <summary>
    /// True while a run or VM the passes so far could not judge (the cloud gave no answer) is still waiting for another look. The reconnect probe
    /// (<see cref="ReconcileOnReconnect"/>) runs only while this is true. A job leaves it once a later pass judges it.
    /// </summary>
    public bool HasDeferred => _lifecycleDeferred || _reattachDeferred || !_deferredRuns.IsEmpty;

    /// <summary>
    /// One full pass: reattaches the runs a killed app left (<see cref="ReattachAsync"/>) and enforces lifecycles (<see cref="EnforceLifecycleAsync"/>),
    /// concurrently, because a reattached run can take minutes and must not hold the housekeeping back. Safe to call again at any time (on
    /// reconnect): a job something already drives is skipped, a row already judged is terminal.
    /// </summary>
    public async Task ReconcileAsync(CancellationToken cancellationToken)
        => await (await BeginReconcileAsync(cancellationToken).ConfigureAwait(false)).ConfigureAwait(false);

    /// <summary>
    /// The same pass as <see cref="ReconcileAsync"/>, split at the point that matters to a repeating caller. A failure of the lifecycle step is logged,
    /// not thrown, so the reattached runs are always returned. The returned (outer) task ends when the
    /// lifecycle enforcement and the idle sweep have run and every non-terminal run has been judged (looked at in the cloud, so <see cref="HasDeferred"/>
    /// is settled) and handed to its own driver in <see cref="ActiveRuns"/>;
    /// its result is the inner task, which ends only when every reattached run has ENDED (minutes or hours). A caller that runs passes one at a
    /// time (<see cref="ReconcileOnReconnect"/>) waits for the outer task only: a long run must not hold the next pass back.
    /// </summary>
    public async Task<Task> BeginReconcileAsync(CancellationToken cancellationToken)
    {
        var started = StartReattachAsync(NextReattachGeneration(), cancellationToken);
        try
        {
            await EnforceLifecycleAsync(NextLifecycleGeneration(), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && cancellationToken.IsCancellationRequested))
        {
            // The reattach is already under way (its runs register as drivers whatever happens here), so the caller must still be handed
            // the task for them: a lifecycle failure that threw instead would orphan them, unwaited and unreported (issue #575). The class
            // of the error is the only trace (never its message, which could carry a path); the next launch or reconnect looks again.
            _log.Warning("reconciler", null, ex.GetType().Name);
        }
        catch (OperationCanceledException)
        {
            // Shutdown: the exception goes to the caller, and the reattach task is observed here so a fault in it is never an unobserved task
            // exception. Its runs are cancelled by the same token, and their drivers end with the process.
            _ = started.ContinueWith(
                static t => _ = t.Exception,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously | TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
            throw;
        }

        var (judged, outcomes) = await started.ConfigureAwait(false);
        await judged.ConfigureAwait(false);
        return outcomes;
    }

    /// <summary>
    /// Hard Rules 9 and 11, applied to what already exists. (1) A finished run's VM, found by its job-id label, in a state its lifecycle label
    /// forbids is fixed: <c>delete</c> deletes it, <c>stop</c> stops a running one, <c>keep</c> past its expiry (the run's finish plus its keep-alive
    /// minutes; at once for a run that did not complete, as startup.sh does) ends per afterKeepAlive. (2) This installation's VMs stopped longer
    /// than <see cref="CloudHousekeepingSettings.IdleStoppedVmLimit"/> are deleted. Only VMs carrying our app label AND this installation's id are
    /// touched; a VM whose labels were not read is judged only by its job id and the run row's own installation id.
    /// Returns what it did. Never throws for a cloud failure or an error in one row: that row or VM gets a <see cref="LifecycleAction.Deferred"/> or <see cref="LifecycleAction.Failed"/> outcome and the pass goes on with the rest.
    /// </summary>
    public Task<IReadOnlyList<LifecycleOutcome>> EnforceLifecycleAsync(CancellationToken cancellationToken)
        => EnforceLifecycleAsync(NextLifecycleGeneration(), cancellationToken);

    private async Task<IReadOnlyList<LifecycleOutcome>> EnforceLifecycleAsync(long generation, CancellationToken cancellationToken)
    {
        try
        {
            return await EnforceLifecycleCoreAsync(generation, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && cancellationToken.IsCancellationRequested))
        {
            // The pass itself failed (a failing repository): deferred if and only if it is a network class, so a non-network failure
            // cannot keep the reconnect probe alive (issue #559). The caller still sees the exception.
            SetLifecycleDeferred(generation, IsNoAnswerException(ex));
            throw;
        }
    }

    private async Task<IReadOnlyList<LifecycleOutcome>> EnforceLifecycleCoreAsync(long generation, CancellationToken cancellationToken)
    {
        var outcomes = new List<LifecycleOutcome>();
        var installation = InstallationId.GetOrCreate(_settings);
        var now = _time.GetUtcNow();
        var all = await _runs.GetAllAsync(cancellationToken).ConfigureAwait(false);
        var finished = all
            .Select(r => r.JobId)
            .Distinct()
            .Select(id => RunRowStore.LatestRecord(all, id)!)
            .Where(r => JobStateMachine.IsTerminal(r.Phase)
                && string.Equals(r.Target, "cloud", StringComparison.OrdinalIgnoreCase)
                && string.Equals(r.InstallationId, installation, StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(r.ProjectId)
                && (r.FinishedAt ?? r.CreatedUtc) >= now - LifecycleLookback)
            .ToList();
        foreach (var row in finished)
        {
            await EnforceRowAsync(row, installation, now, outcomes, cancellationToken).ConfigureAwait(false);
        }

        // Issue #830: the idle sweep lists in every scope a recent finished run of this installation was made in, never in whatever is selected now.
        var extra = _additionalSweepScopes().ToList();
        var sweepScopes = CloudScopes.Dedupe(finished.Select(ScopeOf).Concat(extra), extra.FirstOrDefault(s => s.AccountSub is not null));
        foreach (var scope in sweepScopes)
        {
            await SweepIdleAsync(scope, installation, now, outcomes, cancellationToken).ConfigureAwait(false);
        }

        bool deferred;
        lock (outcomes)
        {
            deferred = outcomes.Any(o => o.Action == LifecycleAction.Deferred);
        }

        SetLifecycleDeferred(generation, deferred);

        LifecycleOutcome[] signedOut;
        lock (outcomes)
        {
            signedOut = [.. outcomes.Where(o => o.Action == LifecycleAction.NeedsSignIn)];
        }

        // Review F4: the shell bar is only for an account with something that could still be running (an unfinished run, an unconfirmed end, an unexpired keep-alive); otherwise it is logged only.
        // Round 5 L2: an outcome that names no account (a row with none) acted as the ACTIVE account, so it is resolved to it here (the selection path's scope carries it); only when there is none does it match rows with no account alone. The resolution happens BEFORE the distinct, so a null-sub outcome and a named one of the same account are one account and one message.
        var activeSub = extra.FirstOrDefault(s => s.AccountSub is not null)?.AccountSub;
        var signedOutSubs = signedOut.Select(o => o.AccountSub ?? activeSub).Distinct().ToList();
        foreach (var sub in signedOutSubs)
        {
            var key = sub ?? string.Empty;
            if (RunObligations.ExistForAccount(all, sub, installation, now))
            {
                lock (_loggedNoObligation)
                {
                    _loggedNoObligation.Remove(key);
                }

                SignInNeeded(sub);
            }
            else
            {
                bool changed;
                lock (_loggedNoObligation)
                {
                    changed = _loggedNoObligation.Add(key);
                }

                if (changed)
                {
                    _log.Warning("reconciler", null, "account-signed-out-no-obligation");
                }
            }
        }

        // An account that is no longer signed out forgets its state, so a later sign-out is logged again.
        lock (_loggedNoObligation)
        {
            _loggedNoObligation.RemoveWhere(k => !signedOutSubs.Any(s => (s ?? string.Empty) == k));
        }

        return outcomes;
    }

    /// <summary>
    /// One finished run. Whatever goes wrong here is this row's alone: a cloud answer that does not come, a refusal, or an error nobody
    /// foresaw (recorded by its class, never its message, which could carry a file name) all end THIS row and the pass goes on to the next.
    /// </summary>
    private async Task EnforceRowAsync(RunRecord row, string installation, DateTimeOffset now, List<LifecycleOutcome> outcomes, CancellationToken cancellationToken, bool noteEndedByApp = false)
    {
        try
        {
            var scope = ScopeOf(row);
            var driver = _active.TryStart(row.JobId, async token =>
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, cancellationToken);
                IReadOnlyList<VmDescriptor> vms;
                try
                {
                    vms = await Lookup(t => _compute.FindByJobIdAsync(scope, row.JobId, t), linked.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is CloudOperationException or TimeoutException)
                {
                    Record(outcomes, row.JobId, string.Empty, ex);
                    return;
                }

                var options = RunOptionsJson.TryDeserialize(row.OptionsJson);
                // Round 5 of #476 (finding 1): a row is settled (and the sweep stops looking it up) ONLY once a VM under its job id is confirmed DELETED (by this sweep's own delete, or
                // gone on the re-read), the one terminal state. A STOPPED VM is not terminal (the Cloud page can Start it again and it would then run to maxRunDuration), an empty listing may be a
                // reuse that borrowed the VM (its donor labels come back if that reuse fails, #970), and a relabelled or unlabelled VM may be given back: all stay unsettled, judged again every tick
                // (one FindByJobId per row per tick, bounded by KeepAliveSweepReach).
                var unsettled = vms.Count == 0;
                foreach (var vm in vms)
                {
                    var want = WantedByLifecycle(row, vm, options, installation, now);
                    if (want == VmWant.Leave)
                    {
                        unsettled = true;
                        continue;
                    }

                    VmDescriptor? fresh;
                    var wasRunning = false;
                    TimeSpan? endBudget = null;
                    try
                    {
                        // Round 10 M1, round 11 M1, round 12 M1 (code read 2026-10-30): the listing is a snapshot, and another run's reuse may have relabelled this VM since, so before ANY delete or stop it is read
                        // again and skipped unless it still carries this row's job id. This NARROWS the window and cannot close it: Compute's delete and stop have no label precondition, so a reuse's setLabels that
                        // lands between this read and the call still affects the VM. Consequence (THEORY, unverified): for a delete, that run's reuse then fails (the VM is gone) and falls to the create ladder,
                        // so no VM leaks and the run proceeds. Round 12 M2, round 13 M2: when the LISTED descriptor had labels, null labels on the re-read mean they were
                        // removed in between (GoogleComputeGateway.ToDescriptor maps the API's absent label map to null), so the VM skips like a mismatch (maxRunDuration is the backstop only for a RUNNING one; a STOPPED one is never listed again and must be deleted by hand in the console, the log line is the only trace). When the listing itself had null labels
                        // (labels not read, as IsOurs documents) there is nothing to compare: the documented fallback stands and the action proceeds. A VM that is gone is nothing to act on.
                        fresh = await Lookup(t => _compute.GetVmAsync(scope, vm.Name, vm.Zone, t), linked.Token).ConfigureAwait(false);
                        string? skip = fresh is null ? "vm-gone"
                            : fresh.Labels is null ? (vm.Labels is not null ? "vm-unlabelled" : null)
                            : (!fresh.Labels.TryGetValue("job-id", out var jobLabel) || jobLabel != row.JobId) ? "vm-relabelled"
                            : null;
                        if (skip is not null)
                        {
                            // Observable (ids and states only, never a name).
                            _log.Warning("reconciler", row.JobId, (want == VmWant.Delete ? "delete-skipped-" : "stop-skipped-") + skip);
                            unsettled |= skip != "vm-gone"; // gone on the re-read is confirmed gone; relabelled or unlabelled may come back
                            continue;
                        }

                        if (noteEndedByApp)
                        {
                            // Issue #476, in this order so nothing is marked that is not about to be ended: the platform's clock must be known and agree (a VM read with no server time, or a
                            // laptop clock far from it, is left for the next sweep), the lifecycle is judged again on the fresh read, and ONLY THEN the VM is marked.
                            if (fresh!.ServerNow is null)
                            {
                                // No platform time (a proxy that strips the Date header) is the same for every row and every tick: one line per process. maxRunDuration stays the backstop.
                                LogNoServerClockOnce(row.JobId);
                                unsettled = true;
                                continue;
                            }

                            if (!KeepAliveClockAgrees(row, options, fresh))
                            {
                                LogSkippedOnce(row.JobId, "ClockSkew");
                                unsettled = true;
                                continue;
                            }

                            want = WantedByLifecycle(row, fresh, options, installation, now);
                            if (want == VmWant.Leave)
                            {
                                unsettled = true;
                                continue;
                            }

                            // The mark is written with the label fingerprint just read, so a VM a newer run has claimed since is neither marked nor ended, and a run that tries to reuse it
                            // after the mark is refused. Written right before the end call, never earlier.
                            VmDescriptor? marked;
                            try
                            {
                                marked = await Lookup(t => _compute.MarkEndingAsync(scope, vm.Name, vm.Zone, row.JobId, t), linked.Token).ConfigureAwait(false);
                            }
                            catch (CloudOperationException ex)
                            {
                                // Round 4 finding 2: a refused fence write (an org policy answers 412 as well) is named by its class, once per job and class. DECISION (agent-made, 2026-10-31,
                                // reversible): the VM is then NOT ended without the fence; the failure is recorded below and the next sweep tries again (maxRunDuration is the backstop).
                                LogSkippedOnce(row.JobId, "MarkEndingFailed:" + RunErrorCodes.For(ex.Kind));
                                throw;
                            }

                            if (marked is null)
                            {
                                // The VM is gone, another run's now, or the fence write lost a race: nothing to end under this window, said once instead of dropped.
                                LogSkippedOnce(row.JobId, "MarkEndingNotApplied");
                                unsettled = true;
                                continue;
                            }

                            wasRunning = fresh.Status == "RUNNING";
                            // Round 3 M3: the end call (with every retry its resilience layer makes and the wait for the operation) is cut off before the mark expires, so a delete that
                            // keeps failing cannot land after a newer run was allowed to reuse the VM. A request Compute already accepted was accepted inside the mark.
                            // Round 4 finding 1: a mark already inside its window (an earlier sweep's) leaves only what is left of it, less a margin; with too little left no end call starts
                            // under it (the mark runs out, and the next sweep writes a fresh one before its own end call).
                            var budget = _markedEndTimeout;
                            if (VmEnding.TryMarkedAt(marked.Labels, _time.GetUtcNow(), out var markedAt))
                            {
                                var remaining = markedAt + VmEnding.Window - _time.GetUtcNow() - VmEnding.EndSafetyMargin;
                                if (remaining < VmEnding.MinEndBudget)
                                {
                                    LogSkippedOnce(row.JobId, "MarkExpiring");
                                    unsettled = true;
                                    continue;
                                }

                                budget = remaining < budget ? remaining : budget;
                            }

                            endBudget = budget;
                            EndBudgetChosen?.Invoke(row.JobId, budget);
                        }
                    }
                    catch (Exception ex) when (ex is CloudOperationException or TimeoutException)
                    {
                        Record(outcomes, row.JobId, vm.Name, ex);
                        unsettled = true;
                        continue;
                    }

                    var wantedAction = want == VmWant.Delete ? LifecycleAction.VmDeleted : LifecycleAction.VmStopped;
                    var ended = await ApplyAsync(scope, row.JobId, vm, want == VmWant.Delete, wantedAction, outcomes, linked.Token, endBudget).ConfigureAwait(false);
                    if (want != VmWant.Delete)
                    {
                        unsettled = true; // a stopped VM is not terminal
                    }
                    else if (!ended)
                    {
                        // ApplyAsync adds the outcome only when the call answered (a 404 is "gone already"); a failed or timed-out end leaves the row for the next tick.
                        lock (outcomes)
                        {
                            unsettled |= !outcomes.Any(o => o.JobId == row.JobId && o.VmName == vm.Name && o.Action == wantedAction);
                        }
                    }

                    // Only a RUNNING VM the app's own call changed is "still running past its window": a stopped one the app only deleted, or one already gone, is not.
                    if (noteEndedByApp && ended && wasRunning)
                    {
                        await NoteKeepAliveEndedAsync(row.JobId).ConfigureAwait(false);
                    }
                }

                if (noteEndedByApp && !unsettled)
                {
                    _keepAliveDone[row.JobId] = 0;
                }
            });
            if (driver is not null)
            {
                await driver.ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && cancellationToken.IsCancellationRequested))
        {
            RecordUnexpected(outcomes, row.JobId, string.Empty, ex);
            _log.Warning("reconciler", row.JobId, ex.GetType().Name);
        }
    }

    private static CloudScope ScopeOf(RunRecord row) => RunScope.Of(row);

    private enum VmWant
    {
        Leave,
        Stop,
        Delete,
    }

    /// <summary>What a finished run's VM should be now, from its lifecycle label (or, when the gateway did not read labels, the run's own options).</summary>
    private static VmWant WantedByLifecycle(RunRecord row, VmDescriptor vm, RunOptions? options, string installation, DateTimeOffset now)
    {
        var want = WantedByLifecycleCore(row, vm, options, installation, now);

        // Round 8 M3: a VM that is not the row's own deg-<job> is another run's (a failed or cancelled reuse relabelled it), whatever the row's state or the lifecycle label says:
        // stopped at most, never deleted. Round 9 M2 (DECISION, reversible): the one exception is the user's own after-run Delete (or keep-alive-then-Delete) of a run that COMPLETED (or partially completed, as RunOutcomeRecorder also applies it live) on that VM,
        // the VM the row records as its own (VmName); the app died before the live delete, so the launch finishes it.
        var completedOnIt = row.Phase is JobPhase.Completed or JobPhase.PartiallyCompleted && row.VmName is not null && row.VmName == vm.Name;
        return want == VmWant.Delete && !completedOnIt && vm.Name != VmTerminator.OwnVmName(row.JobId) ? (vm.Status == "RUNNING" ? VmWant.Stop : VmWant.Leave) : want;
    }

    private static VmWant WantedByLifecycleCore(RunRecord row, VmDescriptor vm, RunOptions? options, string installation, DateTimeOffset now)
    {
        if (!IsOurs(vm, installation))
        {
            return VmWant.Leave;
        }

        // Issue #476: a VM whose labels were read and whose job-id is another run's (VmProvisioner reuse relabels a stopped VM to the run that
        // reuses it) is that run's now, never this row's to end.
        if (vm.Labels is not null && vm.Labels.TryGetValue("job-id", out var vmJob) && !string.Equals(vmJob, row.JobId, StringComparison.Ordinal))
        {
            return VmWant.Leave;
        }

        if (CancelOwnsTheVm(row))
        {
            // Issue #938: a cancel deletes the VM the run CREATED. One found under a cancelled run's job id (a create Compute accepted after the app stopped looking, or after the app was closed
            // mid-cancel) is that cancel's to delete, whatever its lifecycle label says: nothing is left worth stopping or keeping.
            // Round 3 H1: a VM that does not carry the run's own name was created by ANOTHER run and only relabelled to this one by a reuse that landed after the cancel. It is stopped,
            // never deleted (its owner's keep-alive promise stands), and its labels are left.
            return vm.Name == VmTerminator.OwnVmName(row.JobId) ? VmWant.Delete : vm.Status == "RUNNING" ? VmWant.Stop : VmWant.Leave;
        }

        var lifecycle = vm.Labels is not null && vm.Labels.TryGetValue("lifecycle", out var label)
            ? label
            : options is null ? null : CloudJobRequestFactory.LifecycleLabel(options.AfterTask);
        switch (lifecycle)
        {
            case var l when l == CloudJobRequestFactory.LifecycleLabel(AfterTaskAction.Delete):
                return VmWant.Delete;
            case "stop":
                return vm.Status == "RUNNING" ? VmWant.Stop : VmWant.Leave;
            case "keep" when options is not null:
                // startup.sh keep_hold holds the VM for the whole window only after worker exit 0, which the app records as Completed or
                // PartiallyCompleted; exit 2 (Failed) and 3 (Cancelled) apply afterKeepAlive at once.
                var held = row.Phase is JobPhase.Completed or JobPhase.PartiallyCompleted;
                var expired = !held || now >= (row.FinishedAt ?? row.CreatedUtc).AddMinutes(options.KeepAliveMinutes);
                if (!expired)
                {
                    return VmWant.Leave;
                }

                return options.AfterKeepAlive == AfterKeepAliveAction.Delete ? VmWant.Delete : vm.Status == "RUNNING" ? VmWant.Stop : VmWant.Leave;
            default:
                return VmWant.Leave;
        }
    }

    /// <summary>
    /// A run the user cancelled (Cancelled with no code of its own: a cleanup's Stop or Delete is Cancelled under <see cref="RunErrorCodes.StoppedByCleanup"/> / <see cref="RunErrorCodes.DeletedByCleanup"/>, which the user chose),
    /// or one whose cancel could not confirm its VM gone (<see cref="RunErrorCodes.CancelFailed"/>).
    /// </summary>
    private static bool CancelOwnsTheVm(RunRecord row)
        => (row.Phase == JobPhase.Cancelled && (string.IsNullOrEmpty(row.ErrorCode) || row.ErrorCode == RunErrorCodes.LateCreateUnconfirmed)) || (row.Phase == JobPhase.Failed && RunErrorCodes.IsUnconfirmedCancel(row.ErrorCode));

    /// <summary>Hard Rule 9: a VM whose labels were read must carry our app label and this installation's id. Labels not read (null in the LISTING) say nothing against it (its job id already matched a row of this installation); null labels on a re-read of a VM the listing showed with labels mean they were removed, and that VM is skipped (round 13 M2).</summary>
    private static bool IsOurs(VmDescriptor vm, string installation)
        => vm.Labels is null
            || (vm.Labels.TryGetValue("app", out var app) && app == VmSpec.AppLabelValue
                && vm.Labels.TryGetValue("installation-id", out var owner) && owner == installation);

    private async Task SweepIdleAsync(CloudScope scope, string installation, DateTimeOffset now, List<LifecycleOutcome> outcomes, CancellationToken cancellationToken)
    {
        var limit = CloudHousekeepingSettings.IdleStoppedVmLimit(_settings);

        IReadOnlyList<VmDescriptor> listed;
        try
        {
            listed = await Lookup(token => _compute.ListByInstallationAsync(scope, installation, token), cancellationToken).ConfigureAwait(false);
        }
        catch (AccountAuthException ex)
        {
            RecordUnexpected(outcomes, string.Empty, string.Empty, ex);
            return;
        }
        catch (Exception ex) when (ex is CloudOperationException or TimeoutException)
        {
            Record(outcomes, string.Empty, string.Empty, ex);
            return;
        }

        var all = await _runs.GetAllAsync(cancellationToken).ConfigureAwait(false);
        foreach (var vm in listed.Where(v => IsOurs(v, installation) && v.Labels is not null && IsIdle(v, now, limit)))
        {
            var jobId = vm.Labels!.TryGetValue("job-id", out var id) ? id : string.Empty;
            if (jobId.Length == 0 || (RunRowStore.LatestRecord(all, jobId) is { } row && !JobStateMachine.IsTerminal(row.Phase)))
            {
                continue;
            }

            try
            {
                var driver = _active.TryStart(jobId, async token =>
                {
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, cancellationToken);
                    // Looked at again just before the delete: a run that restarted the VM since the list must not lose it.
                    VmDescriptor? fresh;
                    try
                    {
                        fresh = await Lookup(t => _compute.GetVmAsync(scope, vm.Name, vm.Zone, t), linked.Token).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is CloudOperationException or TimeoutException)
                    {
                        Record(outcomes, jobId, vm.Name, ex);
                        return;
                    }

                    // Round 13 M1: a donor a reuse is claiming keeps its stop time (setLabels does not change it), so the re-read also checks the job id the listing carried.
                    if (fresh is not null && (fresh.Labels is null || !fresh.Labels.TryGetValue("job-id", out var freshJob) || freshJob != jobId))
                    {
                        _log.Warning("reconciler", jobId, fresh.Labels is null ? "idle-delete-skipped-vm-unlabelled" : "idle-delete-skipped-vm-relabelled");
                        return;
                    }

                    if (fresh is not null && IsIdle(fresh, now, limit))
                    {
                        await ApplyAsync(scope, jobId, vm, true, LifecycleAction.IdleVmDeleted, outcomes, linked.Token).ConfigureAwait(false);
                    }
                });
                if (driver is not null)
                {
                    await driver.ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (!(ex is OperationCanceledException && cancellationToken.IsCancellationRequested))
            {
                RecordUnexpected(outcomes, jobId, vm.Name, ex);
                _log.Warning("reconciler", jobId, ex.GetType().Name);
            }
        }
    }

    /// <summary>Stopped for longer than <paramref name="limit"/>. A VM whose stop time is unknown is not provably idle, so it is kept.</summary>
    private static bool IsIdle(VmDescriptor vm, DateTimeOffset now, TimeSpan limit)
        => VmFacts.IsStopped(vm) && vm.StoppedAt is { } stoppedAt && now - stoppedAt >= limit;

    /// <summary>Deletes or stops one VM under its own name and zone (found by label, never by a remembered name). A 404 on delete is the state wanted.</summary>
    private async Task<bool> ApplyAsync(CloudScope scope, string jobId, VmDescriptor vm, bool delete, LifecycleAction done, List<LifecycleOutcome> outcomes, CancellationToken cancellationToken, TimeSpan? deadline = null)
    {
        var applied = true;
        try
        {
            await WithDeadline(
                async token =>
                {
                    try
                    {
                        await (delete ? _compute.DeleteVmAsync(scope, vm.Name, vm.Zone, token) : _compute.StopVmAsync(scope, vm.Name, vm.Zone, token)).ConfigureAwait(false);
                    }
                    catch (CloudOperationException ex) when (ex.Error.HttpStatus == 404)
                    {
                        applied = false; // gone already: whoever ended it first did it (issue #476: the app then has nothing to note)
                    }

                    return true;
                },
                deadline is { } cap && cap < _mutationTimeout ? cap : _mutationTimeout,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is CloudOperationException or TimeoutException)
        {
            Record(outcomes, jobId, vm.Name, ex);
            return false;
        }

        lock (outcomes)
        {
            outcomes.Add(new LifecycleOutcome(jobId, vm.Name, done));
        }

        return applied;
    }

    /// <summary>
    /// How far the platform's clock (the read's server time) may differ from the laptop's before the sweep ends nothing: the app judges the window on the
    /// laptop's clock and the worker on the VM's, so a laptop clock further off than this could end a VM early. THEORY (unverified): two minutes is well
    /// inside the 5 minute grace and above normal NTP drift; not measured. An unknown platform time (no server time on the read) is treated as too skewed.
    /// </summary>
    private static readonly TimeSpan MaxKeepAliveClockSkew = TimeSpan.FromMinutes(2);

    /// <summary>
    /// The window is over on the PLATFORM's clock too (the issue #691 server time the read carried), and the laptop's clock is close enough to it to have been
    /// trusted: a fast laptop clock must not end a VM before the worker would have. False when the server time is unknown, too far from the laptop's, or earlier than window plus grace.
    /// </summary>
    private bool KeepAliveClockAgrees(RunRecord row, RunOptions? options, VmDescriptor fresh)
    {
        if (options is null || fresh.ServerNow is not { } server || (server - _time.GetUtcNow()).Duration() > MaxKeepAliveClockSkew)
        {
            return false;
        }

        return server >= (row.FinishedAt ?? row.CreatedUtc).AddMinutes(options.KeepAliveMinutes) + KeepAliveGrace;
    }

    /// <summary>Jobs the sweep has already said it is skipping, by reason: one log line per row, not per tick.</summary>
    private readonly ConcurrentDictionary<(string JobId, string Reason), byte> _keepAliveSkipLogged = new();

    private void LogSkippedOnce(string jobId, string reason)
    {
        if (_keepAliveSkipLogged.TryAdd((jobId, reason), 0))
        {
            _log.Warning("keepalive-sweep-skipped", jobId, reason);
        }
    }

    /// <summary>Rows whose VM the sweep has confirmed settled (ended, gone, another run's or stopped): later ticks skip them. In-process only, pruned to the rows still in reach.</summary>
    private readonly ConcurrentDictionary<string, byte> _keepAliveDone = new();

    private int _noServerClockLogged;

    private void LogNoServerClockOnce(string jobId)
    {
        if (Interlocked.Exchange(ref _noServerClockLogged, 1) == 0)
        {
            _log.Warning("keepalive-sweep-skipped", jobId, "NoServerClock");
        }
    }

    /// <summary>The longest past a Keep-alive window the worker's own end of the VM (startup.sh keep_hold) gets before the app ends it. THEORY (unverified): five minutes covers the worker's own Compute call and its retries; not measured on a real VM.</summary>
    public static readonly TimeSpan KeepAliveGrace = TimeSpan.FromMinutes(5);

    /// <summary>A tick looks only at windows that ended within this long: a VM cannot outlive its maxRunDuration, and older ones are the launch pass's and the idle sweep's.</summary>
    private static readonly TimeSpan KeepAliveSweepReach = TimeSpan.FromDays(1);

    /// <summary>
    /// Issue #476, Hard Rule 11: the app-open half of the Keep-alive guarantee, run every few minutes by <see cref="KeepAliveSweeper"/>. A run that Completed
    /// holding its VM for <c>KeepAliveMinutes</c> has that VM stopped or deleted (per its afterKeepAlive) once the window plus <see cref="KeepAliveGrace"/> has
    /// passed and the VM is still there. The VM is found by the run's job-id label (never by a remembered name) and looked at again just before it is ended,
    /// so a VM another run has reused since is never touched. The finished row gets the <see cref="RunErrorCodes.KeepAliveEndedByApp"/> note unless it already
    /// carries a note. Returns what it did; never throws for a cloud failure or one row's error.
    /// </summary>
    public async Task<IReadOnlyList<LifecycleOutcome>> EnforceKeepAliveAsync(CancellationToken cancellationToken)
    {
        var outcomes = new List<LifecycleOutcome>();
        var installation = InstallationId.GetOrCreate(_settings);
        var now = _time.GetUtcNow();
        var all = await _runs.GetAllAsync(cancellationToken).ConfigureAwait(false);
        var inReach = all
            .Select(r => r.JobId)
            .Distinct()
            .Select(id => RunRowStore.LatestRecord(all, id)!)
            .Where(r => r.Phase is JobPhase.Completed or JobPhase.PartiallyCompleted
                && string.Equals(r.Target, "cloud", StringComparison.OrdinalIgnoreCase)
                && string.Equals(r.InstallationId, installation, StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(r.ProjectId)
                && KeepAliveWindowEndedRecently(r, now))
            .ToList();

        // The in-process memory only holds rows still in reach (a row that leaves it is forgotten), so it never grows without bound.
        var inReachIds = inReach.Select(r => r.JobId).ToHashSet(StringComparer.Ordinal);
        foreach (var key in _keepAliveSkipLogged.Keys.Where(k => !inReachIds.Contains(k.JobId)))
        {
            _keepAliveSkipLogged.TryRemove(key, out _);
        }

        foreach (var id in _keepAliveDone.Keys.Where(id => !inReachIds.Contains(id)))
        {
            _keepAliveDone.TryRemove(id, out _);
        }

        // Round 4 finding 9: a finished row with no finish time cannot be dated, and its start is earlier than its real window end, so it is skipped (and said once), never ended early.
        foreach (var undated in inReach.Where(r => r.FinishedAt is null))
        {
            LogSkippedOnce(undated.JobId, "NoFinishTime");
        }

        var due = inReach.Where(r => r.FinishedAt is not null && !_keepAliveDone.ContainsKey(r.JobId)).ToList();
        foreach (var row in due)
        {
            // The grace is held by the filter above (now >= window end + grace), so the expiry check inside is already met.
            await EnforceRowAsync(row, installation, now, outcomes, cancellationToken, noteEndedByApp: true).ConfigureAwait(false);
        }

        return outcomes;
    }

    private static bool KeepAliveWindowEndedRecently(RunRecord row, DateTimeOffset now)
    {
        var options = RunOptionsJson.TryDeserialize(row.OptionsJson);
        if (options is null || options.AfterTask != AfterTaskAction.KeepAlive)
        {
            return false;
        }

        var expiry = (row.FinishedAt ?? row.CreatedUtc).AddMinutes(options.KeepAliveMinutes); // like the launch pass
        return now >= expiry + KeepAliveGrace && now < expiry + KeepAliveSweepReach;
    }

    /// <summary>Notes on the finished row that the app ended its VM. One note per row: a row that already has one (say worker_default_account) keeps it.</summary>
    private async Task NoteKeepAliveEndedAsync(string jobId)
    {
        try
        {
            await _rows.UpdateRowAsync(
                jobId,
                row => string.IsNullOrWhiteSpace(row.ErrorCode)
                    ? row with { ErrorCode = RunErrorCodes.KeepAliveEndedByApp }
                    : row,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Warning("reconciler", jobId, ex.GetType().Name);
        }
    }

    /// <summary>
    /// Records why a call did not answer: no answer (offline, a deadline) is <see cref="LifecycleAction.Deferred"/>, a refusal (permission, org
    /// policy) is <see cref="LifecycleAction.Failed"/>. Either is that row's or VM's alone: the pass goes on with the rest.
    /// </summary>
    private static void Record(List<LifecycleOutcome> outcomes, string jobId, string vmName, Exception ex)
    {
        var noAnswer = IsNoAnswerException(ex);
        var code = NoAnswerCode(ex);
        lock (outcomes)
        {
            outcomes.Add(new LifecycleOutcome(jobId, vmName, noAnswer ? LifecycleAction.Deferred : LifecycleAction.Failed, code));
        }
    }

    /// <summary>An error nobody foresaw for one row or VM: recorded as Failed with the error's class name (never its message).</summary>
    private static void RecordUnexpected(List<LifecycleOutcome> outcomes, string jobId, string vmName, Exception ex)
    {
        lock (outcomes)
        {
            // Issue #830: an account that is signed out is a named, fixable state (sign in as it), not an error nobody foresaw.
            // Only an expired sign-in has "Sign in" as its action (review M2); a lost connection or a locked accounts file is Deferred and the reconnect probe looks again.
            outcomes.Add(ex switch
            {
                AccountAuthException auth when NeedsSignIn(auth) => new LifecycleOutcome(jobId, vmName, LifecycleAction.NeedsSignIn, auth.Code, auth.AccountSub),
                AccountAuthException auth => new LifecycleOutcome(jobId, vmName, LifecycleAction.Deferred, auth.Code),
                _ => new LifecycleOutcome(jobId, vmName, LifecycleAction.Failed, ex.GetType().Name),
            });
        }
    }

    /// <summary>Reattaches every non-terminal cloud run created before this reconciler existed, concurrently, and returns what happened to each once they have all ended.</summary>
    public async Task<IReadOnlyList<ReattachOutcome>> ReattachAsync(CancellationToken cancellationToken)
    {
        var (_, outcomes) = await StartReattachAsync(NextReattachGeneration(), cancellationToken).ConfigureAwait(false);
        return await outcomes.ConfigureAwait(false);
    }

    /// <summary>
    /// Starts every reattach. <c>Judged</c> ends when each run has been judged: looked at in the cloud and either left deferred or on its way to a
    /// driver (<see cref="HasDeferred"/> is then settled for this pass). <c>Outcomes</c> ends when every run has ended.
    /// </summary>
    private async Task<(Task Judged, Task<IReadOnlyList<ReattachOutcome>> Outcomes)> StartReattachAsync(long generation, CancellationToken cancellationToken)
    {
        IReadOnlyList<RunRecord> all;
        try
        {
            all = await _runs.GetAllAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && cancellationToken.IsCancellationRequested))
        {
            // No run could be listed, so no per-run mark can say so: the pass itself is deferred if and only if it is a network class (issue #559).
            SetReattachDeferred(generation, IsNoAnswerException(ex));
            throw;
        }

        SetReattachDeferred(generation, false);
        var candidates = all
            .Select(r => r.JobId)
            .Distinct()
            .Select(id => RunRowStore.LatestRecord(all, id)!)
            .Where(r => !JobStateMachine.IsTerminal(r.Phase) && r.CreatedUtc < _startedAt)
            .ToList();
        var candidateIds = candidates.Select(r => r.JobId).ToHashSet(StringComparer.Ordinal);
        foreach (var stale in _deferredRuns.Keys.Where(id => !candidateIds.Contains(id)))
        {
            // A run deferred offline that has since ended (cancelled, deleted) is no longer anyone's to look at: it must not keep the probe alive.
            ClearStaleRunDeferred(generation, stale);
        }

        var judgements = candidates.Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToList();
        var tasks = candidates.Select((row, i) => ReattachOneAsync(row, judgements[i], cancellationToken)).ToList();
        return (Task.WhenAll(judgements.Select(j => j.Task)), WhenAllOutcomesAsync(tasks));
    }

    private static async Task<IReadOnlyList<ReattachOutcome>> WhenAllOutcomesAsync(List<Task<ReattachOutcome>> tasks)
        => await Task.WhenAll(tasks).ConfigureAwait(false);

    private async Task<ReattachOutcome> ReattachOneAsync(RunRecord row, TaskCompletionSource judged, CancellationToken cancellationToken)
    {
        try
        {
            var outcome = await ReattachCoreAsync(row, judged, cancellationToken).ConfigureAwait(false);
            if (outcome.Action != ReattachAction.Skipped)
            {
                // Skipped: something else drives the run (or it is not ours), and whoever drives it owns its mark.
                SetRunDeferred(row.JobId, outcome.Action == ReattachAction.Deferred);
            }

            return outcome;
        }
        finally
        {
            judged.TrySetResult();
        }
    }

    private async Task<ReattachOutcome> ReattachCoreAsync(RunRecord row, TaskCompletionSource judged, CancellationToken cancellationToken)
    {
        var underway = new Underway(() =>
        {
            SetRunDeferred(row.JobId, deferred: false);
            judged.TrySetResult();
        });
        try
        {
            // Registered BEFORE the first look at the cloud, and covering every write the reattach can make (the look, a failure it records,
            // a cancel it finishes, the run itself): a cancel at any moment finds the driver, stops it and waits for it, so one writer at a time.
            ReattachOutcome? outcome = null;
            var driver = _active.TryStart(row.JobId, async token =>
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, cancellationToken);
                outcome = await DecideAndActAsync(row, underway, linked.Token).ConfigureAwait(false);
            });
            if (driver is null)
            {
                return new ReattachOutcome(row.JobId, ReattachAction.Skipped);
            }

            await driver.ConfigureAwait(false);
            return outcome ?? await OutcomeOfCancelledAsync(row.JobId, underway.Action, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The app is shutting down while this reattach is working on the run (finishing a cancel it found half done, or driving it): say so
            // (the row is read as it now is) instead of letting the shutdown, rethrown through Task.WhenAll, hide every other run's outcome. The row
            // stays non-terminal when the work did not land and the next launch picks it up again (issue #551, DECISION #599).
            _log.Warning("reconciler", row.JobId, nameof(OperationCanceledException));
            var after = await _rows.TryLatestRecordAsync(row.JobId).ConfigureAwait(false);
            return new ReattachOutcome(row.JobId, ActionAfterShutdown(underway.Action, after?.Phase, after?.ErrorCode), after?.Phase, after?.ErrorCode);
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && cancellationToken.IsCancellationRequested))
        {
            // A failing repository or an unforeseen error must not stop the other runs from being reattached; the row is
            // left as it is and the next launch tries again. Only a missing answer from the cloud (network class) is Deferred and keeps the
            // reconnect probe alive; anything else is logged by class and left for the next launch (issue #559).
            if (ex is AccountAuthException auth && NeedsSignIn(auth))
            {
                _log.Warning("reconciler", row.JobId, "account-signed-out");
                SignInNeeded(auth.AccountSub);
                return new ReattachOutcome(row.JobId, ReattachAction.NeedsSignIn, null, auth.Code, auth.AccountSub);
            }

            if (IsNoAnswerException(ex))
            {
                return new ReattachOutcome(row.JobId, ReattachAction.Deferred, null, NoAnswerCode(ex));
            }

            _log.Warning("reconciler", row.JobId, ex.GetType().Name);
            return new ReattachOutcome(row.JobId, ReattachAction.Errored, null, ex.GetType().Name);
        }
    }

    private async Task<ReattachOutcome> DecideAndActAsync(RunRecord row, Underway underway, CancellationToken cancellationToken)
    {
        if (!string.Equals(row.Target, "cloud", StringComparison.OrdinalIgnoreCase))
        {
            return new ReattachOutcome(row.JobId, ReattachAction.Skipped);
        }

        if (row.Phase == JobPhase.Cancelling)
        {
            underway.Action = ReattachAction.CancelFinished;

            // Judged at the start: finishing a cancel deletes VMs (operations of tens of seconds, or a call that never returns), and the pass, and
            // behind it ReconcileOnReconnect's single-flight slot, must not wait on that. A cancel that then finds no connection is recorded as
            // Deferred by ReattachOneAsync, and the probe is armed again when the reattached runs end (ReconcileOnReconnect.Track).
            underway.Judged();
            return await FinishCancelAsync(row, cancellationToken).ConfigureAwait(false);
        }

        if (string.IsNullOrWhiteSpace(row.ProjectId))
        {
            return await FailAsync(row, underway, ReattachAction.FailedUnrecoverable, RunErrorCodes.NoProject, "The run row names no project.", cancellationToken).ConfigureAwait(false);
        }

        var options = RunOptionsJson.TryDeserialize(row.OptionsJson);
        if (options is null || string.IsNullOrWhiteSpace(row.InstallationId))
        {
            return await FailAsync(row, underway, ReattachAction.FailedUnrecoverable, RunErrorCodes.Other, "The run row does not hold the options or installation id needed to resume it.", cancellationToken).ConfigureAwait(false);
        }

        var vmMayExist = row.Phase == JobPhase.Provisioning || JobStateMachine.HasAlreadyPassed(row.Phase, JobPhase.Provisioning);
        var imageRequired = true; // every phase before Provisioning provisions
        if (vmMayExist)
        {
            Evidence evidence;
            try
            {
                evidence = await LookAsync(row, cancellationToken).ConfigureAwait(false);
            }
            catch (CloudOperationException ex) when (IsNoAnswer(ex))
            {
                return new ReattachOutcome(row.JobId, ReattachAction.Deferred, null, ex.Error.Code);
            }
            catch (TimeoutException)
            {
                return new ReattachOutcome(row.JobId, ReattachAction.Deferred, null, "TIMEOUT");
            }

            if (evidence == Evidence.Nothing && row.Phase != JobPhase.Provisioning)
            {
                // Issue #679, reviewed: the same judgement as the live path (ResultWaiter.VmLostAsync). The worker's own status.json comes first: an
                // error code it wrote is the cause. run_time_limit needs real evidence the VM lived to its limit: the worker's last heartbeat, on ITS
                // clock (startedAt to updatedAt), reaching the limit. The row's own start time is NOT evidence: it counts every hour the app was closed
                // after the VM had died. With no such evidence the honest answer is vm_unhealthy.
                var (workerCode, lived, lastAlive) = await ReadWorkerEvidenceAsync(row, cancellationToken).ConfigureAwait(false);
                var limit = TimeSpan.FromMinutes(options.MaxRunDurationMinutes) + CloudJobRequestFactory.VmGrace;
                var code = RunErrorCodes.ForWorkerStatusCode(workerCode)
                    ?? (lived is { } age && age + LimitHeartbeatWindow >= limit ? RunErrorCodes.RunTimeLimit : RunErrorCodes.VmUnhealthy);
                // Issue #109: the VM is gone, so it billed until the last time anything knew it was alive (VmSpan), not until the app came back.
                await _rows.StampVmSpanAsync(row.JobId, null, VmSpan.EndOfGone(row.VmReadyAt, limit, _time.GetUtcNow(), row, lastAlive)).ConfigureAwait(false);
                return await FailAsync(row, underway, ReattachAction.FailedVmMissing, code, "The VM no longer exists and the worker left no result.json.", cancellationToken).ConfigureAwait(false);
            }
            // Only a VM or a result actually SEEN means the run is not provisioned again, so the worker image (which only a new
            // VM's startup script needs) is not required, and an app update that dropped the old image must not strand it.
            // A look that failed is not evidence: a run that could still provision needs its image, or it would create a VM
            // with no startup script that bills until maxRunDuration.
            imageRequired = evidence != Evidence.Found && row.Phase == JobPhase.Provisioning;
        }

        var staged = await FindInputAsync(row, options, cancellationToken).ConfigureAwait(false);
        // An accession with no valid record would make the run provision a VM that fetches nothing, so it fails here even at Provisioning (a file
        // run at Provisioning is left to the VM look above: its copy may be gone because the VM already has it).
        if (staged is null && (!vmMayExist || (options.Accession is not null && imageRequired)))
        {
            return options.Accession is not null
                ? await FailAsync(row, underway, ReattachAction.FailedUnrecoverable, RunErrorCodes.InputAccessionInvalid, "The recorded NCBI accession or range is not valid.", cancellationToken).ConfigureAwait(false)
                : await FailAsync(row, underway, ReattachAction.FailedUnrecoverable, RunErrorCodes.InputMissing, "No copy of the input remains under app data and the original is gone.", cancellationToken).ConfigureAwait(false);
        }

        // Issue #720: a run paired with a GFF3 (#686) keeps the pairing. The same two sources as the FASTA, the app's copy then the original, and the same
        // answer when neither is there and no VM could hold the input already: failing says what is missing, dropping it would run with the worker's own genes.
        var annotation = staged is { IsAccession: false } ? await FindAnnotationAsync(row, options, cancellationToken).ConfigureAwait(false) : null;
        if (annotation is null && staged is { IsAccession: false } && !vmMayExist && !string.IsNullOrWhiteSpace(options.AnnotationPath))
        {
            return await FailAsync(row, underway, ReattachAction.FailedUnrecoverable, RunErrorCodes.InputMissing, "No copy of the paired GFF3 remains under app data and the original is gone.", cancellationToken).ConfigureAwait(false);
        }

        // Hard Rule 2, as JobEngine does for a new run: the pair is checked before anything is created. The original GFF3 may have been edited since the run started
        // (the app's copy is gone), so nobody pays for a VM to learn it no longer fits the FASTA. Once a VM may exist it already holds the uploaded pair.
        if (annotation is not null && !vmMayExist && AnnotationPairValidator.Check(staged!.LocalPath, annotation.LocalPath, options.Topology).Problem is { } pairProblem)
        {
            // The code only: the problem's own text can hold a sequence name from the user's file.
            return await FailAsync(row, underway, ReattachAction.FailedUnrecoverable, RunErrorCodes.InputInvalid, "Annotation " + pairProblem.Code, cancellationToken).ConfigureAwait(false);
        }

        var appVersion = row.AppVersion ?? "0.0.0";
        var image = _images.Resolve(appVersion, VmProvisioner.ExpectsGpu(CloudJobRequestFactory.MachineTypeFor(options.GpuTier)));
        if (imageRequired && (image.Status != WorkerImageStatus.Available || image.Reference is null))
        {
            var code = image.Status == WorkerImageStatus.OverrideRefused ? RunErrorCodes.WorkerImageRefused : RunErrorCodes.WorkerImageUnavailable;
            return await FailAsync(row, underway, ReattachAction.FailedUnrecoverable, code, "No pinned worker image is available for this run's app version.", cancellationToken).ConfigureAwait(false);
        }

        var request = CloudJobRequestFactory.Create(
            options,
            row.JobId,
            ScopeOf(row),
            row.InstallationId,
            appVersion,
            image.Status == WorkerImageStatus.Available ? image.Reference : null,
            annotation is null
                ? [staged ?? new StagedInput(options.InputPath ?? string.Empty, Path.GetFileName(options.InputPath) is { Length: > 0 } name ? name : "input")]
                : [staged!, annotation],
            OutputParent(row, options));

        // Issue #91: a run that was already preempted once is on its on-demand retry; a resume must not ask for Spot again.
        if (row.PreemptionCount > 0)
        {
            request = request with { Spec = request.Spec with { Spot = false } };
        }

        underway.Action = ReattachAction.Resumed;
        underway.Judged();
        var result = await _runner.RunAsync(request, cancellationToken).ConfigureAwait(false);
        return new ReattachOutcome(row.JobId, ReattachAction.Resumed, result.FinalPhase, result.FailureCode);
    }

    /// <summary>What the reattach of one run was doing; read when a user cancel stopped it before it could say.</summary>
    private sealed class Underway(Action onJudged)
    {
        /// <summary>The run's look at the cloud is done and it goes on to be driven: it is no longer waiting to be judged.</summary>
        public void Judged() => onJudged();

        /// <summary>Resumed until a branch says otherwise: a cancel during the first look stops a run that was about to be resumed.</summary>
        public ReattachAction Action { get; set; } = ReattachAction.Resumed;
    }

    /// <summary>
    /// The user cancelled the run while the reattach was driving it. The driver has stopped, but the canceller writes its phases after that,
    /// so wait for that cancel to end (it signals through <see cref="ActiveRuns"/>, success or failure; no polling) and report the row as it
    /// then is. A cancel that failed leaves the row non-terminal, and says so. The wait ends with the app's shutdown.
    /// </summary>
    private async Task<ReattachOutcome> OutcomeOfCancelledAsync(string jobId, ReattachAction underway, CancellationToken cancellationToken)
    {
        try
        {
            var settle = _active.WhenCancelSettledAsync(jobId).WaitAsync(_cancelSettleTimeout, _time, cancellationToken);
            CancelSettleArmed?.Invoke(jobId);
            await settle.ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // The cancel is still going: report the row as it is now (non-terminal), which says so, rather than wait on it for good.
            _log.Warning("reconciler", jobId, nameof(TimeoutException));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The app is shutting down while the cancel is in flight: the same report, not an exception that would hide every other run's outcome.
            _log.Warning("reconciler", jobId, nameof(OperationCanceledException));
        }

        var settled = await _rows.TryLatestRecordAsync(jobId).ConfigureAwait(false);
        return new ReattachOutcome(jobId, ActionAfterUserCancel(underway, settled?.Phase, settled?.ErrorCode), settled?.Phase, settled?.ErrorCode);
    }

    /// <summary>
    /// The action for a reattach a user cancel took over. A terminal row is judged as <see cref="ActionForFinalPhase"/> does. A row that is
    /// not terminal means the cancel did not end the run (it was cut short by the shutdown or the settle deadline, or it failed): the reattach
    /// is no longer driving the run, so it is never reported as resumed, only as <see cref="ReattachAction.CancelInterrupted"/>.
    /// </summary>
    /// <summary>
    /// The action for a reattach the app's shutdown cut short. One that was finishing a cancel is judged as a cancel a user cancel took over is
    /// (<see cref="ActionAfterUserCancel"/>). Any other is <see cref="ReattachAction.Resumed"/> (a terminal row judged by <see cref="ActionForFinalPhase"/>): it
    /// was handed back to the runner, and <see cref="ReattachOutcome.FinalPhase"/> says the row is still non-terminal for the next launch to resume.
    /// </summary>
    private static ReattachAction ActionAfterShutdown(ReattachAction underway, JobPhase? finalPhase, string? errorCode)
        => underway == ReattachAction.CancelFinished
            ? ActionAfterUserCancel(underway, finalPhase, errorCode)
            : finalPhase is { } phase && JobStateMachine.IsTerminal(phase) ? ActionForFinalPhase(underway, phase, errorCode) : ReattachAction.Resumed;

    private static ReattachAction ActionAfterUserCancel(ReattachAction underway, JobPhase? finalPhase, string? errorCode)
        => finalPhase is { } phase && JobStateMachine.IsTerminal(phase) ? ActionForFinalPhase(underway, phase, errorCode) : ReattachAction.CancelInterrupted;

    /// <summary>
    /// What the reattach did, as the row it left says it did. A reattach that was stopped by a user cancel may have set its own action (it
    /// goes first, before its write) for a write that never landed: a row that ended Cancelled was cancelled, and a Failed* action is only true
    /// of a row that is Failed. A row that is Failed because a cancel could not confirm the VM gone (<see cref="RunErrorCodes.CancelFailed"/>, or any
    /// Failed row the reattach reached while finishing a cancel) is <see cref="ReattachAction.CancelFailed"/>, whatever the reattach was doing.
    /// </summary>
    internal static ReattachAction ActionForFinalPhase(ReattachAction underway, JobPhase? finalPhase, string? errorCode = null)
    {
        if (finalPhase == JobPhase.Cancelled)
        {
            return ReattachAction.CancelFinished;
        }

        if (finalPhase == JobPhase.Failed && (RunErrorCodes.IsUnconfirmedCancel(errorCode) || underway == ReattachAction.CancelFinished))
        {
            return ReattachAction.CancelFailed;
        }

        if (underway == ReattachAction.CancelFinished && (finalPhase is null || !JobStateMachine.IsTerminal(finalPhase.Value)))
        {
            // The reattach was finishing a cancel and the row is not terminal (still Cancelling, or unreadable): a finished cancel was not seen.
            return ReattachAction.CancelInterrupted;
        }

        return underway is ReattachAction.FailedVmMissing or ReattachAction.FailedUnrecoverable && finalPhase != JobPhase.Failed
            ? ReattachAction.Resumed
            : underway;
    }

    /// <summary>
    /// How far before a VM's limit its last heartbeat may sit and still count as "lived to its limit": the worker starts after the VM boots (up to the
    /// 8 minute boot deadline) and beats every 30 s, so the worker-clock age at the last heartbeat trails the VM's own age by about this much.
    /// THEORY (unverified, no live project): ToTest measures the real gap.
    /// </summary>
    private static readonly TimeSpan LimitHeartbeatWindow = TimeSpan.FromMinutes(10);

    /// <summary>
    /// What the worker's <c>status.json</c> says about a VM that is gone: its error code (if any) and how long the VM had lived at the last heartbeat
    /// (<c>updatedAt</c> minus <c>startedAt</c>, falling back to <c>stageStartedAt</c>; one clock, the worker's). Anything unreadable is no evidence.
    /// </summary>
    private async Task<(string? WorkerCode, TimeSpan? Lived, DateTimeOffset? LastAlive)> ReadWorkerEvidenceAsync(RunRecord row, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(row.Bucket))
        {
            return (null, null, null);
        }

        try
        {
            var bucket = row.Bucket;
            var stream = await Lookup(token => _storage.TryDownloadAsync(ScopeOf(row), bucket, WorkerManifestBuilder.JobPrefix(row.JobId) + "status.json", token), cancellationToken).ConfigureAwait(false);
            if (stream is null)
            {
                return (null, null, null);
            }

            string text;
            await using (stream.ConfigureAwait(false))
            {
                using var reader = new StreamReader(stream);
                text = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            }

            var status = WorkerStatusReader.TryParseStatus(text);
            TimeSpan? lived = null;
            DateTimeOffset? lastWrite = null;
            if (status is not null && DateTimeOffset.TryParse(status.UpdatedAt, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var wrote))
            {
                lastWrite = wrote;
            }

            if (status is not null
                && lastWrite is { } written
                && DateTimeOffset.TryParse(status.StartedAt ?? status.StageStartedAt, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var began)
                && written >= began)
            {
                lived = written - began;
            }

            return (WorkerResultReader.TryReadStatusErrorCode(text), lived, lastWrite);
        }
        catch (Exception ex) when (ex is TimeoutException or CloudOperationException or IOException)
        {
            return (null, null, null); // A diagnostic read: no answer is no evidence, never a reason to leave the run unjudged.
        }
    }
    private enum Evidence
    {
        /// <summary>No VM carries the job's label and no result.json exists.</summary>
        Nothing,

        /// <summary>A VM carries the job's label, or result.json exists (the run has something to adopt or download).</summary>
        Found,

        /// <summary>The look was refused (billing, permission, org policy): neither a VM nor a result was seen. The runner judges it.</summary>
        Unknown,
    }

    /// <summary>
    /// Looks at the cloud once: a VM by job-id label, then (only when none) <c>result.json</c>. Throws a
    /// <see cref="CloudOperationException"/> the caller classifies, or <see cref="TimeoutException"/>. An error that is not "no answer"
    /// (billing, permission) is reported as <see cref="Evidence.Unknown"/> so the runner, which knows how to end a VM and name the
    /// failure, judges the run instead of this class guessing. A row with no recorded bucket never uploaded, so it has no result:
    /// nothing is created to look.
    /// </summary>
    private async Task<Evidence> LookAsync(RunRecord row, CancellationToken cancellationToken)
    {
        try
        {
            var vms = await Lookup(token => _compute.FindByJobIdAsync(ScopeOf(row), row.JobId, token), cancellationToken).ConfigureAwait(false);
            if (vms.Count > 0)
            {
                return Evidence.Found;
            }

            if (string.IsNullOrWhiteSpace(row.Bucket))
            {
                return Evidence.Nothing;
            }

            var bucket = row.Bucket;
            var stream = await Lookup(token => _storage.TryDownloadAsync(ScopeOf(row), bucket, WorkerManifestBuilder.JobPrefix(row.JobId) + "result.json", token), cancellationToken).ConfigureAwait(false);
            if (stream is null)
            {
                return Evidence.Nothing;
            }

            await stream.DisposeAsync().ConfigureAwait(false);
            return Evidence.Found;
        }
        catch (CloudOperationException ex) when (!IsNoAnswer(ex))
        {
            return Evidence.Unknown;
        }
    }

    private Task<T> Lookup<T>(Func<CancellationToken, Task<T>> call, CancellationToken cancellationToken)
        => WithDeadline(call, _lookupTimeout, cancellationToken);

    private async Task<T> WithDeadline<T>(Func<CancellationToken, Task<T>> call, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var deadline = new Audit.DeadlineSource(cancellationToken, timeout, _time);
        Task<T>? task = null;
        try
        {
            task = Audit.CloudAuditContext.StartUnderDeadline(cancellationToken, deadline.Token, call);
            return await task.WaitAsync(timeout, _time, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await Audit.CloudAuditContext.StopAbandonedAsync(deadline.Source, task).ConfigureAwait(false);
            throw new TimeoutException();
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            // WaitAsync's own timer can beat the linked deadline (a TimeoutException), and a caller's cancel can unwind the wait before the link passes it on:
            // either way the abandoned call is told to stop now, so it ends (and is audited) at once instead of running on unaware.
            await Audit.CloudAuditContext.StopAbandonedAsync(deadline.Source, task).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>The cloud gave no answer about the run: nothing is connected, or the network is down. That says nothing about the run itself.</summary>
    private static bool IsNoAnswerException(Exception ex)
        => ex is TimeoutException || (ex is CloudOperationException cloud && IsNoAnswer(cloud)) || (ex is AccountAuthException auth && !NeedsSignIn(auth));

    /// <summary>Issue #830 review M2: the one account failure whose action is Sign in (the stored sign-in was refused or is gone). Every other <see cref="AccountAuthException"/> (no connection, proxy, locked or unreadable accounts file, storage, a failed token answer) is a no-answer the reconnect probe retries.</summary>
    private static bool NeedsSignIn(AccountAuthException ex) => ex.Code == AuthErrorCodes.SigninExpired;

    private static string? NoAnswerCode(Exception ex) => ex switch
    {
        CloudOperationException cloud => cloud.Error.Code,
        AccountAuthException auth => auth.Code,
        _ => "TIMEOUT",
    };

    private static bool IsNoAnswer(CloudOperationException ex)
        => ex.Error.Code == RunErrorCodes.NotConnectedGatewayCode || ex.Kind == CloudErrorKind.Network;

    private async Task<StagedInput?> FindInputAsync(RunRecord row, RunOptions options, CancellationToken cancellationToken)
    {
        if (options.Accession is not null)
        {
            // Issue #684: an accession is fetched by the worker; there is no copy to find and nothing to upload. The row is a file on disk
            // that outlives the app, so what it recorded is checked again with the same rules as a new run (Hard Rule 2) before it can reach the cloud.
            var accession = AccessionInput.Check(options.Accession, options.AccessionRange);
            return accession.Problem is null ? StagedInput.ForAccession(accession.Accession, accession.Range) : null;
        }

        try
        {
            var copy = await _inputs.FindAsync(row.JobId, cancellationToken).ConfigureAwait(false);
            if (copy is not null)
            {
                return copy;
            }

            // Hard Rule 14 keeps a copy of every input; a missing one falls back to the original, if it is still where it was.
            return string.IsNullOrWhiteSpace(options.InputPath) ? null : await _inputs.StageAsync(row.JobId, options.InputPath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>The run's paired GFF3 (issue #720): the app's copy, else the original it was paired from, else null (an unpaired run, or both gone).</summary>
    private async Task<StagedInput?> FindAnnotationAsync(RunRecord row, RunOptions options, CancellationToken cancellationToken)
    {
        try
        {
            var copy = await _inputs.FindAnnotationAsync(row.JobId, cancellationToken).ConfigureAwait(false);
            if (copy is not null)
            {
                return copy;
            }

            return string.IsNullOrWhiteSpace(options.AnnotationPath) ? null : await _inputs.StageAnnotationAsync(row.JobId, options.AnnotationPath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private string OutputParent(RunRecord row, RunOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.OutputFolder))
        {
            return options.OutputFolder;
        }

        var existing = string.IsNullOrWhiteSpace(row.OutputDir) ? null : Path.GetDirectoryName(row.OutputDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return string.IsNullOrWhiteSpace(existing) ? RunOutputFolders.DefaultParent(_downloadsFolder) : existing;
    }

    private async Task<ReattachOutcome> FinishCancelAsync(RunRecord row, CancellationToken cancellationToken)
    {
        try
        {
            await _runner.CancelAsync(ScopeOf(row), row.JobId, cancellationToken).ConfigureAwait(false);
        }
        catch (CloudOperationException ex) when (IsNoAnswer(ex))
        {
            return new ReattachOutcome(row.JobId, ReattachAction.Deferred, null, ex.Error.Code);
        }

        var after = await _rows.TryLatestRecordAsync(row.JobId).ConfigureAwait(false);
        return new ReattachOutcome(row.JobId, ActionForFinalPhase(ReattachAction.CancelFinished, after?.Phase, after?.ErrorCode), after?.Phase, after?.ErrorCode);
    }

    private async Task<ReattachOutcome> FailAsync(RunRecord row, Underway underway, ReattachAction action, string code, string detail, CancellationToken cancellationToken)
    {
        underway.Action = action;
        await _rows.SetPhaseAsync(row.JobId, JobPhase.Failed, cancellationToken, code, detail).ConfigureAwait(false);
        return new ReattachOutcome(row.JobId, action, JobPhase.Failed, code);
    }
}
