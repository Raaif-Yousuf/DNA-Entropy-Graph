namespace DnaEntropyGraph.Core.Cloud;

/// <summary>
/// Issue #530: the observer the resilience pipeline reports to, with one addition. It forwards everything to the observer it wraps (the
/// offline banner's source) and, when the connection comes back (<c>OnConnectivityChanged(offline: false)</c>), runs the reconciler again,
/// so a run it could not judge while offline (a Deferred row) is judged now, not at the next launch. It never double-drives a run: the
/// reconciler registers every run it touches in <see cref="ActiveRuns"/>, so a run the engine or an earlier pass owns is skipped.
/// </summary>
public sealed class ReconcileOnReconnect : ICloudCallObserver
{
    private readonly ICloudCallObserver _inner;
    private readonly Func<JobReconciler> _reconciler;
    private readonly object _gate = new();
    private Task _passes = Task.CompletedTask;

    /// <param name="reconciler">Resolved on the first reconnect, not at construction: the reconciler needs the gateways, and the gateways need this observer.</param>
    public ReconcileOnReconnect(ICloudCallObserver inner, Func<JobReconciler> reconciler)
    {
        _inner = inner;
        _reconciler = reconciler;
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

        var pass = Task.Run(async () =>
        {
            try
            {
                await _reconciler().ReconcileAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Called from inside a cloud call's own bookkeeping: nothing here may fault it. A row not judged now stays
                // non-terminal and is looked at on the next reconnect or launch.
            }
        });
        lock (_gate)
        {
            _passes = Task.WhenAll(_passes, pass);
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
