using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Cloud;
using NSubstitute;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Cloud.Tests;

/// <summary>
/// Issue #559: while a pass leaves something deferred, <see cref="ReconcileOnReconnect"/> runs the pass again on a bounded, doubling backoff
/// (virtual time here, no sleeps), and stops once nothing is deferred.
/// </summary>
public class ReconnectProbeTests
{
    private static readonly TimeSpan Initial = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Cap = TimeSpan.FromMinutes(2);

    private sealed class Rig : IDisposable
    {
        public Rig(bool deferred = true)
        {
            Deferred = deferred;
            Observer = new ReconcileOnReconnect(
                Substitute.For<ICloudCallObserver>(),
                _ =>
                {
                    Passes++;
                    return Task.FromResult<Task>(Task.CompletedTask);
                },
                () => Deferred,
                time: Time,
                probeInitialDelay: Initial,
                probeMaxDelay: Cap);
        }

        public VirtualTimeProvider Time { get; } = new();

        public ReconcileOnReconnect Observer { get; }

        public bool Deferred { get; set; }

        public int Passes { get; private set; }

        public void Dispose() => Observer.Dispose();

        /// <summary>Advances virtual time and waits for every pass that came due (nothing to wait for when no timer was due).</summary>
        public async Task AdvanceAsync(TimeSpan by)
        {
            Time.Advance(by);
            await Observer.WhenIdleAsync();
        }
    }

    [Fact]
    public async Task A_probe_pass_runs_when_the_first_delay_has_passed_and_only_then()
    {
        using var rig = new Rig();
        rig.Observer.StartProbeIfDeferred();

        await rig.AdvanceAsync(Initial - TimeSpan.FromSeconds(1));
        rig.Passes.ShouldBe(0, "the probe waits its full first delay");

        await rig.AdvanceAsync(TimeSpan.FromSeconds(1));
        rig.Passes.ShouldBe(1);
    }

    [Fact]
    public async Task The_probe_does_not_start_when_nothing_is_deferred()
    {
        using var rig = new Rig(deferred: false);

        rig.Observer.StartProbeIfDeferred();
        await rig.AdvanceAsync(TimeSpan.FromDays(1));

        rig.Passes.ShouldBe(0);
        rig.Time.PendingTimers.ShouldBe(0);
    }

    [Fact]
    public async Task The_wait_between_probe_passes_doubles_and_stops_doubling_at_the_cap()
    {
        using var rig = new Rig();
        rig.Observer.StartProbeIfDeferred();
        var gaps = new List<TimeSpan>();

        // Each gap is the shortest advance that produces the next pass, found by whole seconds (virtual, so free). After every pass the test
        // waits until the probe's next timer is armed: the re-arm runs on a continuation, so stepping before it would measure nothing.
        for (var pass = 1; pass <= 6; pass++)
        {
            rig.Time.PendingTimers.ShouldBe(1, "WhenIdleAsync returns only once the next probe timer is armed");
            var waited = TimeSpan.Zero;
            while (rig.Passes < pass)
            {
                await rig.AdvanceAsync(TimeSpan.FromSeconds(1));
                waited += TimeSpan.FromSeconds(1);
                waited.ShouldBeLessThan(TimeSpan.FromMinutes(10), "a probe pass never came");
            }

            gaps.Add(waited);
        }

        gaps.ShouldBe([Initial, TimeSpan.FromSeconds(60), Cap, Cap, Cap, Cap]);
    }

    [Fact]
    public async Task The_probe_stops_once_a_pass_leaves_nothing_deferred_and_starts_over_at_the_first_delay_next_time()
    {
        using var rig = new Rig();
        rig.Observer.StartProbeIfDeferred();
        await rig.AdvanceAsync(Initial);
        rig.Passes.ShouldBe(1);
        rig.Deferred = false;
        await rig.AdvanceAsync(TimeSpan.FromSeconds(60));
        rig.Passes.ShouldBe(2, "the second pass is the one that finds nothing deferred");

        await rig.AdvanceAsync(TimeSpan.FromDays(1));
        rig.Passes.ShouldBe(2, "no probe pass after nothing is deferred");
        rig.Time.PendingTimers.ShouldBe(0);

        rig.Deferred = true;
        rig.Observer.StartProbeIfDeferred();
        await rig.AdvanceAsync(Initial);
        rig.Passes.ShouldBe(3, "a later deferral waits the first delay again, not the doubled one");
    }

    [Fact]
    public async Task A_pass_a_reconnect_starts_that_leaves_something_deferred_starts_the_probe()
    {
        using var rig = new Rig();

        rig.Observer.OnConnectivityChanged(offline: false);
        await rig.Observer.WhenIdleAsync();
        rig.Passes.ShouldBe(1);

        await rig.AdvanceAsync(Initial);
        rig.Passes.ShouldBe(2, "the reconnect pass left a row deferred, so the probe looks again");
    }

    [Fact]
    public async Task Disposing_the_observer_stops_the_probe()
    {
        var rig = new Rig();
        rig.Observer.StartProbeIfDeferred();

        rig.Dispose();
        await rig.AdvanceAsync(TimeSpan.FromDays(1));

        rig.Passes.ShouldBe(0);
    }

