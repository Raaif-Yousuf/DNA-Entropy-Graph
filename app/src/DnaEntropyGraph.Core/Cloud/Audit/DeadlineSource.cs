namespace DnaEntropyGraph.Core.Cloud.Audit;

/// <summary>
/// The per-call deadline of a gateway call: a token that is cancelled when the caller's token is, or when the timeout passes. It is made WITHOUT
/// <see cref="CancellationTokenSource.CreateLinkedTokenSource(CancellationToken)"/> and without <see cref="CancellationTokenSource.CancelAfter(TimeSpan)"/>: a link cancels the deadline
/// source synchronously from inside the CALLER's own Cancel, and a timer cancels it on the timer thread, and either way a callback the callee registered on the token runs there.
/// A callback that throws would then surface out of the user's Cancel() as an AggregateException (MEASURED 2026-10-29, PR #944 CI, Release, twice), or be unhandled on the timer thread and end the
/// process (MEASURED 2026-10-29: 3 of 3 host deaths). Here both relays call <see cref="Cancel"/>, which swallows what a callee callback throws and traces only its class.
/// Every site that gives a deadline token to a gateway call makes it here (guarded by a test).
/// </summary>
internal sealed class DeadlineSource : IDisposable
{
    private readonly CancellationTokenSource _source = new();
    private readonly CancellationTokenRegistration _callerRegistration;
    private readonly ITimer? _timer;

    public DeadlineSource(CancellationToken callerToken, TimeSpan timeout, TimeProvider? timeProvider = null)
    {
        _callerRegistration = callerToken.Register(static state => ((DeadlineSource)state!).Cancel(), this);
        if (timeout != Timeout.InfiniteTimeSpan)
        {
            _timer = (timeProvider ?? TimeProvider.System).CreateTimer(static state => ((DeadlineSource)state!).Cancel(), this, timeout < TimeSpan.Zero ? TimeSpan.Zero : timeout, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>The token to hand the call.</summary>
    public CancellationToken Token => _source.Token;

    /// <summary>The underlying source, for <see cref="CloudAuditContext.StopAbandonedAsync"/>.</summary>
    public CancellationTokenSource Source => _source;

    /// <summary>Cancels the token. Never throws: a callee's callback that throws, or a source already disposed, is traced by class only.</summary>
    public void Cancel()
    {
        try
        {
            _source.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The call is over and its deadline gone: nothing left to cancel.
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning($"A cancellation callback of a cloud call threw: {ex.GetType().Name}");
        }
    }

    public void Dispose()
    {
        _callerRegistration.Dispose();
        _timer?.Dispose();
        _source.Dispose();
    }
}
