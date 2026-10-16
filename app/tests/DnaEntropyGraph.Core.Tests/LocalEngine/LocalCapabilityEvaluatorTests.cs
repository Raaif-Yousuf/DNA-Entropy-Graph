using DnaEntropyGraph.Core.LocalEngine;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Core.Tests.LocalEngine;

public sealed class LocalCapabilityEvaluatorTests
{
    private const long Gib = 1024L * 1024 * 1024;

    private static GpuDetection Found(long mib, string driver = "551.61", string name = "NVIDIA GeForce RTX 4090")
        => new(GpuDetectionStatus.Found, [new GpuInfo(name, mib, driver)]);

    [Fact]
    public void A_24_GB_card_with_a_new_driver_and_disk_is_eligible()
    {
        var result = LocalCapabilityEvaluator.Evaluate(Found(24564), 100 * Gib);

        result.Eligible.ShouldBeTrue();
        result.Blockers.ShouldBeEmpty();
        result.Gpu!.Name.ShouldBe("NVIDIA GeForce RTX 4090");
    }

    [Fact]
    public void A_card_that_reports_23034_MiB_but_is_sold_as_24_GB_is_eligible()
    {
        LocalCapabilityEvaluator.Evaluate(Found(23034, name: "NVIDIA L4"), 100 * Gib).Eligible.ShouldBeTrue();
    }

    [Fact]
    public void An_8_GB_card_is_blocked_for_memory_only()
    {
        var result = LocalCapabilityEvaluator.Evaluate(Found(8192), 100 * Gib);

        result.Eligible.ShouldBeFalse();
        result.Blockers.ShouldBe([LocalBlocker.NotEnoughGpuMemory]);
    }

    [Fact]
    public void An_old_driver_is_blocked()
    {
        LocalCapabilityEvaluator.Evaluate(Found(24564, "535.98"), 100 * Gib).Blockers.ShouldBe([LocalBlocker.DriverTooOld]);
    }

    [Fact]
    public void Driver_550_exactly_is_enough()
    {
        LocalCapabilityEvaluator.Evaluate(Found(24564, "550.0"), 100 * Gib).Eligible.ShouldBeTrue();
    }

    [Fact]
    public void An_unreadable_driver_version_is_blocked_not_assumed_fine()
    {
        LocalCapabilityEvaluator.Evaluate(Found(24564, "unknown"), 100 * Gib).Blockers.ShouldBe([LocalBlocker.DriverTooOld]);
    }

    [Fact]
    public void Less_than_30_GB_free_is_blocked()
    {
        LocalCapabilityEvaluator.Evaluate(Found(24564), 29 * Gib).Blockers.ShouldBe([LocalBlocker.NotEnoughDisk]);
    }

    [Fact]
    public void No_nvidia_tool_at_all_is_NoNvidiaGpu_and_says_nothing_about_memory_or_driver()
    {
        var result = LocalCapabilityEvaluator.Evaluate(new GpuDetection(GpuDetectionStatus.NoNvidia, []), 100 * Gib);

        result.Eligible.ShouldBeFalse();
        result.Blockers.ShouldBe([LocalBlocker.NoNvidiaGpu]);
        result.Gpu.ShouldBeNull();
    }

    [Fact]
    public void A_tool_that_is_there_but_fails_is_its_own_blocker_not_NoNvidiaGpu()
    {
        // MEASURED 2026-10-15 (review of #119-#174): nvidia-smi present with the driver not loaded exits 9; the user HAS a card, so
        // "this PC has no NVIDIA card" would send them to the wrong fix.
        var result = LocalCapabilityEvaluator.Evaluate(new GpuDetection(GpuDetectionStatus.ToolFailed, []), 100 * Gib);

        result.Eligible.ShouldBeFalse();
        result.Blockers.ShouldBe([LocalBlocker.GpuToolFailed]);
        result.Gpu.ShouldBeNull();
    }

    [Fact]
    public void The_judged_card_keeps_its_uuid_so_the_worker_can_be_pinned_to_it()
    {
        var detection = new GpuDetection(GpuDetectionStatus.Found, [new GpuInfo("small", 8192, "551.61", "GPU-a"), new GpuInfo("big", 24564, "551.61", "GPU-b")]);

        LocalCapabilityEvaluator.Evaluate(detection, 100 * Gib).Gpu!.Uuid.ShouldBe("GPU-b");
    }
    [Fact]
    public void With_several_cards_the_one_with_the_most_memory_is_judged()
    {
        var detection = new GpuDetection(GpuDetectionStatus.Found, [new GpuInfo("small", 8192, "551.61"), new GpuInfo("big", 24564, "551.61")]);

        var result = LocalCapabilityEvaluator.Evaluate(detection, 100 * Gib);

        result.Eligible.ShouldBeTrue();
        result.Gpu!.Name.ShouldBe("big");
    }

    [Fact]
    public void Several_problems_are_all_listed()
    {
        LocalCapabilityEvaluator.Evaluate(Found(8192, "500.1"), Gib).Blockers
            .ShouldBe([LocalBlocker.NotEnoughGpuMemory, LocalBlocker.DriverTooOld, LocalBlocker.NotEnoughDisk]);
    }
}
