using DnaEntropyGraph.Core.Cloud;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Cloud.Tests;

/// <summary>Issue #498: the heartbeat judgement, alone, on a scripted clock and scripted file contents.</summary>
public class HeartbeatWatchTests
{
    private static readonly TimeSpan FirstDeadline = TimeSpan.FromMinutes(25);
    private static readonly TimeSpan Stale = TimeSpan.FromMinutes(10);

    private static string Status(long seq, string updatedAt = "2026-09-18T15:20:03Z", string version = "1.0.0")
        => $$$"""{"schema":1,"stage":"running","heartbeatSeq":{{{seq}}},"updatedAt":"{{{updatedAt}}}","worker":{"version":"{{{version}}}"}}""";

    private const string NoGpuProgress = """{"message":"worker starting; GPU: no GPU detected; free disk 9 GB"}""";
    private const string GpuProgress = """{"message":"worker starting; GPU: NVIDIA L4 (driver 580); free disk 9 GB"}""";

    private static HeartbeatWatch Watch(bool expectsGpu = true) => new(expectsGpu, FirstDeadline, Stale);

    [Fact]
    public void No_worker_status_before_the_first_heartbeat_deadline_is_fine()
        => Watch().Observe(null, null, FirstDeadline - TimeSpan.FromSeconds(1)).ShouldBeNull();

    [Fact]
    public void No_worker_status_at_the_first_heartbeat_deadline_is_no_heartbeat()
    {
        var verdict = Watch().Observe(null, null, FirstDeadline);

        verdict.ShouldNotBeNull();
        verdict.Code.ShouldBe(RunErrorCodes.WorkerNoHeartbeat);
    }

    [Fact]
    public void A_startup_script_status_alone_never_counts_as_a_heartbeat()
    {
        var startupOnly = """{"stage":"installing","heartbeatSeq":0,"worker":{"version":""}}""";

        Watch().Observe(startupOnly, null, FirstDeadline)!.Code.ShouldBe(RunErrorCodes.WorkerNoHeartbeat);
    }

    [Fact]
    public void A_heartbeat_that_keeps_changing_is_never_stale_however_long_the_run()
    {
        var watch = Watch();
        for (var minute = 0; minute < 120; minute++)
        {
            watch.Observe(Status(minute), GpuProgress, TimeSpan.FromMinutes(minute)).ShouldBeNull($"minute {minute}");
        }
    }

    [Fact]
    public void A_heartbeat_that_stops_changing_is_stale_after_the_limit_measured_from_the_last_change()
    {
        var watch = Watch();
        watch.Observe(Status(5), GpuProgress, TimeSpan.FromMinutes(1)).ShouldBeNull();
        watch.Observe(Status(6), GpuProgress, TimeSpan.FromMinutes(2)).ShouldBeNull();

        watch.Observe(Status(6), GpuProgress, TimeSpan.FromMinutes(2) + Stale - TimeSpan.FromSeconds(1)).ShouldBeNull();
        var verdict = watch.Observe(Status(6), GpuProgress, TimeSpan.FromMinutes(2) + Stale);

        verdict.ShouldNotBeNull();
        verdict.Code.ShouldBe(RunErrorCodes.WorkerHeartbeatStale);
    }

    [Fact]
    public void A_changed_updatedAt_alone_counts_as_a_heartbeat()
    {
        var watch = Watch();
        watch.Observe(Status(5, "2026-01-01T00:00:00Z"), GpuProgress, TimeSpan.Zero).ShouldBeNull();

        watch.Observe(Status(5, "2026-01-01T00:00:30Z"), GpuProgress, Stale + TimeSpan.FromMinutes(1)).ShouldBeNull();
    }

    [Fact]
    public void A_first_progress_line_with_no_GPU_on_a_GPU_tier_is_gpu_not_visible_at_once()
    {
        var verdict = Watch(expectsGpu: true).Observe(Status(1), NoGpuProgress, TimeSpan.Zero);

        verdict.ShouldNotBeNull();
        verdict.Code.ShouldBe(RunErrorCodes.GpuNotVisible);
        verdict.DeleteVm.ShouldBeTrue();
    }

    [Fact]
    public void No_GPU_on_a_CPU_tier_is_expected_and_not_a_failure()
        => Watch(expectsGpu: false).Observe(Status(1), NoGpuProgress, TimeSpan.Zero).ShouldBeNull();

    [Fact]
    public void A_GPU_that_was_confirmed_once_is_not_looked_for_again()
    {
        var watch = Watch();
        watch.Observe(Status(1), GpuProgress, TimeSpan.Zero).ShouldBeNull();

        watch.NeedsProgress.ShouldBeFalse();
    }

    // ---- #90: per-stage deadlines (docs/job_contract.md section 5) ----

    private static string StageStatus(string stage, long seq, bool worker = true)
        => $$$"""{"schema":1,"stage":"{{{stage}}}","heartbeatSeq":{{{seq}}},"updatedAt":"2026-09-18T15:20:03Z","worker":{"version":"{{{(worker ? "1.0.0" : "")}}}"}}""";

