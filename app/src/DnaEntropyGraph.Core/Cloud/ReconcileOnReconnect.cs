using DnaEntropyGraph.Core.Abstractions;

namespace DnaEntropyGraph.Core.Cloud;

/// <summary>
/// Issue #530: the observer the resilience pipeline reports to, with one addition. It forwards everything to the observer it wraps (the
/// offline banner's source) and, when the connection comes back (<c>OnConnectivityChanged(offline: false)</c>), runs the reconciler again,
/// so a run it could not judge while offline (a Deferred row) is judged now, not at the next launch. It never double-drives a run: the
/// reconciler registers every run it touches in <see cref="ActiveRuns"/>, so a run the engine or an earlier pass owns is skipped. Passes are
/// single-flight: reconnects while one runs coalesce into at most one follow-up pass.
/// A pass ends once the lifecycle and the idle sweep have run and the reattached runs have been handed to their own drivers; it does not wait for those
/// runs to end, so one long run never holds every later reconnect back.
/// Issue #559: the pipeline's connectivity event is rare (every reconciler call bypasses the breaker that raises it), so this class also runs a
/// reconnect probe. While <c>hasDeferred</c> says a run or VM could not be judged, it runs the same pass again on a bounded, doubling backoff
/// (timed on a <see cref="TimeProvider"/>, so tests move virtual time) and stops once nothing is deferred. A probe pass is an ordinary pass:
/// single-flight with reconnect passes, and it tells the wrapped observer nothing (the offline banner hears only the pipeline).
/// </summary>
public sealed class ReconcileOnReconnect : ICloudCallObserver, IDisposable
{
    /// <summary>The wait before the first probe pass after a pass left something deferred.</summary>
    public static readonly TimeSpan DefaultProbeInitialDelay = TimeSpan.FromSeconds(30);

    /// <summary>The longest wait between probe passes: a laptop offline for a day makes one pass every five minutes, not more.</summary>
    public static readonly TimeSpan DefaultProbeMaxDelay = TimeSpan.FromMinutes(5);

    private readonly ICloudCallObserver _inner;
    private readonly Func<CancellationToken, Task<Task>> _pass;
    private readonly Func<bool> _hasDeferred;
    private readonly IDiagnosticsLog _log;
    private readonly TimeProvider _time;
    private readonly TimeSpan _probeInitialDelay;
    private readonly TimeSpan _probeMaxDelay;
    private readonly object _gate = new();
    private Task _passes = Task.CompletedTask;
    private Task _background = Task.CompletedTask;
    private Task _probeFollowUps = Task.CompletedTask;
    private bool _running;
    private bool _again;
    private bool _probing;
    private bool _disposed;
    private TimeSpan _probeDelay;
    private ITimer? _probeTimer;

    /// <param name="reconciler">Resolved on the first reconnect, not at construction: the reconciler needs the gateways, and the gateways need this observer.</param>
    /// <param name="log">Told when a pass throws (its error class only). Null records nothing.</param>
    /// <param name="time">Times the probe. Null is the system clock.</param>
    public ReconcileOnReconnect(
        ICloudCallObserver inner,
        Func<JobReconciler> reconciler,
        IDiagnosticsLog? log = null,
        TimeProvider? time = null,
        TimeSpan? probeInitialDelay = null,
        TimeSpan? probeMaxDelay = null)
        : this(inner, token => reconciler().BeginReconcileAsync(token), () => reconciler().HasDeferred, log, time, probeInitialDelay, probeMaxDelay)
    {
    }

    /// <param name="pass">
    /// One reconcile pass, run on each reconnect. Its outer task is the pass (single-flight: the next pass waits for it); its result is a
    /// task for the runs the pass reattached, which keep going on their own and never hold a later pass back.
    /// </param>
    /// <param name="log">Told when a pass throws (its error class only). Null records nothing.</param>
    public ReconcileOnReconnect(ICloudCallObserver inner, Func<CancellationToken, Task<Task>> pass, IDiagnosticsLog? log = null)
        : this(inner, pass, static () => false, log, null, null, null)
    {
    }

    /// <param name="pass">As above.</param>
    /// <param name="hasDeferred">True while some run or VM the last passes could not judge is still waiting; the probe runs only while it is.</param>
    public ReconcileOnReconnect(
        ICloudCallObserver inner,
        Func<CancellationToken, Task<Task>> pass,
        Func<bool> hasDeferred,
        IDiagnosticsLog? log = null,
        TimeProvider? time = null,
        TimeSpan? probeInitialDelay = null,
        TimeSpan? probeMaxDelay = null)
    {
        _inner = inner;
        _pass = pass;
        _hasDeferred = hasDeferred;
        _log = log ?? NullDiagnosticsLog.Instance;
        _time = time ?? TimeProvider.System;
        _probeInitialDelay = probeInitialDelay ?? DefaultProbeInitialDelay;
        _probeMaxDelay = probeMaxDelay ?? DefaultProbeMaxDelay;
    }

    public void OnRetry(CloudRetryEvent retry) => _inner.OnRetry(retry);

    public void OnTokenRefreshed(string operation) => _inner.OnTokenRefreshed(operation);

    public void OnConnectivityChanged(bool offline)
    {
        _inner.OnConnectivityChanged(offline);
        if (offline)
        {
            return;
        }

        StartPass();
    }

    /// <summary>
    /// Starts a pass, or, when one runs, asks for one follow-up after it (single flight: two passes never overlap, and a run one pass released
    /// is never re-driven by another at the same moment). Returns the task that ends when the passes now owed have ended.
    /// </summary>
    private Task StartPass()
    {
        lock (_gate)
        {
            if (_running)
            {
                _again = true;
                return _passes;
            }

            _running = true;
            _passes = Task.Run(RunPassesAsync);
            return _passes;
        }
    }

