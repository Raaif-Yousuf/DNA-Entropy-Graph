using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.LocalEngine;
using DnaEntropyGraph.Presentation.Services;
using DnaEntropyGraph.Presentation.ViewModels;
using NSubstitute;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Presentation.Tests;

/// <summary>Issues #120, #172, #173: the Settings "This PC" group.</summary>
public sealed class ThisPcSettingsTests
{
    private readonly ILocalReadinessSource _readiness = Substitute.For<ILocalReadinessSource>();
    private readonly ILocalEngineInstaller _installer = Substitute.For<ILocalEngineInstaller>();
    private readonly IDialogService _dialogs = Substitute.For<IDialogService>();
    private readonly IStringResourceProvider _strings = Substitute.For<IStringResourceProvider>();

    public ThisPcSettingsTests()
    {
        _strings.GetString(Arg.Any<string>()).Returns(call => call.Arg<string>() switch
        {
            "ThisPcGpuFormat" => "{0}|{1} GB|{2}",
            "ThisPcDiskFormat" => "{0} GB free",
            var other => other,
        });
        _dialogs.ConfirmAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);
        Ready(new LocalCapability(false, [LocalBlocker.NoNvidiaGpu], null, 50L << 30), LocalEngineHealth.NotInstalled());
    }

    private ThisPcSettingsViewModel Create()
        => new(_readiness, _installer, _dialogs, _strings, new RunTargetPickerViewModel(new Memory(), _readiness, _strings));

    private void Ready(LocalCapability capability, LocalEngineHealth engine)
        => _readiness.CheckAsync(Arg.Any<CancellationToken>()).Returns(new LocalReadiness(capability, engine));

    private static readonly LocalCapability Capable = new(true, [], new GpuInfo("NVIDIA GeForce RTX 4090", 24564, "551.61"), 100L << 30);

    private static readonly LocalEngineHealth Healthy = new(LocalEngineState.Healthy, "0.0.1", true, true, string.Empty);

    [Fact]
    public async Task The_health_card_for_a_laptop_with_no_nvidia_card_says_so_and_why_This_PC_is_off()
    {
        var vm = Create();

        await vm.RefreshCommand.ExecuteAsync(null);

        vm.IsChecked.ShouldBeTrue();
        vm.GpuText.ShouldBe("ThisPcNoGpu");
        vm.BlockersText.ShouldBe("ThisPcBlocker_NoNvidiaGpu");
        vm.EngineText.ShouldBe("ThisPcEngine_NotInstalled");
        vm.DiskText.ShouldBe("50 GB free");
    }

    [Fact]
    public async Task The_health_card_for_a_capable_PC_names_the_card_its_memory_and_driver()
    {
        Ready(Capable, Healthy);
        var vm = Create();

        await vm.RefreshCommand.ExecuteAsync(null);

        vm.GpuText.ShouldBe("NVIDIA GeForce RTX 4090|24 GB|551.61");
        vm.BlockersText.ShouldBeEmpty();
        vm.EngineText.ShouldBe("ThisPcEngine_Healthy");
        vm.Picker.Options.Single(o => o.Value == "This PC").IsAvailable.ShouldBeTrue("the picker is refreshed by the same check");
    }

    [Fact]
    public async Task A_healthy_engine_that_sees_no_cuda_says_so_on_the_card()
    {
        Ready(Capable, new LocalEngineHealth(LocalEngineState.Healthy, "0.0.1", true, false, string.Empty));
        var vm = Create();

        await vm.RefreshCommand.ExecuteAsync(null);

        vm.EngineText.ShouldBe("ThisPcEngine_Healthy ThisPcEngineNoCuda");
    }

    [Fact]
    public async Task A_check_that_throws_shows_a_message_and_never_throws()
    {
        _readiness.CheckAsync(Arg.Any<CancellationToken>()).Returns<LocalReadiness>(_ => throw new InvalidOperationException("boom"));
        var vm = Create();

        await vm.RefreshCommand.ExecuteAsync(null);

        vm.EngineText.ShouldBe("ThisPcCheckFailed");
        vm.IsBusy.ShouldBeFalse();
    }

    [Fact]
    public async Task Install_is_offered_only_when_nothing_is_installed_and_Repair_and_Remove_only_when_something_is()
    {
        var vm = Create();
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.InstallCommand.CanExecute(null).ShouldBeTrue();
        vm.RepairCommand.CanExecute(null).ShouldBeFalse();
        vm.RemoveCommand.CanExecute(null).ShouldBeFalse();

        Ready(Capable, Healthy);
        await vm.RefreshCommand.ExecuteAsync(null);

        vm.InstallCommand.CanExecute(null).ShouldBeFalse();
        vm.RepairCommand.CanExecute(null).ShouldBeTrue();
        vm.RemoveCommand.CanExecute(null).ShouldBeTrue();
    }

    [Fact]
    public async Task Install_runs_the_installer_shows_its_steps_then_rechecks_so_the_card_turns_green()
    {
        var vm = Create();
        await vm.RefreshCommand.ExecuteAsync(null);
        var seen = new List<string>();
        _installer.InstallAsync(Arg.Any<IProgress<LocalInstallProgress>>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            call.Arg<IProgress<LocalInstallProgress>>().Report(new LocalInstallProgress(LocalInstallStep.InstallingPackages, "torch"));
            await Task.Delay(150); // a Progress report posts to the thread pool in a test; let it land while the install is still running
            Ready(Capable, Healthy);
            return new LocalInstallResult(true);
        });
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(vm.ProgressText) && vm.ProgressText.Length > 0)
            {
                seen.Add(vm.ProgressText);
            }
        };

        await vm.InstallCommand.ExecuteAsync(null);

        vm.StatusText.ShouldBe("ThisPcInstalled");
        vm.EngineState.ShouldBe(LocalEngineState.Healthy);
        vm.EngineText.ShouldBe("ThisPcEngine_Healthy");
        vm.IsBusy.ShouldBeFalse();
        vm.ProgressText.ShouldBeEmpty();
        seen.ShouldContain("ThisPcStep_InstallingPackages torch");
    }

    [Fact]
    public async Task A_failed_install_shows_the_codes_message_which_names_an_action()
    {
        var vm = Create();
        await vm.RefreshCommand.ExecuteAsync(null);
        _installer.InstallAsync(Arg.Any<IProgress<LocalInstallProgress>>(), Arg.Any<CancellationToken>())
            .Returns(new LocalInstallResult(false, LocalEngineErrorCodes.UvMissing));

        await vm.InstallCommand.ExecuteAsync(null);

        vm.StatusText.ShouldBe("LocalEngineError_local_uv_missing");
    }

    [Fact]
    public async Task Buttons_are_off_while_an_install_runs_and_Cancel_is_on_and_cancels_its_token()
    {
        var vm = Create();
        await vm.RefreshCommand.ExecuteAsync(null);
        var started = new TaskCompletionSource();
        CancellationToken seenToken = default;
        _installer.InstallAsync(Arg.Any<IProgress<LocalInstallProgress>>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            seenToken = call.Arg<CancellationToken>();
            started.SetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, seenToken);
            }
            catch (OperationCanceledException)
            {
            }

            return new LocalInstallResult(false, LocalEngineErrorCodes.Cancelled);
        });

        var install = vm.InstallCommand.ExecuteAsync(null);
        await started.Task;

        vm.IsBusy.ShouldBeTrue();
        vm.InstallCommand.CanExecute(null).ShouldBeFalse();
        vm.RefreshCommand.CanExecute(null).ShouldBeFalse();
        vm.CancelInstallCommand.CanExecute(null).ShouldBeTrue();

        vm.CancelInstallCommand.Execute(null);
        await install;

        seenToken.IsCancellationRequested.ShouldBeTrue();
        vm.StatusText.ShouldBe("LocalEngineError_local_cancelled");
        vm.IsBusy.ShouldBeFalse();
    }

    [Fact]
    public async Task Repair_calls_the_installers_repair()
    {
        Ready(Capable, Healthy);
        var vm = Create();
        await vm.RefreshCommand.ExecuteAsync(null);
        _installer.RepairAsync(Arg.Any<IProgress<LocalInstallProgress>>(), Arg.Any<CancellationToken>()).Returns(new LocalInstallResult(true));

        await vm.RepairCommand.ExecuteAsync(null);

        await _installer.Received(1).RepairAsync(Arg.Any<IProgress<LocalInstallProgress>>(), Arg.Any<CancellationToken>());
        vm.StatusText.ShouldBe("ThisPcInstalled");
    }

    [Fact]
    public async Task Remove_asks_first_and_does_nothing_when_the_user_says_no()
    {
        Ready(Capable, Healthy);
        _dialogs.ConfirmAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(false);
        var vm = Create();
        await vm.RefreshCommand.ExecuteAsync(null);

        await vm.RemoveCommand.ExecuteAsync(null);

        await _installer.DidNotReceive().RemoveAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Remove_after_a_yes_removes_and_rechecks()
    {
        Ready(Capable, Healthy);
        var vm = Create();
        await vm.RefreshCommand.ExecuteAsync(null);
        _installer.RemoveAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            Ready(Capable, LocalEngineHealth.NotInstalled());
            return Task.FromResult(new LocalInstallResult(true));
        });

        await vm.RemoveCommand.ExecuteAsync(null);

        vm.StatusText.ShouldBe("ThisPcRemoved");
        vm.EngineState.ShouldBe(LocalEngineState.NotInstalled);
    }

    private sealed class Memory : ISettingsStore
    {
        private readonly Dictionary<string, string> _values = [];

        public string? GetString(string key) => _values.GetValueOrDefault(key);

        public void SetString(string key, string value) => _values[key] = value;
    }
}