    [Fact]
    public async Task A_deferred_check_that_throws_while_re_arming_does_not_stop_the_probe()
    {
        // Issue #559 review r4 F4: a throwing hasDeferred ended the probe for good; it re-arms on the current delay instead.
        var time = new VirtualTimeProvider();
        var broken = false;
        var passes = 0;
        using var observer = new ReconcileOnReconnect(
            Substitute.For<ICloudCallObserver>(),
            _ =>
            {
                passes++;
                return Task.FromResult<Task>(Task.CompletedTask);
            },
            () => broken ? throw new InvalidOperationException("boom") : true,
            time: time,
            probeInitialDelay: Initial,
            probeMaxDelay: Cap);
        observer.StartProbeIfDeferred();

        broken = true;
        time.Advance(Initial);
        await observer.WhenIdleAsync();
        time.PendingTimers.ShouldBe(1, "WhenIdleAsync returns only once the probe re-armed");
        broken = false;
        passes.ShouldBe(1);

        time.Advance(Initial);
        await observer.WhenIdleAsync();

        passes.ShouldBe(2, "the probe re-armed on the same delay after the check threw");
    }

    [Fact]
    public async Task A_probe_pass_that_throws_does_not_end_the_probe()
    {
        var time = new VirtualTimeProvider();
        var passes = 0;
        using var observer = new ReconcileOnReconnect(
            Substitute.For<ICloudCallObserver>(),
            _ =>
            {
                passes++;
                throw new InvalidOperationException("boom");
            },
            () => true,
            time: time,
            probeInitialDelay: Initial,
            probeMaxDelay: Cap);
        observer.StartProbeIfDeferred();

        time.Advance(Initial);
        await observer.WhenIdleAsync();
        passes.ShouldBe(1);
        time.Advance(TimeSpan.FromSeconds(60));
        await observer.WhenIdleAsync();

        passes.ShouldBe(2);
    }

    private sealed class ThrowingLog : IDiagnosticsLog
    {
        public void Warning(string source, string? jobId, string errorClass) => throw new InvalidOperationException("the log is broken");
    }

    [Fact]
    public async Task A_probe_follow_up_that_faults_never_makes_WhenIdleAsync_throw()
    {
        // Review r5: the step after a probe pass runs on a continuation whose failure was kept in a task WhenIdleAsync awaits for ever after.
        // Here the deferred check throws while re-arming AND the log that is told about it throws: the follow-up must swallow both.
        var time = new VirtualTimeProvider();
        var broken = false;
        var passes = 0;
        using var observer = new ReconcileOnReconnect(
            Substitute.For<ICloudCallObserver>(),
            _ =>
            {
                passes++;
                return Task.FromResult<Task>(Task.CompletedTask);
            },
            () => broken ? throw new InvalidOperationException("boom") : true,
            log: new ThrowingLog(),
            time: time,
            probeInitialDelay: Initial,
            probeMaxDelay: Cap);
        observer.StartProbeIfDeferred();

        broken = true;
        time.Advance(Initial);
        var first = await Record.ExceptionAsync(observer.WhenIdleAsync);
        var second = await Record.ExceptionAsync(observer.WhenIdleAsync);

        first.ShouldBeNull();
        second.ShouldBeNull();
        passes.ShouldBe(1, "the probe fired and ran its pass");
        time.PendingTimers.ShouldBe(1, "and re-armed on the same delay even though the check and the log both threw");
    }

    /// <summary>A clock whose next CreateTimer calls throw, then behaves as the wrapped virtual clock.</summary>
    private sealed class FlakyTimerClock(VirtualTimeProvider inner, int failures) : TimeProvider
    {
        private int _failures = failures;

        public override DateTimeOffset GetUtcNow() => inner.GetUtcNow();

        public override long GetTimestamp() => inner.GetTimestamp();

        public override long TimestampFrequency => inner.TimestampFrequency;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
            => _failures-- > 0 ? throw new InvalidOperationException("no timer") : inner.CreateTimer(callback, state, dueTime, period);
    }

    [Fact]
    public async Task A_timer_that_cannot_be_created_does_not_wedge_the_probe()
    {
        // Review r6: _probing was set before CreateTimer, so a throw left it true with no timer and every later start returned early.
        var time = new VirtualTimeProvider();
        using var observer = new ReconcileOnReconnect(
            Substitute.For<ICloudCallObserver>(),
            _ => Task.FromResult<Task>(Task.CompletedTask),
            () => true,
            time: new FlakyTimerClock(time, failures: 1),
            probeInitialDelay: Initial,
            probeMaxDelay: Cap);

        var first = Record.Exception(observer.StartProbeIfDeferred);
        time.PendingTimers.ShouldBe(0, "precondition: the first timer could not be created");
        observer.StartProbeIfDeferred();

        first.ShouldBeNull("a failed timer is logged, not thrown at the caller (a pass's own end)");
        time.PendingTimers.ShouldBe(1, "the next start arms the probe: nothing is left marked as probing");
        await observer.WhenIdleAsync();
    }
}