    [Theory]
    [InlineData("booting", 8, false)]
    [InlineData("installing", 15, false)]
    [InlineData("restoring-cache", 15, true)]
    [InlineData("model-loading", 15, true)]
    [InlineData("uploading", 10, true)]
    public void A_stage_that_outlasts_its_deadline_is_stuck_even_while_the_heartbeat_ticks(string stage, int minutes, bool worker)
    {
        var watch = new HeartbeatWatch(false, TimeSpan.FromHours(2), TimeSpan.FromHours(2));
        var limit = TimeSpan.FromMinutes(minutes);
        long seq = 0;
        for (var t = TimeSpan.Zero; t < limit; t += TimeSpan.FromSeconds(30))
        {
            watch.Observe(StageStatus(stage, ++seq, worker), null, t).ShouldBeNull($"{stage} at {t}");
        }

        var verdict = watch.Observe(StageStatus(stage, ++seq, worker), null, limit);

        verdict.ShouldNotBeNull();
        verdict.Code.ShouldBe(RunErrorCodes.WorkerStageStuck);
        verdict.DeleteVm.ShouldBeFalse();
        verdict.Message.ShouldContain(stage);
    }

    [Fact]
    public void Moving_to_the_next_stage_restarts_the_stage_clock()
    {
        var watch = new HeartbeatWatch(false, TimeSpan.FromHours(2), TimeSpan.FromHours(2));
        watch.Observe(StageStatus("model-loading", 1), null, TimeSpan.Zero).ShouldBeNull();
        watch.Observe(StageStatus("model-loading", 2), null, TimeSpan.FromMinutes(14)).ShouldBeNull();
        watch.Observe(StageStatus("running", 3), null, TimeSpan.FromMinutes(14.5)).ShouldBeNull();

        watch.Observe(StageStatus("running", 4), null, TimeSpan.FromHours(1.5)).ShouldBeNull("running has no stage deadline; the result limit and the heartbeat judge it");
    }

    [Fact]
    public void A_stage_deadline_override_replaces_the_default_for_that_stage()
    {
        var watch = new HeartbeatWatch(false, TimeSpan.FromHours(2), TimeSpan.FromHours(2), new Dictionary<string, TimeSpan> { ["model-loading"] = TimeSpan.FromSeconds(60) });
        watch.Observe(StageStatus("model-loading", 1), null, TimeSpan.Zero).ShouldBeNull();

        watch.Observe(StageStatus("model-loading", 2), null, TimeSpan.FromSeconds(60))!.Code.ShouldBe(RunErrorCodes.WorkerStageStuck);
    }

    [Fact]
    public void Progress_is_wanted_only_for_a_GPU_tier_with_a_worker_heartbeat_and_no_verdict_yet()
    {
        var watch = Watch();
        watch.NeedsProgress.ShouldBeFalse("no worker heartbeat yet");
        watch.Observe(Status(1), null, TimeSpan.Zero);
        watch.NeedsProgress.ShouldBeTrue();
        Watch(expectsGpu: false).NeedsProgress.ShouldBeFalse();
    }

    // ---- #90 review: a resume does not grant a stuck stage a fresh allowance ----

    private static string AgedStageStatus(string stage, long seq, string stageStartedAt, string updatedAt)
        => $$$"""{"schema":1,"stage":"{{{stage}}}","stageStartedAt":"{{{stageStartedAt}}}","heartbeatSeq":{{{seq}}},"updatedAt":"{{{updatedAt}}}","worker":{"version":"1.0.0"}}""";

    [Fact]
    public void A_stage_already_old_at_the_first_look_is_stuck_after_only_the_time_it_has_left()
    {
        // model-loading's deadline is 15 minutes. The worker says the stage began 14 minutes before its last write.
        var watch = Watch();
        var status = AgedStageStatus("model-loading", 40, "2026-09-18T15:06:03Z", "2026-09-18T15:20:03Z");

        watch.Observe(status, null, TimeSpan.Zero).ShouldBeNull("14 of 15 minutes used");
        watch.Observe(AgedStageStatus("model-loading", 41, "2026-09-18T15:06:03Z", "2026-09-18T15:20:33Z"), null, TimeSpan.FromSeconds(30)).ShouldBeNull();

        var verdict = watch.Observe(AgedStageStatus("model-loading", 42, "2026-09-18T15:06:03Z", "2026-09-18T15:21:03Z"), null, TimeSpan.FromSeconds(60));

        verdict.ShouldNotBeNull("a resumed watch must count the stage's age on the worker's own clock, not restart at 0");
        verdict.Code.ShouldBe(RunErrorCodes.WorkerStageStuck);
    }

    [Fact]
    public void A_status_without_a_stage_start_time_still_starts_the_clock_at_the_first_look()
    {
        var watch = Watch();
        watch.Observe(StageStatus("model-loading", 1), null, TimeSpan.Zero).ShouldBeNull();
        watch.Observe(StageStatus("model-loading", 2), null, TimeSpan.FromMinutes(14)).ShouldBeNull();
        watch.Observe(StageStatus("model-loading", 3), null, TimeSpan.FromMinutes(15))!.Code.ShouldBe(RunErrorCodes.WorkerStageStuck);
    }

    [Fact]
    public void An_unparseable_or_future_stage_start_time_is_ignored_not_trusted()
    {
        var watch = Watch();
        watch.Observe(AgedStageStatus("model-loading", 1, "garbage", "2026-09-18T15:20:03Z"), null, TimeSpan.Zero).ShouldBeNull();
        var future = Watch();
        future.Observe(AgedStageStatus("model-loading", 1, "2026-09-18T16:00:00Z", "2026-09-18T15:20:03Z"), null, TimeSpan.Zero).ShouldBeNull();
        future.Observe(AgedStageStatus("model-loading", 2, "2026-09-18T16:00:00Z", "2026-09-18T15:20:33Z"), null, TimeSpan.FromMinutes(14)).ShouldBeNull("a stage start after the last write is nonsense: age is 0");
    }
}
