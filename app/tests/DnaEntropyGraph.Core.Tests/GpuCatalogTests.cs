using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Cloud;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Core.Tests;

/// <summary>Issue #87: the shipped model and GPU catalog is the one place the hardware facts and the issue #14 visibility live.</summary>
public class GpuCatalogTests
{
    private static readonly GpuCatalog Shipped = GpuCatalog.Shipped;

    [Theory]
    [InlineData("evo2_7b", GpuTier.L4, GpuFit.Fits)]
    [InlineData("evo2_7b", GpuTier.A100_40, GpuFit.Fits)]
    [InlineData("evo2_7b", GpuTier.A100_80, GpuFit.Fits)]
    [InlineData("evo2_7b", GpuTier.H100, GpuFit.Fits)]
    [InlineData("evo2_7b_262k", GpuTier.L4, GpuFit.Fits)]
    [InlineData("evo2_1b_base", GpuTier.L4, GpuFit.NeedsHopper)]
    [InlineData("evo2_1b_base", GpuTier.A100_80, GpuFit.NeedsHopper)]
    [InlineData("evo2_1b_base", GpuTier.H100, GpuFit.Fits)]
    [InlineData("evo2_40b", GpuTier.A100_80, GpuFit.NeedsHopper)]
    [InlineData("evo2_40b", GpuTier.H100, GpuFit.NeedsMoreGpus)]
    [InlineData("evo2_40b", GpuTier.H100x2, GpuFit.Fits)]
    public void A_model_fits_a_gpu_by_precision_count_and_memory(string model, GpuTier gpu, GpuFit expected)
        => Shipped.FitOf(model, gpu).ShouldBe(expected);

    [Fact]
    public void Memory_is_checked_when_the_hardware_family_is_right()
    {
        var catalog = new GpuCatalog(
            [new ModelEntry("big", "bf16", 70, MinGpuCount: 1, MinVramGbPerGpu: 48, NeedsHopper: false, Offered: true)],
            [new GpuEntry(GpuTier.L4, "g2-standard-8", "nvidia-l4", QuotaMetrics.NvidiaL4Gpus, 1, 24, false, false, 8192, true)]);

        catalog.FitOf("big", GpuTier.L4).ShouldBe(GpuFit.NeedsMoreMemory);
    }

    [Fact]
    public void An_unknown_model_does_not_fit_anything_and_says_so()
        => Shipped.FitOf("evo2_nope", GpuTier.L4).ShouldBe(GpuFit.UnknownModel);

    [Fact]
    public void Today_only_the_standard_7b_model_and_the_three_tested_gpus_are_offered()
    {
        // The state of issue #14 (an owner DECISION, open): the app has offered only evo2_7b so far. When #14 is decided, flip
        // Offered in GpuCatalog.Shipped and update this one assertion; nothing else in the app changes.
        Shipped.OfferedModels.Select(m => m.Id).ShouldBe(["evo2_7b"]);
        Shipped.OfferedGpus.Select(g => g.Tier).ShouldBe([GpuTier.L4, GpuTier.A100_40, GpuTier.A100_80]);
    }

    [Fact]
    public void Every_offered_model_has_an_offered_gpu_that_runs_it()
    {
        foreach (var model in Shipped.OfferedModels)
        {
            Shipped.OfferedGpus.ShouldContain(g => Shipped.FitOf(model.Id, g.Tier) == GpuFit.Fits, $"{model.Id} is offered but no offered GPU can run it.");
        }
    }

    [Fact]
    public void Gpus_are_listed_cheapest_first_so_auto_picks_the_cheapest_that_fits()
        => Shipped.Gpus.Select(g => g.Tier).ShouldBe([GpuTier.L4, GpuTier.A100_40, GpuTier.A100_80, GpuTier.H100, GpuTier.H100x2]);

    [Fact]
    public void Every_gpu_names_a_quota_metric_the_preflight_knows()
    {
        foreach (var gpu in Shipped.Gpus)
        {
            QuotaMetrics.ForAccelerator(gpu.Accelerator).ShouldBe(gpu.QuotaMetric, $"{gpu.Tier}: accelerator {gpu.Accelerator} does not resolve to its own quota metric.");
        }
    }

    [Fact]
    public void The_machine_type_a_run_starts_is_read_from_the_catalog()
    {
        foreach (var gpu in Shipped.Gpus)
        {
            CloudJobRequestFactory.MachineTypeFor(gpu.Tier).ShouldBe(gpu.MachineType);
        }

        CloudJobRequestFactory.MachineTypeFor(GpuTier.CheapestAvailable).ShouldBe("g2-standard-8");
        CloudJobRequestFactory.MachineTypeFor(GpuTier.H100x2).ShouldBe("a3-highgpu-2g");
    }

    [Fact]
    public void Every_concrete_tier_is_in_the_catalog()
    {
        foreach (var tier in Enum.GetValues<GpuTier>().Where(t => t != GpuTier.CheapestAvailable))
        {
            Shipped.Gpus.ShouldContain(g => g.Tier == tier, $"{tier} has no catalog entry.");
        }
    }

    [Fact]
    public void Every_hopper_shape_is_spot_only_and_no_other_is()
    {
        // Source: Appendix B section 4.1 ("not offered on-demand for small A3 shapes", i.e. a3-highgpu-1g and 2g alike). THEORY (unverified
        // against a real project): no live capacity read has confirmed it for the 1g shape, so both rows follow the one stated source.
        foreach (var gpu in Shipped.Gpus)
        {
            gpu.SpotOnly.ShouldBe(gpu.IsHopper, $"{gpu.Tier}: SpotOnly must follow the A3 rule in Appendix B section 4.1.");
        }
    }

    [Fact]
    public void The_window_ceiling_is_known_only_where_it_was_measured()
    {
        Shipped.Gpu(GpuTier.L4).ContextCeiling.ShouldBe(8192);
        Shipped.Gpu(GpuTier.A100_40).ContextCeiling.ShouldBeNull();
    }
}
