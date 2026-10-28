using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Cloud;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Core.Tests;

/// <summary>Issue #87: picking a model narrows the GPU, picking a GPU narrows the model, and a pin makes either side independent.</summary>
public class ModelGpuLinkerTests
{
    // The full catalog with every model offered, as it will look once issue #14 is decided to show them.
    private static readonly GpuCatalog All = GpuCatalog.Shipped.WithEverythingOffered();
    private static readonly ModelGpuLinker Linker = new(All);

    private static PlanState Start(string model = "evo2_7b", GpuTier gpu = GpuTier.CheapestAvailable, bool modelPinned = false, bool gpuPinned = false)
        => new(model, gpu, modelPinned, gpuPinned);

    // ---- picking a model ----

    [Fact]
    public void Picking_40b_with_an_automatic_gpu_resolves_to_two_h100_with_a_notice()
    {
        var result = Linker.ChooseModel(Start(), "evo2_40b");

        result.ResolvedGpu.ShouldBe(GpuTier.H100x2);
        result.Notice.ShouldBe(PlanNotice.GpuSwitched);
        result.Fit.ShouldBe(GpuFit.Fits);
        result.State.ModelId.ShouldBe("evo2_40b");
    }

    [Fact]
    public void Picking_40b_with_an_explicit_unpinned_l4_moves_the_gpu_up()
    {
        var result = Linker.ChooseModel(Start(gpu: GpuTier.L4), "evo2_40b");

        result.State.Gpu.ShouldBe(GpuTier.H100x2);
        result.Notice.ShouldBe(PlanNotice.GpuSwitched);
        result.Fit.ShouldBe(GpuFit.Fits);
    }

    [Fact]
    public void Pinning_l4_then_picking_40b_keeps_l4_and_reports_that_it_will_not_fit()
    {
        var result = Linker.ChooseModel(Start(gpu: GpuTier.L4, gpuPinned: true), "evo2_40b");

        result.State.Gpu.ShouldBe(GpuTier.L4);
        result.ResolvedGpu.ShouldBe(GpuTier.L4);
        result.Fit.ShouldBe(GpuFit.NeedsHopper);
        result.Notice.ShouldBe(PlanNotice.None);
    }

    [Fact]
    public void A_model_the_current_gpu_already_runs_leaves_the_gpu_alone()
    {
        var result = Linker.ChooseModel(Start(gpu: GpuTier.A100_80), "evo2_7b_262k");

        result.State.Gpu.ShouldBe(GpuTier.A100_80);
        result.Notice.ShouldBe(PlanNotice.None);
    }

    [Fact]
    public void Going_back_to_the_standard_model_with_an_automatic_gpu_returns_to_the_cheapest_l4_with_a_notice()
    {
        var result = Linker.ChooseModel(Start(model: "evo2_40b"), "evo2_7b");

        result.ResolvedGpu.ShouldBe(GpuTier.L4);
        result.Notice.ShouldBe(PlanNotice.GpuSwitched);
    }

    [Fact]
    public void Picking_the_model_already_chosen_changes_nothing()
    {
        var result = Linker.ChooseModel(Start(), "evo2_7b");

        result.Notice.ShouldBe(PlanNotice.None);
        result.ResolvedGpu.ShouldBe(GpuTier.L4);
    }

    [Fact]
    public void A_pinned_automatic_gpu_still_resolves_for_the_model()
    {
        var result = Linker.ChooseModel(Start(gpuPinned: true), "evo2_1b_base");

        result.ResolvedGpu.ShouldBe(GpuTier.H100);
        result.Fit.ShouldBe(GpuFit.Fits);
    }

    // ---- picking a GPU ----

    [Fact]
    public void Picking_two_h100_suggests_the_largest_model_that_fits_when_the_model_is_not_pinned()
    {
        var result = Linker.ChooseGpu(Start(), GpuTier.H100x2);

        result.State.ModelId.ShouldBe("evo2_40b");
        result.Notice.ShouldBe(PlanNotice.ModelSwitched);
        result.Fit.ShouldBe(GpuFit.Fits);
    }

