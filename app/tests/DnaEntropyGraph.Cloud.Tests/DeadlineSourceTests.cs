using System.Diagnostics;
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

        public List<RecordingTimer> Timers { get; } = [];

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Callbacks.Add(_ => callback(state));
            var timer = new RecordingTimer(dueTime);
            Timers.Add(timer);
            return timer;
        }
    }

    private sealed class RecordingTimer(TimeSpan due) : ITimer
    {
        public TimeSpan Due { get; } = due;

        public bool Disposed { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period) => true;

        public void Dispose() => Disposed = true;

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
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
    public void A_deadline_source_cancels_on_the_caller_and_on_the_timer()
    {
        var clock = new ManualTimeProvider();
        using var cts = new CancellationTokenSource();
        using var fromCaller = new DeadlineSource(cts.Token, TimeSpan.FromSeconds(5), clock);
        fromCaller.Token.IsCancellationRequested.ShouldBeFalse();
        cts.Cancel();
        fromCaller.Token.IsCancellationRequested.ShouldBeTrue("the caller's cancel reaches the deadline token");

        using var timed = new DeadlineSource(CancellationToken.None, TimeSpan.FromSeconds(5), clock);
        clock.Timers[^1].Due.ShouldBe(TimeSpan.FromSeconds(5));
        clock.Callbacks[^1](null);
        timed.Token.IsCancellationRequested.ShouldBeTrue("the timer reaches the deadline token");
    }

    /// <summary>Collects the trace lines a swallowed callback failure writes, so "nothing was swallowed" is observable.</summary>
    private sealed class CapturingListener : TraceListener
    {
        private readonly List<string> _lines = [];

        public string[] Lines
        {
            get
            {
                lock (_lines)
                {
                    return [.. _lines];
                }
            }
        }

        public override void Write(string? message) => WriteLine(message);

        public override void WriteLine(string? message)
        {
            lock (_lines)
            {
                _lines.Add(message ?? string.Empty);
            }
        }
    }

    [Fact]
    public void Dispose_releases_the_timer_and_a_late_timer_fire_or_caller_cancel_never_reaches_the_disposed_source()
    {
        var clock = new ManualTimeProvider();
        using var cts = new CancellationTokenSource();
        var deadline = new DeadlineSource(cts.Token, TimeSpan.FromSeconds(5), clock);
        var listener = new CapturingListener();
        Trace.Listeners.Add(listener);
        try
        {
            deadline.Dispose();

            clock.Timers.Single().Disposed.ShouldBeTrue("the timer is released with the source");
            clock.Callbacks.Single()(null);
            cts.Cancel();

            // Cancelling a disposed source throws ObjectDisposedException, which Cancel swallows and traces: a line naming it is the evidence that the disposed flag did not stop the cancel.
            listener.Lines.ShouldNotContain(l => l.Contains(nameof(ObjectDisposedException), StringComparison.Ordinal));
        }
        finally
        {
            Trace.Listeners.Remove(listener);
        }
    }

    [Fact]
    public async Task A_callee_callback_blocked_until_Dispose_returns_does_not_deadlock_the_Dispose_when_the_caller_cancels()
    {
        using var cts = new CancellationTokenSource();
        var deadline = new DeadlineSource(cts.Token, Timeout.InfiniteTimeSpan);
        using var entered = new ManualResetEventSlim();
        using var disposed = new ManualResetEventSlim();
        deadline.Token.Register(() =>
        {
            entered.Set();
            disposed.Wait(TimeSpan.FromSeconds(30));
        });

        var cancelling = Task.Run(() => cts.Cancel(), TestContext.Current.CancellationToken);
        entered.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken).ShouldBeTrue();
        var disposing = Task.Run(deadline.Dispose, TestContext.Current.CancellationToken);

        var finished = await Task.WhenAny(disposing, Task.Delay(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        disposed.Set();
        finished.ShouldBe(disposing, "Dispose must not wait for a relay that is running a callee callback");
        await cancelling;
    }

    [Fact]
    public async Task A_callee_callback_blocked_until_Dispose_returns_does_not_deadlock_the_Dispose_when_the_timer_fires()
    {
        var clock = new ManualTimeProvider();
        var deadline = new DeadlineSource(CancellationToken.None, TimeSpan.FromSeconds(5), clock);
        using var entered = new ManualResetEventSlim();
        using var disposed = new ManualResetEventSlim();
        deadline.Token.Register(() =>
        {
            entered.Set();
            disposed.Wait(TimeSpan.FromSeconds(30));
        });

        var firing = Task.Run(() => clock.Callbacks.Single()(null), TestContext.Current.CancellationToken);
        entered.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken).ShouldBeTrue();
        var disposing = Task.Run(deadline.Dispose, TestContext.Current.CancellationToken);

        var finished = await Task.WhenAny(disposing, Task.Delay(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        disposed.Set();
        finished.ShouldBe(disposing, "Dispose must not wait behind a timer thread that is running a callee callback");
        await firing;
    }

    [Fact]
    public void A_zero_timeout_cancels_at_construction_and_never_arms_a_timer()
    {
        var clock = new ManualTimeProvider();
        using var deadline = new DeadlineSource(CancellationToken.None, TimeSpan.Zero, clock);

        deadline.Token.IsCancellationRequested.ShouldBeTrue();
        clock.Timers.ShouldBeEmpty();
    }

    [Fact]
    public void An_infinite_timeout_arms_no_timer_and_any_other_negative_one_is_refused()
    {
        var clock = new ManualTimeProvider();
        using (var forever = new DeadlineSource(CancellationToken.None, Timeout.InfiniteTimeSpan, clock))
        {
            forever.Token.IsCancellationRequested.ShouldBeFalse();
        }

        clock.Timers.ShouldBeEmpty();
        Should.Throw<ArgumentOutOfRangeException>(() => new DeadlineSource(CancellationToken.None, TimeSpan.FromSeconds(-2), clock));
    }
}
