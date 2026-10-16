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
    private static readonly TimeSpan PumpGiveUp = TimeSpan.FromSeconds(30);

    public VirtualTimeProvider Run { get; } = new();

    public VirtualTimeProvider Retry { get; } = new(autoAdvance: true);

    public Task SleepAsync(TimeSpan span, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Run.Advance(span);
        Retry.Advance(span);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Wires <paramref name="gcp"/> so a call it parks (a scripted hang, slow create or create that never lands) is cut by its
    /// caller's deadline on <see cref="Run"/>. Why not "advance while any call is parked": that would also cut a healthy but slow
    /// call that runs after the abandoned parked one. This advances once per parked call the test scripted, by that call's own
    /// <paramref name="callLimit"/> (plus a millisecond) for a hang, or by the create's own delay, and only after the caller has
    /// armed its deadline timer (the clock's created-timer count rises past what it was when the call parked; a count of live
    /// timers is not used, a timer from the previous call may still be disposing), because a deadline armed after the clock moved
    /// would never fire. A create that never lands gets one more move, by <paramref name="settleLimit"/> (plus a millisecond),
    /// once the caller starts waiting out the settle window on the abandoned create: nothing else would ever end that wait.
    /// </summary>
    public void ParkPump(FakeGcp gcp, TimeSpan callLimit, TimeSpan? settleLimit = null)
        => gcp.OnCallParked = (kind, span) =>
        {
            var createdBefore = Run.TimersCreated;
            var step = span == Timeout.InfiniteTimeSpan ? callLimit + TimeSpan.FromMilliseconds(1) : span;
            var settle = kind == FakeGcp.ParkedCall.CreateThatNeverLands ? settleLimit : null;
            _ = Task.Run(async () =>
            {
                await WaitForTimerAsync(createdBefore).ConfigureAwait(false);
                var armed = Run.TimersCreated;
                Run.Advance(step);
                if (settle is { } window)
                {
                    await WaitForTimerAsync(armed).ConfigureAwait(false);
                    Run.Advance(window + TimeSpan.FromMilliseconds(1));
                }
            });
        };

    private async Task WaitForTimerAsync(int createdBefore)
    {
        // A signal, not a poll: the clock says when the caller has armed a deadline. The real-time bound only ends a stuck pump (the deadline was never armed); it asserts nothing.
        try
        {
            await Run.WhenTimerCreatedAsync(createdBefore).WaitAsync(PumpGiveUp).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // No deadline was armed: move on, so the stuck test fails on its own watchdog instead of the pump faulting unobserved.
        }
    }
}