    [Fact]
    public void Picking_l4_while_on_40b_drops_to_a_model_that_fits_when_the_model_is_not_pinned()
    {
        var result = Linker.ChooseGpu(Start(model: "evo2_40b", gpu: GpuTier.H100x2), GpuTier.L4);

        result.State.ModelId.ShouldBe("evo2_7b");
        result.Notice.ShouldBe(PlanNotice.ModelSwitched);
        result.Fit.ShouldBe(GpuFit.Fits);
    }

    [Fact]
    public void A_pinned_model_is_never_moved_by_a_gpu_pick_and_the_misfit_is_reported()
    {
        var result = Linker.ChooseGpu(Start(model: "evo2_40b", modelPinned: true), GpuTier.L4);

        result.State.ModelId.ShouldBe("evo2_40b");
        result.Fit.ShouldBe(GpuFit.NeedsHopper);
        result.Notice.ShouldBe(PlanNotice.None);
    }

    [Fact]
    public void A_bigger_gpu_does_not_swap_the_model_when_nothing_larger_fits()
    {
        var result = Linker.ChooseGpu(Start(model: "evo2_7b"), GpuTier.A100_80);

        result.State.ModelId.ShouldBe("evo2_7b");
        result.Notice.ShouldBe(PlanNotice.None);
    }

    [Fact]
    public void Choosing_automatic_leaves_the_model_and_resolves_for_it()
    {
        var result = Linker.ChooseGpu(Start(model: "evo2_40b", gpu: GpuTier.L4, gpuPinned: true), GpuTier.CheapestAvailable);

        result.State.ModelId.ShouldBe("evo2_40b");
        result.ResolvedGpu.ShouldBe(GpuTier.H100x2);
        result.Fit.ShouldBe(GpuFit.Fits);
    }

    [Fact]
    public void With_the_shipped_catalog_picking_a_bigger_gpu_keeps_the_only_offered_model()
    {
        var shipped = new ModelGpuLinker(GpuCatalog.Shipped);

        var result = shipped.ChooseGpu(Start(), GpuTier.A100_40);

        result.State.ModelId.ShouldBe("evo2_7b");
        result.ResolvedGpu.ShouldBe(GpuTier.A100_40);
        result.Fit.ShouldBe(GpuFit.Fits);
    }

    // ---- pins and locks ----

    [Fact]
    public void Pins_are_flags_only_and_change_nothing_else()
    {
        var pinned = Linker.SetGpuPinned(Start(gpu: GpuTier.L4), true);

        pinned.State.GpuPinned.ShouldBeTrue();
        pinned.State.Gpu.ShouldBe(GpuTier.L4);
        Linker.SetModelPinned(pinned.State, true).State.ModelPinned.ShouldBeTrue();
        Linker.SetGpuPinned(pinned.State, false).State.GpuPinned.ShouldBeFalse();
    }

    [Fact]
    public void The_locked_gpus_for_a_model_are_the_offered_ones_that_cannot_run_it()
    {
        var locks = Linker.GpuFits("evo2_40b");

        locks.Single(l => l.Tier == GpuTier.H100x2).Fit.ShouldBe(GpuFit.Fits);
        locks.Single(l => l.Tier == GpuTier.L4).Fit.ShouldBe(GpuFit.NeedsHopper);
        locks.Single(l => l.Tier == GpuTier.H100).Fit.ShouldBe(GpuFit.NeedsMoreGpus);
    }

    [Fact]
    public void A_model_no_offered_gpu_can_run_resolves_to_nothing()
    {
        var shipped = new ModelGpuLinker(GpuCatalog.Shipped);

        var result = shipped.ChooseModel(Start(), "evo2_40b");

        result.ResolvedGpu.ShouldBeNull();
        result.Fit.ShouldBe(GpuFit.NoOfferedGpu);
    }
}
