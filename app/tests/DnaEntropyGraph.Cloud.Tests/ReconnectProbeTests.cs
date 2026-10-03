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
            await WaitForPendingTimersAsync(rig.Time, 1);
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

    private static async Task WaitForPendingTimersAsync(VirtualTimeProvider time, int expected)
    {
        for (var i = 0; i < 500 && time.PendingTimers != expected; i++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        time.PendingTimers.ShouldBe(expected);
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
        await WaitForPendingTimersAsync(time, 1);
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
        time.Advance(TimeSpan.FromSeconds(60));
        await observer.WhenIdleAsync();

        passes.ShouldBe(2);
    }
}
