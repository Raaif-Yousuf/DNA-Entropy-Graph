using DnaEntropyGraph.Core.Cloud;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Cloud.Tests;

/// <summary>
/// Issue #675: the soft "taking longer than usual" notice (a stage past 75 percent of its deadline, once per stage) and the snooze that
/// "Keep waiting" uses, on the watch alone with a scripted clock and scripted status files.
/// </summary>
public class HeartbeatWatchSlowNoticeTests
{
    private static readonly TimeSpan Stale = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan First = TimeSpan.FromMinutes(25);
    private static readonly TimeSpan ModelLoading = TimeSpan.FromMinutes(15);

    private static string Status(long seq, string stage = "model-loading")
        => $$$"""{"schema":1,"stage":"{{{stage}}}","heartbeatSeq":{{{seq}}},"updatedAt":"2026-09-18T15:20:{{{seq % 60:00}}}Z","worker":{"version":"1.0.0"}}""";

    private static HeartbeatWatch Watch() => new(false, First, Stale);

    [Fact]
    public void No_notice_before_75_percent_of_the_stage_deadline_and_one_at_it_naming_the_stage_and_the_deadline()
    {
        var watch = Watch();
        watch.Observe(Status(1), null, TimeSpan.Zero);
        watch.TakeSlowNotice().ShouldBeNull();

        var justBefore = TimeSpan.FromTicks((long)(ModelLoading.Ticks * 0.75)) - TimeSpan.FromSeconds(1);
        watch.Observe(Status(2), null, justBefore);
        watch.TakeSlowNotice().ShouldBeNull();

        var atThreshold = TimeSpan.FromTicks((long)(ModelLoading.Ticks * 0.75));
        watch.Observe(Status(3), null, atThreshold);
        var notice = watch.TakeSlowNotice();

        notice.ShouldNotBeNull();
        notice.Stage.ShouldBe("model-loading");
        notice.Deadline.ShouldBe(ModelLoading);
        notice.InStage.ShouldBe(atThreshold);
    }

    [Fact]
    public void The_notice_is_given_once_per_stage_entry_and_taking_it_clears_it()
    {
        var watch = Watch();
        watch.Observe(Status(1), null, TimeSpan.Zero);
        watch.Observe(Status(2), null, TimeSpan.FromMinutes(12));
        watch.TakeSlowNotice().ShouldNotBeNull();

        watch.TakeSlowNotice().ShouldBeNull("taking it clears it");
        watch.Observe(Status(3), null, TimeSpan.FromMinutes(13));
        watch.TakeSlowNotice().ShouldBeNull("the same stage is not announced twice");
    }

    [Fact]
    public void A_new_stage_restarts_the_clock_and_can_be_announced_again()
    {
        var watch = Watch();
        watch.Observe(Status(1), null, TimeSpan.Zero);
        watch.Observe(Status(2), null, TimeSpan.FromMinutes(12));
        watch.TakeSlowNotice().ShouldNotBeNull();

        watch.Observe(Status(3, "uploading"), null, TimeSpan.FromMinutes(13));
        watch.TakeSlowNotice().ShouldBeNull("uploading just began");
        watch.Observe(Status(4, "uploading"), null, TimeSpan.FromMinutes(13) + TimeSpan.FromSeconds(7.5 * 60));

        var notice = watch.TakeSlowNotice();
        notice.ShouldNotBeNull();
        notice.Stage.ShouldBe("uploading");
        notice.Deadline.ShouldBe(TimeSpan.FromMinutes(10));
    }

    [Fact]
    public void A_stage_with_no_deadline_never_gets_a_notice()
    {
        var watch = Watch();
        for (var minute = 0; minute < 120; minute++)
        {
            watch.Observe(Status(minute, "running"), null, TimeSpan.FromMinutes(minute));
            watch.TakeSlowNotice().ShouldBeNull($"minute {minute}");
        }
    }

    [Fact]
    public void The_notice_uses_a_custom_stage_deadline()
    {
        var watch = new HeartbeatWatch(false, First, Stale, new Dictionary<string, TimeSpan> { ["model-loading"] = TimeSpan.FromSeconds(40) });
        watch.Observe(Status(1), null, TimeSpan.Zero);
        watch.Observe(Status(2), null, TimeSpan.FromSeconds(30));

        watch.TakeSlowNotice().ShouldNotBeNull().Deadline.ShouldBe(TimeSpan.FromSeconds(40));
    }

    [Fact]
    public void Snooze_gives_a_stuck_stage_a_fresh_allowance_and_a_stale_heartbeat_a_fresh_window()
    {
        var watch = Watch();
        watch.Observe(Status(1), null, TimeSpan.Zero);
        watch.Observe(Status(1), null, Stale).ShouldNotBeNull().Code.ShouldBe(RunErrorCodes.WorkerHeartbeatStale);

        watch.Snooze(Stale);

        watch.Observe(Status(1), null, Stale + TimeSpan.FromSeconds(1)).ShouldBeNull("the window restarted at the snooze");
        watch.Observe(Status(1), null, Stale + Stale).ShouldNotBeNull().Code.ShouldBe(RunErrorCodes.WorkerHeartbeatStale);
    }

    [Fact]
    public void Snooze_restarts_the_stage_deadline()
    {
        var watch = Watch();
        watch.Observe(Status(1), null, TimeSpan.Zero);
        watch.Observe(Status(2), null, ModelLoading).ShouldNotBeNull().Code.ShouldBe(RunErrorCodes.WorkerStageStuck);

        watch.Snooze(ModelLoading);

        watch.Observe(Status(3), null, ModelLoading + TimeSpan.FromMinutes(14)).ShouldBeNull();
        watch.Observe(Status(4), null, ModelLoading + ModelLoading).ShouldNotBeNull().Code.ShouldBe(RunErrorCodes.WorkerStageStuck);
    }

    [Fact]
    public void Snooze_restarts_the_wait_for_a_first_heartbeat()
    {
        var watch = Watch();
        watch.Observe(null, null, First).ShouldNotBeNull().Code.ShouldBe(RunErrorCodes.WorkerNoHeartbeat);

        watch.Snooze(First);

        watch.Observe(null, null, First + TimeSpan.FromMinutes(24)).ShouldBeNull();
        watch.Observe(null, null, First + First).ShouldNotBeNull().Code.ShouldBe(RunErrorCodes.WorkerNoHeartbeat);
    }
}
