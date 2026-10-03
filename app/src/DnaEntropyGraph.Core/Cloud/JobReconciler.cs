using System.Collections.Concurrent;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Contract;
using DnaEntropyGraph.Core.Runs;

namespace DnaEntropyGraph.Core.Cloud;

/// <summary>What the reconciler did with one run row.</summary>
public enum ReattachAction
{
    /// <summary>The run was handed back to <see cref="CloudJobRunner"/>, which carried it to the phase in <see cref="ReattachOutcome.FinalPhase"/>.</summary>
    Resumed,

    /// <summary>The run was mid-cancel; the cancel was finished.</summary>
    CancelFinished,

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
}

/// <summary>One row's outcome. <paramref name="FinalPhase"/> and <paramref name="ErrorCode"/> are what the row ended as, when it ended.</summary>
public sealed record ReattachOutcome(string JobId, ReattachAction Action, JobPhase? FinalPhase = null, string? ErrorCode = null);

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
}

/// <summary>One VM the lifecycle enforcement acted on, or could not. A VM it looked at and left alone has no outcome.</summary>
public sealed record LifecycleOutcome(string JobId, string VmName, LifecycleAction Action, string? ErrorCode = null);

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
    private readonly IDiagnosticsLog _log;

    /// <summary>Jobs whose last reattach could not ask the cloud (Deferred), until a later pass judges them (issue #559).</summary>
    private readonly ConcurrentDictionary<string, byte> _deferredRuns = new();

    /// <summary>True when the last lifecycle enforcement had a row or VM it could not ask the cloud about.</summary>
    private volatile bool _lifecycleDeferred;

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

    /// <summary>Records or clears a run's deferred mark unless a newer reattach pass has started since <paramref name="generation"/> began.</summary>
    private void SetRunDeferred(long generation, string jobId, bool deferred)
    {
        lock (_generationGate)
        {
            if (generation != _reattachGeneration)
            {
                return;
            }

            if (deferred)
            {
                _deferredRuns[jobId] = 0;
            }
            else
            {
                _deferredRuns.TryRemove(jobId, out _);
            }
        }
    }

    /// <summary>
    /// How far back a finished run's VM is looked up by its job-id label. Older leaks are caught by the idle sweep, which lists by installation and
    /// so costs one call however long the history is; this keeps a launch from making one lookup per run row ever recorded.
    /// </summary>
    private static readonly TimeSpan LifecycleLookback = TimeSpan.FromDays(14);

    /// <param name="activeRuns">The registry the engine's Cancel looks in. Required: a private one would let a cancel miss a reattached run.</param>
    /// <param name="settings">Where the installation id (which VMs are ours, Hard Rule 9) and the idle-VM limit are read, on every pass so a changed setting applies at once.</param>
    /// <param name="onPhaseChanged">Told after the reconciler itself commits a phase (a run it fails); a run it resumes reports through the runner's own callback.</param>
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
        IDiagnosticsLog? log = null)
    {
        _log = log ?? NullDiagnosticsLog.Instance;
        _lookupTimeout = lookupTimeout ?? DefaultLookupTimeout;
        _mutationTimeout = mutationTimeout ?? DefaultMutationTimeout;
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
    public bool HasDeferred => _lifecycleDeferred || !_deferredRuns.IsEmpty;

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

        await SweepIdleAsync(installation, now, outcomes, cancellationToken).ConfigureAwait(false);
        bool deferred;
        lock (outcomes)
        {
            deferred = outcomes.Any(o => o.Action == LifecycleAction.Deferred);
        }

        SetLifecycleDeferred(generation, deferred);

        return outcomes;
    }

    /// <summary>
    /// One finished run. Whatever goes wrong here is this row's alone: a cloud answer that does not come, a refusal, or an error nobody
    /// foresaw (recorded by its class, never its message, which could carry a file name) all end THIS row and the pass goes on to the next.
    /// </summary>
    private async Task EnforceRowAsync(RunRecord row, string installation, DateTimeOffset now, List<LifecycleOutcome> outcomes, CancellationToken cancellationToken)
    {
        try
        {
            var driver = _active.TryStart(row.JobId, async token =>
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, cancellationToken);
                IReadOnlyList<VmDescriptor> vms;
                try
                {
                    vms = await Lookup(t => _compute.FindByJobIdAsync(row.JobId, t), linked.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is CloudOperationException or TimeoutException)
                {
                    Record(outcomes, row.JobId, string.Empty, ex);
                    return;
                }

                var options = RunOptionsJson.TryDeserialize(row.OptionsJson);
                foreach (var vm in vms)
                {
                    var want = WantedByLifecycle(row, vm, options, installation, now);
                    if (want == VmWant.Leave)
                    {
                        continue;
                    }

                    await ApplyAsync(row.JobId, vm, want == VmWant.Delete, want == VmWant.Delete ? LifecycleAction.VmDeleted : LifecycleAction.VmStopped, outcomes, linked.Token).ConfigureAwait(false);
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

    private enum VmWant
    {
        Leave,
        Stop,
        Delete,
    }

    /// <summary>What a finished run's VM should be now, from its lifecycle label (or, when the gateway did not read labels, the run's own options).</summary>
    private static VmWant WantedByLifecycle(RunRecord row, VmDescriptor vm, RunOptions? options, string installation, DateTimeOffset now)
    {
        if (!IsOurs(vm, installation))
        {
            return VmWant.Leave;
        }

        var lifecycle = vm.Labels is not null && vm.Labels.TryGetValue("lifecycle", out var label)
            ? label
            : options is null ? null : CloudJobRequestFactory.LifecycleLabel(options.AfterTask);
        switch (lifecycle)
        {
            case "delete":
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

    /// <summary>Hard Rule 9: a VM whose labels were read must carry our app label and this installation's id. Labels not read say nothing against it (its job id already matched a row of this installation).</summary>
    private static bool IsOurs(VmDescriptor vm, string installation)
        => vm.Labels is null
            || (vm.Labels.TryGetValue("app", out var app) && app == VmSpec.AppLabelValue
                && vm.Labels.TryGetValue("installation-id", out var owner) && owner == installation);

    private async Task SweepIdleAsync(string installation, DateTimeOffset now, List<LifecycleOutcome> outcomes, CancellationToken cancellationToken)
    {
        var limit = CloudHousekeepingSettings.IdleStoppedVmLimit(_settings);

        IReadOnlyList<VmDescriptor> listed;
        try
        {
            listed = await Lookup(token => _compute.ListByInstallationAsync(installation, token), cancellationToken).ConfigureAwait(false);
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
                        fresh = await Lookup(t => _compute.GetVmAsync(vm.Name, vm.Zone, t), linked.Token).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is CloudOperationException or TimeoutException)
                    {
                        Record(outcomes, jobId, vm.Name, ex);
                        return;
                    }

                    if (fresh is not null && IsIdle(fresh, now, limit))
                    {
                        await ApplyAsync(jobId, vm, true, LifecycleAction.IdleVmDeleted, outcomes, linked.Token).ConfigureAwait(false);
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
    private async Task ApplyAsync(string jobId, VmDescriptor vm, bool delete, LifecycleAction done, List<LifecycleOutcome> outcomes, CancellationToken cancellationToken)
    {
        try
        {
            await WithDeadline(
                async token =>
                {
                    try
                    {
                        await (delete ? _compute.DeleteVmAsync(vm.Name, vm.Zone, token) : _compute.StopVmAsync(vm.Name, vm.Zone, token)).ConfigureAwait(false);
                    }
                    catch (CloudOperationException ex) when (ex.Error.HttpStatus == 404)
                    {
                    }

                    return true;
                },
                _mutationTimeout,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is CloudOperationException or TimeoutException)
        {
            Record(outcomes, jobId, vm.Name, ex);
            return;
        }

        lock (outcomes)
        {
            outcomes.Add(new LifecycleOutcome(jobId, vm.Name, done));
        }
    }

    /// <summary>
    /// Records why a call did not answer: no answer (offline, a deadline) is <see cref="LifecycleAction.Deferred"/>, a refusal (permission, org
    /// policy) is <see cref="LifecycleAction.Failed"/>. Either is that row's or VM's alone: the pass goes on with the rest.
    /// </summary>
    private static void Record(List<LifecycleOutcome> outcomes, string jobId, string vmName, Exception ex)
    {
        var noAnswer = IsNoAnswerException(ex);
        var code = ex is CloudOperationException c ? c.Error.Code : "TIMEOUT";
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
            outcomes.Add(new LifecycleOutcome(jobId, vmName, LifecycleAction.Failed, ex.GetType().Name));
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
        var all = await _runs.GetAllAsync(cancellationToken).ConfigureAwait(false);
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
            SetRunDeferred(generation, stale, deferred: false);
        }

        var judgements = candidates.Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToList();
        var tasks = candidates.Select((row, i) => ReattachOneAsync(row, judgements[i], generation, cancellationToken)).ToList();
        return (Task.WhenAll(judgements.Select(j => j.Task)), WhenAllOutcomesAsync(tasks));
    }

    private static async Task<IReadOnlyList<ReattachOutcome>> WhenAllOutcomesAsync(List<Task<ReattachOutcome>> tasks)
        => await Task.WhenAll(tasks).ConfigureAwait(false);

    private async Task<ReattachOutcome> ReattachOneAsync(RunRecord row, TaskCompletionSource judged, long generation, CancellationToken cancellationToken)
    {
        try
        {
            var outcome = await ReattachCoreAsync(row, judged, generation, cancellationToken).ConfigureAwait(false);
            SetRunDeferred(generation, row.JobId, outcome.Action == ReattachAction.Deferred);
            return outcome;
        }
        finally
        {
            judged.TrySetResult();
        }
    }

    private async Task<ReattachOutcome> ReattachCoreAsync(RunRecord row, TaskCompletionSource judged, long generation, CancellationToken cancellationToken)
    {
        try
        {
            // Registered BEFORE the first look at the cloud, and covering every write the reattach can make (the look, a failure it records,
            // a cancel it finishes, the run itself): a cancel at any moment finds the driver, stops it and waits for it, so one writer at a time.
            ReattachOutcome? outcome = null;
            var underway = new Underway(() =>
            {
                SetRunDeferred(generation, row.JobId, deferred: false);
                judged.TrySetResult();
            });
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
        catch (Exception ex) when (!(ex is OperationCanceledException && cancellationToken.IsCancellationRequested))
        {
            // A failing repository or an unforeseen error must not stop the other runs from being reattached; the row is
            // left as it is and the next launch tries again. Only a missing answer from the cloud (network class) is Deferred and keeps the
            // reconnect probe alive; anything else is logged by class and left for the next launch (issue #559).
            if (IsNoAnswerException(ex))
            {
                return new ReattachOutcome(row.JobId, ReattachAction.Deferred, null, ex is CloudOperationException c ? c.Error.Code : "TIMEOUT");
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
                return await FailAsync(row, underway, ReattachAction.FailedVmMissing, RunErrorCodes.VmUnhealthy, "The VM no longer exists and the worker left no result.json.", cancellationToken).ConfigureAwait(false);
            }

            // Only a VM or a result actually SEEN means the run is not provisioned again, so the worker image (which only a new
            // VM's startup script needs) is not required, and an app update that dropped the old image must not strand it.
            // A look that failed is not evidence: a run that could still provision needs its image, or it would create a VM
            // with no startup script that bills until maxRunDuration.
            imageRequired = evidence != Evidence.Found && row.Phase == JobPhase.Provisioning;
        }

        var staged = await FindInputAsync(row, options, cancellationToken).ConfigureAwait(false);
        if (staged is null && !vmMayExist)
        {
            return await FailAsync(row, underway, ReattachAction.FailedUnrecoverable, RunErrorCodes.InputMissing, "No copy of the input remains under app data and the original is gone.", cancellationToken).ConfigureAwait(false);
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
            row.ProjectId,
            row.InstallationId,
            appVersion,
            image.Status == WorkerImageStatus.Available ? image.Reference : null,
            [staged ?? new StagedInput(options.InputPath ?? string.Empty, Path.GetFileName(options.InputPath) is { Length: > 0 } name ? name : "input")],
            OutputParent(row, options));

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
        await _active.WhenCancelSettledAsync(jobId).WaitAsync(cancellationToken).ConfigureAwait(false);
        var settled = await _rows.TryLatestRecordAsync(jobId).ConfigureAwait(false);
        return new ReattachOutcome(jobId, underway, settled?.Phase, settled?.ErrorCode);
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
            var vms = await Lookup(token => _compute.FindByJobIdAsync(row.JobId, token), cancellationToken).ConfigureAwait(false);
            if (vms.Count > 0)
            {
                return Evidence.Found;
            }

            if (string.IsNullOrWhiteSpace(row.Bucket))
            {
                return Evidence.Nothing;
            }

            var bucket = row.Bucket;
            var stream = await Lookup(token => _storage.TryDownloadAsync(bucket, WorkerManifestBuilder.JobPrefix(row.JobId) + "result.json", token), cancellationToken).ConfigureAwait(false);
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

    private static async Task<T> WithDeadline<T>(Func<CancellationToken, Task<T>> call, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            return await call(deadline.Token).WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException();
        }
    }

    /// <summary>The cloud gave no answer about the run: nothing is connected, or the network is down. That says nothing about the run itself.</summary>
    private static bool IsNoAnswerException(Exception ex)
        => ex is TimeoutException || (ex is CloudOperationException cloud && IsNoAnswer(cloud));

    private static bool IsNoAnswer(CloudOperationException ex)
        => ex.Error.Code == RunErrorCodes.NotConnectedGatewayCode || ex.Kind == CloudErrorKind.Network;

    private async Task<StagedInput?> FindInputAsync(RunRecord row, RunOptions options, CancellationToken cancellationToken)
    {
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
            await _runner.CancelAsync(row.JobId, cancellationToken).ConfigureAwait(false);
        }
        catch (CloudOperationException ex) when (IsNoAnswer(ex))
        {
            return new ReattachOutcome(row.JobId, ReattachAction.Deferred, null, ex.Error.Code);
        }

        var after = await _rows.TryLatestRecordAsync(row.JobId).ConfigureAwait(false);
        return new ReattachOutcome(row.JobId, ReattachAction.CancelFinished, after?.Phase, after?.ErrorCode);
    }

    private async Task<ReattachOutcome> FailAsync(RunRecord row, Underway underway, ReattachAction action, string code, string detail, CancellationToken cancellationToken)
    {
        underway.Action = action;
        await _rows.SetPhaseAsync(row.JobId, JobPhase.Failed, cancellationToken, code, detail).ConfigureAwait(false);
        return new ReattachOutcome(row.JobId, action, JobPhase.Failed, code);
    }
}