    private async Task RunPassesAsync()
    {
        do
        {
            try
            {
                var reattached = await _pass(CancellationToken.None).ConfigureAwait(false);
                Track(reattached);
            }
            catch (Exception ex)
            {
                // Called from inside a cloud call's own bookkeeping: nothing here may fault it. A row not judged now stays
                // non-terminal and is looked at on the next reconnect, probe or launch; the class of the error is the only trace.
                Warn("reconcile-on-reconnect", ex);
            }
        }
        while (TakeFollowUp());
        StartProbeIfDeferred();
    }

    /// <summary>
    /// Starts the reconnect probe when something is deferred and none runs. Called after every pass this observer runs; the app calls it once
    /// after the launch pass, which it runs directly. Idempotent.
    /// </summary>
    public void StartProbeIfDeferred() => ArmProbe(escalate: false);

    private void ArmProbe(bool escalate)
    {
        bool deferred;
        var keepDelay = false;
        try
        {
            deferred = _hasDeferred();
        }
        catch (Exception ex)
        {
            // The check itself failed (the reconciler could not be resolved, say): that is not an answer, so the probe goes on, on the delay it
            // had (not doubled: nothing was learned), rather than stop for good and leave a deferred run to the next launch.
            Warn("reconcile-probe", ex);
            deferred = true;
            keepDelay = escalate;
        }

        if (!deferred)
        {
            return;
        }

        lock (_gate)
        {
            if (_probing || _disposed)
            {
                return;
            }

            _probing = true;
            _probeDelay = keepDelay ? _probeDelay : escalate ? TimeSpan.FromTicks(Math.Min(_probeDelay.Ticks * 2, _probeMaxDelay.Ticks)) : _probeInitialDelay;
            _probeTimer?.Dispose();
            _probeTimer = _time.CreateTimer(_ => OnProbeDue(), null, _probeDelay, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Tells the log the error's class. Called from inside a cloud call's bookkeeping and from continuations nobody awaits: a log that throws must not fault either.</summary>
    private void Warn(string source, Exception error)
    {
        try
        {
            _log.Warning(source, null, error.GetType().Name);
        }
        catch (Exception)
        {
            // The log is broken: there is nothing left to tell.
        }
    }

    private void OnProbeDue()
    {
        Task passes;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
        }

        // Registered synchronously (StartPass sets the pass task before it returns), so WhenIdleAsync sees a probe pass the moment its timer fires.
        passes = StartPass();
        // Held under the gate with the probe state, so a WhenIdleAsync caller sees this follow-up the moment the pass it follows is visible.
        lock (_gate)
        {
            var followUp = passes.ContinueWith(
                _ =>
                {
                    // Never throws: a fault kept in _probeFollowUps would make every later WhenIdleAsync throw. A failure here (the timer could
                    // not be created, the log itself threw) is logged by class if the log still works, and the probe simply is not re-armed.
                    try
                    {
                        lock (_gate)
                        {
                            _probing = false;
                        }

                        // Still deferred: wait twice as long (up to the cap) for the next pass. Nothing deferred: the probe ends and the delay starts over.
                        ArmProbe(escalate: true);
                    }
                    catch (Exception ex)
                    {
                        Warn("reconcile-probe", ex);
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            _probeFollowUps = _probeFollowUps.IsCompleted ? followUp : Task.WhenAll(_probeFollowUps, followUp);
        }
    }

    /// <summary>Remembers the runs a pass reattached, so <see cref="WhenIdleAsync"/> can wait for them; their own failure is logged, never thrown.</summary>
    private void Track(Task reattached)
    {
        var watched = reattached.ContinueWith(
            t =>
            {
                if (t.IsFaulted)
                {
                    Warn("reconcile-on-reconnect", t.Exception!.GetBaseException());
                }

                // A run judged at the start of its own work (a cancel being finished) can end Deferred after the pass that started it ended: look again.
                StartProbeIfDeferred();
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        lock (_gate)
        {
            _background = Task.WhenAll(_background, watched);
        }
    }

    /// <summary>True when a reconnect arrived during the pass that just ended (and clears it); otherwise marks the observer idle.</summary>
    private bool TakeFollowUp()
    {
        lock (_gate)
        {
            if (_again)
            {
                _again = false;
                return true;
            }

            _running = false;
            return false;
        }
    }

    /// <summary>
    /// Completes when every reconcile pass this observer started has ended, and every run they reattached. A probe that is only waiting for its
    /// next timer is not a pass and is not waited for, but the re-arm after a probe pass is: when this returns, the next timer exists. For tests and shutdown; the app never waits on it.
    /// </summary>
    public async Task WhenIdleAsync()
    {
        Task passes;
        lock (_gate)
        {
            passes = _passes;
        }

        await passes.ConfigureAwait(false);
        Task background;
        lock (_gate)
        {
            background = _background;
        }

        await background.ConfigureAwait(false);
        Task followUps;
        lock (_gate)
        {
            followUps = _probeFollowUps;
        }

        // The step after a probe pass (clear the probing flag, arm the next timer) runs on a continuation: without waiting for it a caller
        // could see "idle" a moment before the next timer exists, and a test that then moves virtual time would move it past nothing (#559 r5).
        await followUps.ConfigureAwait(false);
    }

    /// <summary>Stops the probe. Passes already running finish.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _probeTimer?.Dispose();
            _probeTimer = null;
        }
    }
}
