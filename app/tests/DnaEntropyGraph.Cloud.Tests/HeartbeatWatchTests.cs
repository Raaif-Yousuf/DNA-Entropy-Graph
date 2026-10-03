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

    [Fact]
    public void Progress_is_wanted_only_for_a_GPU_tier_with_a_worker_heartbeat_and_no_verdict_yet()
    {
        var watch = Watch();
        watch.NeedsProgress.ShouldBeFalse("no worker heartbeat yet");
        watch.Observe(Status(1), null, TimeSpan.Zero);
        watch.NeedsProgress.ShouldBeTrue();
        Watch(expectsGpu: false).NeedsProgress.ShouldBeFalse();
    }
}
