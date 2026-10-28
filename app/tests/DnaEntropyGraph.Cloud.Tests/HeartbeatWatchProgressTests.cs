using DnaEntropyGraph.Core.Cloud;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Cloud.Tests;

/// <summary>
/// Issue #723 (the app half of #522): a worker whose heartbeat thread ticks while its compute thread is wedged in the <c>running</c> stage
/// is judged by <c>windowsDone</c>/<c>lastProgressAt</c> not moving across looks, on the watch alone with a scripted clock.
/// </summary>
public class HeartbeatWatchProgressTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromMinutes(20);

    private static string Status(long seq, int? windows = 3, string? progressAt = "2026-09-18T15:19:58Z", string stage = "running")
    {
        var progress = windows is null ? "" : $",\"windowsDone\":{windows},\"lastProgressAt\":\"{progressAt}\"";
        return $$$"""{"schema":1,"stage":"{{{stage}}}","heartbeatSeq":{{{seq}}},"updatedAt":"2026-09-18T15:20:{{{seq % 60:00}}}Z","worker":{"version":"1.0.0"}{{{progress}}}}""";
    }

    private static HeartbeatWatch Watch() => new(false, TimeSpan.FromMinutes(25), TimeSpan.FromMinutes(10), progressStallTimeout: Bound);

    /// <summary>Looks every minute with a moving heartbeat, calling <paramref name="status"/> for the status text of each.</summary>
    private static HeartbeatVerdict? LookEveryMinute(HeartbeatWatch watch, int fromMinute, int toMinute, Func<int, string> status)
    {
        for (var minute = fromMinute; minute <= toMinute; minute++)
        {
            if (watch.Observe(status(minute), null, TimeSpan.FromMinutes(minute)) is { } verdict)
            {
                return verdict;
            }
        }

        return null;
    }

    [Fact]
    public void A_ticking_heartbeat_with_frozen_progress_is_stuck_after_the_bound_measured_from_the_first_look_at_that_progress()
    {
        var watch = Watch();

        LookEveryMinute(watch, 0, 19, m => Status(m)).ShouldBeNull("19 minutes of frozen progress is inside the bound");
        var verdict = watch.Observe(Status(20), null, Bound);

        verdict.ShouldNotBeNull();
        verdict.Code.ShouldBe(RunErrorCodes.WorkerProgressStalled);
        verdict.DeleteVm.ShouldBeFalse();
        verdict.Stage.ShouldBe("running");
    }

    [Fact]
    public void One_more_finished_window_restarts_the_clock()
    {
        var watch = Watch();
        LookEveryMinute(watch, 0, 19, m => Status(m)).ShouldBeNull();

        watch.Observe(Status(20, windows: 4), null, TimeSpan.FromMinutes(20)).ShouldBeNull();
        LookEveryMinute(watch, 21, 39, m => Status(m, windows: 4)).ShouldBeNull("the bound runs from the look that saw window 4");
        watch.Observe(Status(40, windows: 4), null, TimeSpan.FromMinutes(40))!.Code.ShouldBe(RunErrorCodes.WorkerProgressStalled);
    }

    [Fact]
    public void A_moved_lastProgressAt_alone_restarts_the_clock()
    {
        var watch = Watch();
        LookEveryMinute(watch, 0, 19, m => Status(m)).ShouldBeNull();

        watch.Observe(Status(20, progressAt: "2026-09-18T15:40:00Z"), null, TimeSpan.FromMinutes(20)).ShouldBeNull();
        LookEveryMinute(watch, 21, 39, m => Status(m, progressAt: "2026-09-18T15:40:00Z")).ShouldBeNull();
    }

    [Fact]
    public void A_status_with_neither_field_is_never_judged_stuck_by_this_rule_however_long()
    {
        var watch = Watch();

        LookEveryMinute(watch, 0, 300, m => Status(m, windows: null)).ShouldBeNull("an older worker has no progress signal");
    }

    [Fact]
    public void The_comparison_is_between_looks_never_against_the_app_clock()
    {
        // A worker clock years behind the app's: lastProgressAt is ancient, but it MOVES, so the run is healthy.
        var watch = Watch();

        LookEveryMinute(watch, 0, 120, m => Status(m, windows: m, progressAt: $"2001-01-01T00:{m % 60:00}:00Z")).ShouldBeNull();
    }

    [Fact]
    public void A_stage_other_than_running_is_left_to_the_stage_deadlines()
    {
        var watch = new HeartbeatWatch(false, TimeSpan.FromMinutes(25), TimeSpan.FromMinutes(10), new Dictionary<string, TimeSpan> { ["uploading"] = TimeSpan.FromHours(5) }, Bound);

        LookEveryMinute(watch, 0, 60, m => Status(m, stage: "uploading")).ShouldBeNull();
    }

    [Fact]
    public void Snooze_gives_a_fresh_allowance_after_keep_waiting()
    {
        var watch = Watch();
        LookEveryMinute(watch, 0, 19, m => Status(m)).ShouldBeNull();
        watch.Observe(Status(20), null, Bound).ShouldNotBeNull();

        watch.Snooze(Bound);

        LookEveryMinute(watch, 21, 39, m => Status(m)).ShouldBeNull();
        watch.Observe(Status(40), null, TimeSpan.FromMinutes(40))!.Code.ShouldBe(RunErrorCodes.WorkerProgressStalled);
    }

    [Fact]
    public void The_default_bound_is_twenty_minutes()
    {
        var watch = new HeartbeatWatch(false, TimeSpan.FromMinutes(25), TimeSpan.FromMinutes(10));

        LookEveryMinute(watch, 0, 19, m => Status(m)).ShouldBeNull();
        watch.Observe(Status(20), null, TimeSpan.FromMinutes(20))!.Code.ShouldBe(RunErrorCodes.WorkerProgressStalled);
    }
}
