namespace DnaEntropyGraph.Core.Cloud;

/// <summary>
/// Issue #530: the observer the resilience pipeline reports to, with one addition. It forwards everything to the observer it wraps (the
/// offline banner's source) and, when the connection comes back (<c>OnConnectivityChanged(offline: false)</c>), runs the reconciler again,
/// so a run it could not judge while offline (a Deferred row) is judged now, not at the next launch. It never double-drives a run: the
/// reconciler registers every run it touches in <see cref="ActiveRuns"/>, so a run the engine or an earlier pass owns is skipped. Passes are
/// single-flight: reconnects while one runs coalesce into at most one follow-up pass.
/// </summary>
public sealed class ReconcileOnReconnect : ICloudCallObserver
{
    private readonly ICloudCallObserver _inner;
    private readonly Func<CancellationToken, Task> _pass;
    private readonly object _gate = new();
    private Task _passes = Task.CompletedTask;
    private bool _running;
    private bool _again;

    /// <param name="reconciler">Resolved on the first reconnect, not at construction: the reconciler needs the gateways, and the gateways need this observer.</param>
    public ReconcileOnReconnect(ICloudCallObserver inner, Func<JobReconciler> reconciler)
        : this(inner, token => reconciler().ReconcileAsync(token))
    {
    }

    /// <param name="pass">One reconcile pass. Resolved and run on each reconnect, never at construction.</param>
    public ReconcileOnReconnect(ICloudCallObserver inner, Func<CancellationToken, Task> pass)
    {
        _inner = inner;
        _pass = pass;
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
                await _pass(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Called from inside a cloud call's own bookkeeping: nothing here may fault it. A row not judged now stays
                // non-terminal and is looked at on the next reconnect or launch.
            }
        }
        while (TakeFollowUp());
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

    /// <summary>Completes when every reconcile pass this observer started has ended. For tests and shutdown; the app never waits on it.</summary>
    public Task WhenIdleAsync()
    {
        lock (_gate)
        {
            return _passes;
        }
    }
}
