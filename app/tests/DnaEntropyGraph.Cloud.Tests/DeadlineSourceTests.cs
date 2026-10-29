using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Cloud;
using DnaEntropyGraph.Core.Cloud.Audit;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Cloud.Tests;

/// <summary>
/// Issue #245 (MEASURED 2026-10-29, PR #944 CI, Release): a callee's throwing cancellation callback must not surface out of the user's Cancel()
/// nor out of a timer thread. Both relays are forced here deterministically.
/// </summary>
public class DeadlineSourceTests
{
    /// <summary>A TimeProvider whose timers never fire by themselves: the test fires them, on its own thread, where an escaping exception is visible.</summary>
    private sealed class ManualTimeProvider : TimeProvider
    {
        public List<TimerCallback> Callbacks { get; } = [];

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Callbacks.Add(_ => callback(state));
            return new NoopTimer();
        }

        private sealed class NoopTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;

            public void Dispose()
            {
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private static async Task<int> NeverAsync(CancellationToken token)
    {
        await Task.Delay(Timeout.Infinite, token);
        return 0;
    }

    private static Func<CancellationToken, Task<int>> ThrowingCallbackCall(TaskCompletionSource reached) => token =>
    {
        token.Register(() => throw new InvalidOperationException("callee callback"));
        reached.TrySetResult();
        return NeverAsync(token);
    };

    [Fact]
    public async Task A_users_cancel_through_GatewayCalls_does_not_throw_when_the_call_has_a_throwing_cancellation_callback()
    {
        var calls = new GatewayCalls(new CloudRunSettings { CallTimeout = TimeSpan.FromSeconds(30) }, new FakeGcp());
        using var cts = new CancellationTokenSource();
        var escaped = new List<Exception>();

        // The user cancels from inside the call, after the callee registered its throwing callback and before the wait has registered anything on the caller's token:
        // the only relay left on the caller's token is the deadline's own, and it runs inline on this thread, so a throw would come out of the user's Cancel() every run (not a race).
        var task = calls.CallAsync(token =>
        {
            token.Register(() => throw new InvalidOperationException("callee callback"));
            try
            {
                cts.Cancel();
            }
            catch (Exception ex)
            {
                escaped.Add(ex);
            }

            return NeverAsync(token);
        }, cts.Token);

        await Should.ThrowAsync<OperationCanceledException>(task);
        escaped.ShouldBeEmpty("a callee's callback must not surface out of the user's own Cancel()");
    }

    [Fact]
    public async Task A_deadline_firing_on_a_timer_thread_through_GatewayCalls_is_a_timeout_and_nothing_escapes_the_timer()
    {
        var clock = new ManualTimeProvider();
        var calls = new GatewayCalls(new CloudRunSettings { CallTimeout = TimeSpan.FromSeconds(30), TimeProvider = clock }, new FakeGcp());
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var task = calls.CallAsync(ThrowingCallbackCall(reached), CancellationToken.None);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        var escaped = new List<Exception>();
        foreach (var fire in clock.Callbacks.ToList())
        {
            try
            {
                fire(null);
            }
            catch (Exception ex)
            {
                escaped.Add(ex);
            }
        }

        escaped.ShouldBeEmpty("an exception out of a timer callback ends the process");
        var failure = await Should.ThrowAsync<CloudOperationException>(task);
        failure.Error.Code.ShouldBe("TIMEOUT");
    }

    [Fact]
    public void A_deadline_source_cancels_on_the_caller_and_on_the_timer_and_disposes_both_registrations()
    {
        var clock = new ManualTimeProvider();
        using var cts = new CancellationTokenSource();
        var deadline = new DeadlineSource(cts.Token, TimeSpan.FromSeconds(5), clock);
        deadline.Token.IsCancellationRequested.ShouldBeFalse();

        cts.Cancel();
        deadline.Token.IsCancellationRequested.ShouldBeTrue("the caller's cancel reaches the deadline token");
        deadline.Dispose();
        Should.NotThrow(() => clock.Callbacks.ForEach(c => c(null)), "a timer that fires after dispose is harmless");

        using var timed = new DeadlineSource(CancellationToken.None, TimeSpan.FromSeconds(5), clock);
        clock.Callbacks[^1](null);
        timed.Token.IsCancellationRequested.ShouldBeTrue("the timer reaches the deadline token");
    }
}
