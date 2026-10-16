using System.Text;
using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Cloud;
using DnaEntropyGraph.Core.Cost;
using DnaEntropyGraph.Core.Inputs;
using DnaEntropyGraph.Presentation.Services;
using DnaEntropyGraph.Presentation.ViewModels;
using NSubstitute;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Presentation.Tests;

/// <summary>Issue #87: the New run page's linked model and GPU pickers drive the run's options, its machine type and its estimate.</summary>
public sealed class GpuPlanTests : IDisposable
{
    private const string Dna = "ACGTACGTACGTACGT";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "deg-gpuplan-" + Guid.NewGuid().ToString("N"));
    private readonly IJobEngine _jobEngine = Substitute.For<IJobEngine>();
    private readonly ICostEstimateService _estimates = Substitute.For<ICostEstimateService>();
    private readonly KeyStrings _strings = new();
    private RunOptions? _started;

    public GpuPlanTests()
    {
        Directory.CreateDirectory(_dir);
        _jobEngine.StartRunAsync(Arg.Do<RunOptions>(o => _started = o), Arg.Any<CancellationToken>()).Returns(Task.FromResult("job-1"));
        _estimates.EstimateAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<long?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(CostEstimateResult.Unavailable(EstimateUnavailable.UnknownSize)));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // best effort
        }
    }

    private sealed class KeyStrings : IStringResourceProvider
    {
        public string GetString(string key) => key;
    }

    private NewRunViewModel NewViewModel(GpuCatalog? catalog = null) => new(
        Substitute.For<IFilePicker>(),
        _jobEngine,
        Substitute.For<INavigator>(),
        _strings,
        new LocalPastedInputStore(Path.Combine(_dir, "appdata")),
        _estimates,
        new OutputSelectionStore(Substitute.For<ISettingsStore>()),
        Substitute.For<IDiagnosticsLog>(),
        new GpuPlanViewModel(catalog ?? GpuCatalog.Shipped, _strings));

    private async Task<NewRunViewModel> WithOneFile(GpuCatalog? catalog = null)
    {
        var viewModel = NewViewModel(catalog);
        var path = Path.Combine(_dir, "a.fasta");
        File.WriteAllText(path, ">a\n" + Dna + "\n", new UTF8Encoding(false));
        await viewModel.AddDroppedCommand.ExecuteAsync(new DroppedItems([path], 0, false));
        return viewModel;
    }

    private static GpuChoiceItem Gpu(NewRunViewModel vm, GpuTier tier) => vm.Gpu.Gpus.Single(g => g.Tier == tier);

    private static ModelChoiceItem Model(NewRunViewModel vm, string id) => vm.Gpu.Models.Single(m => m.Id == id);

    private static readonly GpuCatalog Everything = GpuCatalog.Shipped.WithEverythingOffered();

    // ---- the wired observable ----

    [Fact]
    public async Task Choosing_the_a100_makes_the_run_options_and_the_cloud_request_name_the_a100_machine()
    {
        var vm = await WithOneFile();

        vm.Gpu.SelectedGpu = Gpu(vm, GpuTier.A100_40);
        await vm.StartRunCommand.ExecuteAsync(null);

        _started.ShouldNotBeNull();
        _started.GpuTier.ShouldBe(GpuTier.A100_40);
        var request = CloudJobRequestFactory.Create(
            _started, "job-1", "p", "i", "0.0.1", null, [new DnaEntropyGraph.Core.Contract.StagedInput(@"C:\x\a.fasta", "a.fasta")], @"C:\out");
        request.Spec.MachineType.ShouldBe("a2-highgpu-1g");
    }

    [Fact]
    public async Task The_untouched_page_still_starts_the_cheapest_automatic_l4_run()
    {
        var vm = await WithOneFile();

        await vm.StartRunCommand.ExecuteAsync(null);

        _started!.GpuTier.ShouldBe(GpuTier.CheapestAvailable);
        _started.ModelId.ShouldBe("evo2_7b");
        CloudJobRequestFactory.MachineTypeFor(_started.GpuTier).ShouldBe("g2-standard-8");
    }

    [Fact]
    public async Task The_estimate_is_asked_for_the_machine_of_the_chosen_gpu()
    {
        var vm = await WithOneFile();
        _estimates.ClearReceivedCalls();

        vm.Gpu.SelectedGpu = Gpu(vm, GpuTier.A100_80);

        await WaitUntil(() => _estimates.ReceivedCalls().Any());
        await _estimates.Received().EstimateAsync("a2-ultragpu-1g", false, Dna.Length, 150, Arg.Any<CancellationToken>());
    }

    private static async Task WaitUntil(Func<bool> done)
    {
        for (var i = 0; i < 200 && !done(); i++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }

    // ---- what is offered ----

    [Fact]
    public void Today_there_is_one_model_so_the_model_picker_is_not_shown_and_three_gpus_plus_automatic_are_offered()
    {
        var vm = NewViewModel();

        vm.Gpu.HasModelChoice.ShouldBeFalse();
        vm.Gpu.Models.Select(m => m.Id).ShouldBe(["evo2_7b"]);
        vm.Gpu.Gpus.Select(g => g.Tier).ShouldBe([GpuTier.CheapestAvailable, GpuTier.L4, GpuTier.A100_40, GpuTier.A100_80]);
        vm.Gpu.SelectedGpu.Tier.ShouldBe(GpuTier.CheapestAvailable);
    }

    // ---- linking ----

    [Fact]
    public async Task Choosing_40b_raises_the_gpu_and_says_so()
    {
        var vm = await WithOneFile(Everything);

        vm.Gpu.SelectedModel = Model(vm, "evo2_40b");

        vm.Gpu.Tier.ShouldBe(GpuTier.H100x2);
        vm.Gpu.HasNotice.ShouldBeTrue();
        vm.Gpu.HasWarning.ShouldBeFalse();
        vm.StartRunCommand.CanExecute(null).ShouldBeTrue();
    }

    [Fact]
    public async Task Locking_l4_then_choosing_40b_warns_and_the_run_waits_for_a_confirmation()
    {
        var vm = await WithOneFile(Everything);
        vm.Gpu.SelectedGpu = Gpu(vm, GpuTier.L4);
        vm.Gpu.GpuPinned = true;

        vm.Gpu.SelectedModel = Model(vm, "evo2_40b");

        vm.Gpu.Tier.ShouldBe(GpuTier.L4);
        vm.Gpu.HasWarning.ShouldBeTrue();
        vm.Gpu.WarningText.ShouldBe("NewRunGpuMisfit");
        vm.StartRunCommand.CanExecute(null).ShouldBeFalse();

        vm.Gpu.IsConfirmed = true;
        vm.StartRunCommand.CanExecute(null).ShouldBeTrue();
        await vm.StartRunCommand.ExecuteAsync(null);
        _started!.ModelId.ShouldBe("evo2_40b");
        _started.GpuTier.ShouldBe(GpuTier.L4);
    }

    [Fact]
    public async Task Any_change_withdraws_the_confirmation()
    {
        var vm = await WithOneFile(Everything);
        vm.Gpu.SelectedGpu = Gpu(vm, GpuTier.L4);
        vm.Gpu.GpuPinned = true;
        vm.Gpu.SelectedModel = Model(vm, "evo2_40b");
        vm.Gpu.IsConfirmed = true;

        vm.Gpu.SelectedModel = Model(vm, "evo2_1b_base");

        vm.Gpu.IsConfirmed.ShouldBeFalse();
        vm.StartRunCommand.CanExecute(null).ShouldBeFalse();
    }

    [Fact]
    public async Task Choosing_the_big_gpu_moves_an_unpinned_model_and_a_pinned_model_stays()
    {
        var vm = await WithOneFile(Everything);

        vm.Gpu.SelectedGpu = Gpu(vm, GpuTier.H100x2);
        vm.Gpu.SelectedModel.Id.ShouldBe("evo2_40b");

        vm.Gpu.ModelPinned = true;
        vm.Gpu.SelectedGpu = Gpu(vm, GpuTier.L4);
        vm.Gpu.SelectedModel.Id.ShouldBe("evo2_40b");
        vm.Gpu.HasWarning.ShouldBeTrue();
    }

    [Fact]
    public async Task A_gpu_that_cannot_run_the_chosen_model_is_marked_locked_with_the_reason()
    {
        var vm = await WithOneFile(Everything);

        vm.Gpu.SelectedModel = Model(vm, "evo2_40b");

        Gpu(vm, GpuTier.L4).IsLocked.ShouldBeTrue();
        Gpu(vm, GpuTier.L4).Label.ShouldBe("NewRunGpuLocked");
        Gpu(vm, GpuTier.H100x2).IsLocked.ShouldBeFalse();
        Gpu(vm, GpuTier.H100x2).Label.ShouldBe("NewRunGpu_H100x2");
    }

    // ---- Spot-only shapes ----

    [Fact]
    public async Task A_spot_only_tier_makes_the_run_options_and_the_estimate_ask_for_spot()
    {
        var vm = await WithOneFile(Everything);
        _estimates.ClearReceivedCalls();

        vm.Gpu.SelectedModel = Model(vm, "evo2_40b");
        await vm.StartRunCommand.ExecuteAsync(null);

        _started!.GpuTier.ShouldBe(GpuTier.H100x2);
        _started.SpotVm.ShouldBeTrue("a3-highgpu-2g is Spot or Flex-start only");
        await WaitUntil(() => _estimates.ReceivedCalls().Any());
        await _estimates.Received().EstimateAsync("a3-highgpu-2g", true, Dna.Length, 150, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_on_demand_tier_keeps_spot_off()
    {
        var vm = await WithOneFile(Everything);

        vm.Gpu.SelectedGpu = Gpu(vm, GpuTier.A100_40);
        await vm.StartRunCommand.ExecuteAsync(null);

        _started!.SpotVm.ShouldBeFalse();
    }

    // ---- nothing fits ----

    [Fact]
    public async Task A_model_no_offered_card_can_run_cannot_be_run_even_when_ticked_and_says_what_to_do()
    {
        // 40B offered, but no Hopper card offered: an honest dead end, not a run on the L4.
        var catalog = new GpuCatalog(Everything.Models, GpuCatalog.Shipped.Gpus);
        var vm = await WithOneFile(catalog);

        vm.Gpu.SelectedModel = Model(vm, "evo2_40b");

        vm.Gpu.HasWarning.ShouldBeTrue();
        vm.Gpu.WarningText.ShouldBe("NewRunGpuNoCard");
        vm.Gpu.CanConfirm.ShouldBeFalse();
        vm.Gpu.IsConfirmed = true;
        vm.Gpu.IsRunnable.ShouldBeFalse();
        vm.StartRunCommand.CanExecute(null).ShouldBeFalse();
    }

    // ---- pins do not undo a confirmation ----

    [Fact]
    public async Task Toggling_a_lock_keeps_the_confirmation_and_the_notice_when_the_pair_did_not_change()
    {
        var vm = await WithOneFile(Everything);
        vm.Gpu.SelectedGpu = Gpu(vm, GpuTier.L4);
        vm.Gpu.GpuPinned = true;
        vm.Gpu.SelectedModel = Model(vm, "evo2_40b");
        vm.Gpu.IsConfirmed = true;

        vm.Gpu.ModelPinned = true;
        vm.Gpu.GpuPinned = false;

        vm.Gpu.IsConfirmed.ShouldBeTrue();
        vm.StartRunCommand.CanExecute(null).ShouldBeTrue();
    }

    [Fact]
    public async Task Toggling_a_lock_keeps_the_notice_of_the_last_automatic_move()
    {
        var vm = await WithOneFile(Everything);
        vm.Gpu.SelectedModel = Model(vm, "evo2_40b");
        vm.Gpu.HasNotice.ShouldBeTrue();

        vm.Gpu.GpuPinned = true;

        vm.Gpu.HasNotice.ShouldBeTrue();
    }

    [Fact]
    public async Task The_pins_start_open()
    {
        var vm = await WithOneFile();

        vm.Gpu.ModelPinned.ShouldBeFalse();
        vm.Gpu.GpuPinned.ShouldBeFalse();
    }
}
