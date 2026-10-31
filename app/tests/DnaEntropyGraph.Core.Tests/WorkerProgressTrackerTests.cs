using DnaEntropyGraph.Core.Contract;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Core.Tests;

/// <summary>Issue #1086: the run page's progress is the worker's status.json percent and window counters, handed on once per CHANGE, never once per poll.</summary>
public class WorkerProgressTrackerTests
{
    private const string Running42 = "{\"stage\":\"running\",\"percent\":42.5,\"detail\":{\"input\":\"in1\",\"contig\":\"secret-name\",\"window\":5,\"windows\":11,\"direction\":\"forward\"},\"heartbeatSeq\":3,\"worker\":{\"version\":\"1.0.0\"}}";

    [Fact]
    public void The_status_reader_reads_percent_and_the_window_counters()
    {
        var status = WorkerStatusReader.TryParseStatus(Running42)!;

        status.Percent.ShouldBe(42.5);
        status.Window.ShouldBe(5);
        status.Windows.ShouldBe(11);
    }

    [Fact]
    public void A_status_without_percent_or_detail_reads_them_as_unknown()
    {
        var status = WorkerStatusReader.TryParseStatus("{\"stage\":\"booting\",\"heartbeatSeq\":1}")!;

        status.Percent.ShouldBeNull();
        status.Window.ShouldBeNull();
        status.Windows.ShouldBeNull();
    }

    [Fact]
    public void The_event_carries_the_stage_the_fraction_and_the_window_text_and_never_a_contig_or_input_name()
    {
        var tracker = new WorkerProgressTracker();

        var ev = tracker.Next(WorkerStatusReader.TryParseStatus(Running42));

        ev.ShouldNotBeNull();
        ev.Stage.ShouldBe("running");
        ev.FractionComplete.ShouldBe(0.425, 1e-9);
        ev.Message.ShouldBe("Window 5 of 11");
        ev.Message.ShouldNotContain("secret-name");
    }

    [Fact]
    public void An_unchanged_status_gives_no_second_event_and_a_changed_one_does()
    {
        var tracker = new WorkerProgressTracker();
        var status = WorkerStatusReader.TryParseStatus(Running42)!;

        tracker.Next(status).ShouldNotBeNull();
        tracker.Next(status).ShouldBeNull("the same progress is not re-sent every poll");
        tracker.Next(status with { HeartbeatSeq = 4 }).ShouldBeNull("a heartbeat tick alone is not progress");
        tracker.Next(status with { Percent = 50, Window = 6 })!.Message.ShouldBe("Window 6 of 11");
    }

    [Fact]
    public void A_stage_with_no_percent_reports_the_stage_and_keeps_the_last_fraction()
    {
        var tracker = new WorkerProgressTracker();
        tracker.Next(WorkerStatusReader.TryParseStatus(Running42));

        var ev = tracker.Next(WorkerStatusReader.TryParseStatus("{\"stage\":\"uploading\",\"heartbeatSeq\":9}"));

        ev.ShouldNotBeNull();
        ev.Stage.ShouldBe("uploading");
        ev.Message.ShouldBe("uploading");
        ev.FractionComplete.ShouldBe(0.425, 1e-9);
    }

    [Theory]
    [InlineData(-5.0, 0.0)]
    [InlineData(250.0, 1.0)]
    public void A_percent_outside_zero_to_a_hundred_is_clamped(double percent, double fraction)
    {
        var ev = new WorkerProgressTracker().Next(new WorkerStatusSnapshot("running", 1, null, "1", Percent: percent));

        ev!.FractionComplete.ShouldBe(fraction);
    }

    [Fact]
    public void No_status_or_a_status_with_no_stage_gives_no_event()
    {
        var tracker = new WorkerProgressTracker();

        tracker.Next(null).ShouldBeNull();
        tracker.Next(new WorkerStatusSnapshot(null, 1, null, null)).ShouldBeNull();
    }
}
