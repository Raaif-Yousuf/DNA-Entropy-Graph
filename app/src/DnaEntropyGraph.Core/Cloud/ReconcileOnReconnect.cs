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
/// </summary>
public sealed class ReconcileOnReconnect : ICloudCallObserver
{
    private readonly ICloudCallObserver _inner;
    private readonly Func<CancellationToken, Task<Task>> _pass;
    private readonly IDiagnosticsLog _log;
    private readonly object _gate = new();
    private Task _passes = Task.CompletedTask;
    private Task _background = Task.CompletedTask;
    private bool _running;
    private bool _again;

    /// <param name="reconciler">Resolved on the first reconnect, not at construction: the reconciler needs the gateways, and the gateways need this observer.</param>
    /// <param name="log">Told when a pass throws (its error class only). Null records nothing.</param>
    public ReconcileOnReconnect(ICloudCallObserver inner, Func<JobReconciler> reconciler, IDiagnosticsLog? log = null)
        : this(inner, token => reconciler().BeginReconcileAsync(token), log)
    {
    }

    /// <param name="pass">
    /// One reconcile pass, run on each reconnect. Its outer task is the pass (single-flight: the next pass waits for it); its result is a
    /// task for the runs the pass reattached, which keep going on their own and never hold a later pass back.
    /// </param>
    /// <param name="log">Told when a pass throws (its error class only). Null records nothing.</param>
    public ReconcileOnReconnect(ICloudCallObserver inner, Func<CancellationToken, Task<Task>> pass, IDiagnosticsLog? log = null)
    {
        _inner = inner;
        _pass = pass;
        _log = log ?? NullDiagnosticsLog.Instance;
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

        lock (_gate)
        {
            if (_running)
            {
                // Single flight: a pass already runs. Reconnects during it are one follow-up pass, run after it ends, so two passes never overlap
                // and a run one pass released is never re-driven by another at the same moment.
                _again = true;
                return;
            }

            _running = true;
            _passes = Task.Run(RunPassesAsync);
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
                // non-terminal and is looked at on the next reconnect or launch; the class of the error is the only trace.
                _log.Warning("reconcile-on-reconnect", null, ex.GetType().Name);
            }
        }
        while (TakeFollowUp());
    }

    /// <summary>Remembers the runs a pass reattached, so <see cref="WhenIdleAsync"/> can wait for them; their own failure is logged, never thrown.</summary>
    private void Track(Task reattached)
    {
        var watched = reattached.ContinueWith(
            t =>
            {
                if (t.IsFaulted)
                {
                    _log.Warning("reconcile-on-reconnect", null, t.Exception!.GetBaseException().GetType().Name);
                }
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

    /// <summary>Completes when every reconcile pass this observer started has ended, and every run they reattached. For tests and shutdown; the app never waits on it.</summary>
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
    }
}
