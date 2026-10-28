using DnaEntropyGraph.Cloud;

namespace DnaEntropyGraph.Cloud.Tests;

/// <summary>
/// The clocks of one runner test (#525, #567). <see cref="Run"/> is the runner's: call deadlines, the result and lifecycle
/// waits. It moves only when the runner sleeps between polls (<see cref="SleepAsync"/>) or when a call the test itself scripted
/// to park is waiting (<see cref="ParkPump"/>), never while a healthy call is in flight, so a loaded machine cannot make a
/// call "time out". <see cref="Retry"/> is the retry pipeline's: its backoff fires at once and the breaker's break period
/// passes as the runner polls.
/// </summary>
internal sealed class RunnerClocks
{
    /// <summary>Real-time ceiling on a pump waiting for a deadline to be armed; it only ends a stuck pump, it asserts nothing.</summary>
    private static readonly TimeSpan PumpGiveUp = TimeSpan.FromSeconds(10);

    public VirtualTimeProvider Run { get; } = new();

    public VirtualTimeProvider Retry { get; } = new(autoAdvance: true);

    public Task SleepAsync(TimeSpan span, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (PumpFault is { } fault)
        {
            throw new InvalidOperationException("A parked-call pump failed: " + fault.Message, fault);
        }

        Run.Advance(span);
        Retry.Advance(span);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Wires <paramref name="gcp"/> so a call it parks (a scripted hang, slow create or create that never lands) is cut by its
    /// caller's deadline on <see cref="Run"/>. Why not "advance while any call is parked": that would also cut a healthy but slow
    /// call that runs after the abandoned parked one. This advances once per parked call the test scripted, by that call's own
    /// <paramref name="callLimit"/> (plus a millisecond) for a hang, or by the create's own delay, and only after the caller has
    /// armed THE deadline timer: the one whose due time is <paramref name="callLimit"/>, created after the call parked (any other
    /// timer, such as a retry or a poll, is not what the call is waiting on), because a deadline armed after the clock moved would
    /// never fire. A create that never lands gets one more move, by <paramref name="settleLimit"/> (plus a millisecond), once the
    /// caller has armed the settle window (a timer of <paramref name="settleLimit"/>) on the abandoned create: nothing else would
    /// ever end that wait. A pump whose timer never arrives records the fault (<see cref="PumpFault"/>) and the next
    /// <see cref="SleepAsync"/> throws it, so a wrong limit fails the test with its cause instead of hanging it silently.
    /// </summary>
    public void ParkPump(FakeGcp gcp, TimeSpan callLimit, TimeSpan? settleLimit = null)
        => gcp.OnCallParked = (kind, span) =>
        {
            var createdBefore = Run.TimersCreated;
            var step = span == Timeout.InfiniteTimeSpan ? callLimit + TimeSpan.FromMilliseconds(1) : span;
            var settle = kind == FakeGcp.ParkedCall.CreateThatNeverLands ? settleLimit : null;
            _ = Task.Run(async () =>
            {
                try
                {
                    await WaitForTimerAsync(createdBefore, callLimit).ConfigureAwait(false);
                    var armed = Run.TimersCreated;
                    Run.Advance(step);
                    if (settle is { } window)
                    {
                        await WaitForTimerAsync(armed, window).ConfigureAwait(false);
                        Run.Advance(window + TimeSpan.FromMilliseconds(1));
                    }
                }
                catch (Exception ex)
                {
                    Volatile.Write(ref _pumpFault, ex);
                }
            });
        };

    private Exception? _pumpFault;

    /// <summary>What stopped a pump (a deadline it waited for was never armed); null while every pump is healthy. <see cref="SleepAsync"/> throws it.</summary>
    public Exception? PumpFault => Volatile.Read(ref _pumpFault);

    private async Task WaitForTimerAsync(int createdBefore, TimeSpan due)
    {
        // A signal, not a poll: the clock says when the caller has armed the deadline. The real-time bound only ends a stuck pump; it asserts nothing but is loud.
        try
        {
            await Run.WhenTimerCreatedAsync(createdBefore, due).WaitAsync(PumpGiveUp).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new InvalidOperationException($"The pump waited {PumpGiveUp.TotalSeconds:0} s for a timer of {due.TotalMilliseconds:0} ms on the runner clock and none was armed: the limit passed to ParkPump is not the deadline the caller uses.");
        }
    }
}
