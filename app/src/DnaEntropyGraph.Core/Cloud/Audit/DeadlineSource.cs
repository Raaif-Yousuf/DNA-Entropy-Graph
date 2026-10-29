namespace DnaEntropyGraph.Core.Cloud.Audit;

/// <summary>
/// The per-call deadline of a gateway call: a token that is cancelled when the caller's token is, or when the timeout passes. It is made WITHOUT
/// <see cref="CancellationTokenSource.CreateLinkedTokenSource(CancellationToken)"/> and without <see cref="CancellationTokenSource.CancelAfter(TimeSpan)"/>: a link cancels the deadline
/// source synchronously from inside the CALLER's own Cancel, and a timer cancels it on the timer thread, and either way a callback the callee registered on the token runs there.
/// A callback that throws would then surface out of the user's Cancel() as an AggregateException (MEASURED 2026-10-29, PR #944 CI, Release, twice), or be unhandled on the timer thread and end the
/// process (MEASURED 2026-10-29: 3 of 3 host deaths). Here both relays call <see cref="Cancel"/>, which swallows what a callee callback throws and traces only its class.
/// Every site in the app that gives a deadline token to a gateway call makes it here (guarded by GatewayDeadlineGuardTests).
/// </summary>
public sealed class DeadlineSource : IDisposable
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _source = new();
    private readonly CancellationTokenRegistration _callerRegistration;
    private readonly ITimer? _timer;
    private bool _disposed;

    /// <param name="callerToken">The caller's own token; its cancel reaches <see cref="Token"/>.</param>
    /// <param name="timeout">
    /// <see cref="Timeout.InfiniteTimeSpan"/> for none. Zero (or a spent budget) cancels at construction, synchronously, like <c>new CancellationTokenSource(TimeSpan.Zero)</c>.
    /// Any other negative value throws <see cref="ArgumentOutOfRangeException"/>, like the framework's own constructor.
    /// </param>
    /// <param name="timeProvider">The clock the timer runs on; the system clock when null.</param>
    public DeadlineSource(CancellationToken callerToken, TimeSpan timeout, TimeProvider? timeProvider = null)
    {
        if (timeout < TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "A deadline is not negative.");
        }

        try
        {
            _callerRegistration = callerToken.Register(static state => ((DeadlineSource)state!).Cancel(), this);
            if (timeout == TimeSpan.Zero)
            {
                Cancel();
            }
            else if (timeout != Timeout.InfiniteTimeSpan)
            {
                _timer = (timeProvider ?? TimeProvider.System).CreateTimer(static state => ((DeadlineSource)state!).Cancel(), this, timeout, Timeout.InfiniteTimeSpan);
            }
        }
        catch
        {
            // Nothing is handed out when the constructor throws, so release what was already made.
            _callerRegistration.Dispose();
            _source.Dispose();
            throw;
        }
    }

    /// <summary>The token to hand the call.</summary>
    public CancellationToken Token => _source.Token;

    /// <summary>The underlying source, for <see cref="CloudAuditContext.StopAbandonedAsync"/>. Do not dispose it.</summary>
    public CancellationTokenSource Source => _source;

    /// <summary>Cancels the token. Never throws: a callee's callback that throws is traced by class only, and a source already disposed is left alone.</summary>
    public void Cancel()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            CloudAuditContext.CancelSwallowing(_source);
        }
    }

    public void Dispose()
    {
        // The registration's Dispose waits for a relay running on another thread (which takes the gate), so it must not be called while holding it.
        _callerRegistration.Dispose();
        _timer?.Dispose();
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            // A timer callback already in flight finds the flag and does nothing.
            _disposed = true;
            _source.Dispose();
        }
    }
}
