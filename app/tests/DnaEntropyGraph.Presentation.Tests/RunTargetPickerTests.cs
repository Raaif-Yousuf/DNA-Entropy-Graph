using DnaEntropyGraph.Core.LocalEngine;
using DnaEntropyGraph.Presentation.Services;
using DnaEntropyGraph.Presentation.ViewModels;
using NSubstitute;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Presentation.Tests;

/// <summary>Issues #120 and #173: the Cloud / This PC / Auto picker.</summary>
public sealed class RunTargetPickerTests
{
    private readonly MemorySettings _settings = new();
    private readonly ILocalReadinessSource _readiness = Substitute.For<ILocalReadinessSource>();
    private readonly IStringResourceProvider _strings = Substitute.For<IStringResourceProvider>();

    public RunTargetPickerTests()
    {
        _strings.GetString(Arg.Any<string>()).Returns(call => call.Arg<string>());
    }

    private RunTargetPickerViewModel Create() => new(_settings, _readiness, _strings);

    private void Readiness(LocalCapability capability, LocalEngineHealth engine)
        => _readiness.CheckAsync(Arg.Any<CancellationToken>()).Returns(new LocalReadiness(capability, engine));

    private static LocalEngineHealth Healthy(bool cuda = true) => new(LocalEngineState.Healthy, "0.0.1", cuda, cuda, string.Empty);

    private static LocalCapability Capable() => new(true, [], new GpuInfo("RTX 4090", 24564, "551.61"), 100L << 30);

    private static LocalCapability Blocked(params LocalBlocker[] blockers) => new(false, blockers, null, 100L << 30);

    private sealed class RecordingContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state)
            => ThreadPool.QueueUserWorkItem(_ =>
            {
                SetSynchronizationContext(this);
                d(state);
            });
    }

    [Fact]
    public async Task Refreshing_sets_the_bound_properties_on_the_context_that_called_it_not_on_a_pool_thread()
    {
        // Settings binds ThisPcReason/Note OneWay; a PropertyChanged raised off the UI thread throws RPC_E_WRONG_THREAD in WinUI.
        _readiness.CheckAsync(Arg.Any<CancellationToken>()).Returns(async _ =>
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
            return new LocalReadiness(Blocked(LocalBlocker.NoNvidiaGpu), LocalEngineHealth.NotInstalled());
        });
        var vm = Create();
        var raisedOn = new List<SynchronizationContext?>();
        vm.PropertyChanged += (_, _) => raisedOn.Add(SynchronizationContext.Current);
        var ui = new RecordingContext();
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(ui);
        try
        {
            await vm.RefreshAsync(TestContext.Current.CancellationToken);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        raisedOn.ShouldNotBeEmpty();
        raisedOn.ShouldAllBe(c => ReferenceEquals(c, ui));
    }

    [Fact]
    public async Task When_two_looks_overlap_the_one_that_started_last_wins_even_if_the_older_one_finishes_last()
    {
        // Issue #735 review: New run and Settings share this picker; a slow stale probe must not overwrite a newer answer.
        var slow = new TaskCompletionSource<LocalReadiness>();
        var fast = new LocalReadiness(Capable(), Healthy());
        _readiness.CheckAsync(Arg.Any<CancellationToken>()).Returns(slow.Task, Task.FromResult(fast));
        var vm = Create();

        var first = vm.RefreshAsync(TestContext.Current.CancellationToken);
        await vm.RefreshAsync(TestContext.Current.CancellationToken);
        vm.Options.Single(o => o.Value == "This PC").IsAvailable.ShouldBeTrue();

        slow.SetResult(new LocalReadiness(Blocked(LocalBlocker.NoNvidiaGpu), LocalEngineHealth.NotInstalled()));
        await first;

        var thisPc = vm.Options.Single(o => o.Value == "This PC");
        thisPc.IsAvailable.ShouldBeTrue();
        thisPc.Reason.ShouldBeEmpty();
        vm.ThisPcReason.ShouldBeEmpty();
    }

    [Fact]
    public async Task When_two_looks_overlap_and_the_older_one_finishes_first_the_newer_answer_still_wins()
    {
        var slowNew = new TaskCompletionSource<LocalReadiness>();
        _readiness.CheckAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(new LocalReadiness(Capable(), Healthy())), slowNew.Task);
        var vm = Create();

        var first = vm.RefreshAsync(TestContext.Current.CancellationToken);
        var second = vm.RefreshAsync(TestContext.Current.CancellationToken);
        await first;
        slowNew.SetResult(new LocalReadiness(Blocked(LocalBlocker.NoNvidiaGpu), LocalEngineHealth.NotInstalled()));
        await second;

        vm.Options.Single(o => o.Value == "This PC").IsAvailable.ShouldBeFalse();
        vm.ThisPcReason.ShouldBe("RunTargetReason_NoNvidiaGpu");
    }

    [Fact]
    public void With_nothing_saved_the_choice_is_Cloud_and_the_three_places_are_offered_in_order()
    {
        var vm = Create();

        vm.SelectedTarget.ShouldBe("Cloud");
        vm.Options.Select(o => o.Value).ShouldBe(["Cloud", "This PC", "Auto"]);
    }

    [Fact]
    public void Choosing_a_place_saves_it_and_Read_gives_it_back_for_a_new_run()
    {
        var vm = Create();

        vm.SelectedTarget = RunTargets.Auto;

        _settings.Values[RunTargetPickerViewModel.SettingKey].ShouldBe("Auto");
        RunTargetPickerViewModel.Read(_settings).ShouldBe("Auto");
        Create().SelectedTarget.ShouldBe("Auto");
    }

    [Fact]
    public void Opening_the_picker_does_not_save_what_it_just_read()
    {
        _settings.Values[RunTargetPickerViewModel.SettingKey] = "This PC";

        Create();

        _settings.Values[RunTargetPickerViewModel.SettingKey].ShouldBe("This PC");
        _settings.Writes.ShouldBe(0);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Moon")]
    [InlineData("cloud ")]
    public void A_saved_value_the_picker_does_not_offer_reads_as_Cloud(string saved)
    {
        _settings.Values[RunTargetPickerViewModel.SettingKey] = saved;

        RunTargetPickerViewModel.Read(_settings).ShouldBe("Cloud");
    }

    [Fact]
    public async Task A_PC_with_no_nvidia_card_greys_out_This_PC_with_the_reason_and_leaves_the_others_on()
    {
        Readiness(Blocked(LocalBlocker.NoNvidiaGpu), LocalEngineHealth.NotInstalled());
        var vm = Create();

        await vm.RefreshAsync(TestContext.Current.CancellationToken);

        var thisPc = vm.Options.Single(o => o.Value == "This PC");
        thisPc.IsAvailable.ShouldBeFalse();
        thisPc.Reason.ShouldBe("RunTargetReason_NoNvidiaGpu");
        vm.ThisPcReason.ShouldBe("RunTargetReason_NoNvidiaGpu");
        vm.Options.Single(o => o.Value == "Cloud").IsAvailable.ShouldBeTrue();
        vm.Options.Single(o => o.Value == "Auto").IsAvailable.ShouldBeTrue();
    }

    [Fact]
    public async Task An_8_GB_card_is_greyed_out_for_its_memory()
    {
        Readiness(new LocalCapability(false, [LocalBlocker.NotEnoughGpuMemory], new GpuInfo("RTX 3070", 8192, "551.61"), 100L << 30), Healthy());
        var vm = Create();

        await vm.RefreshAsync(TestContext.Current.CancellationToken);

        vm.Options.Single(o => o.Value == "This PC").Reason.ShouldBe("RunTargetReason_NotEnoughGpuMemory");
    }

    [Fact]
    public async Task A_graphics_driver_that_is_not_working_is_greyed_out_for_the_driver_not_for_a_missing_card()
    {
        Readiness(Blocked(LocalBlocker.GpuToolFailed), Healthy());
        var vm = Create();

        await vm.RefreshAsync(TestContext.Current.CancellationToken);

        vm.Options.Single(o => o.Value == "This PC").Reason.ShouldBe("RunTargetReason_GpuToolFailed");
    }

    [Fact]
    public async Task A_capable_PC_with_no_engine_says_to_install_it()
    {
        Readiness(Capable(), LocalEngineHealth.NotInstalled());
        var vm = Create();

        await vm.RefreshAsync(TestContext.Current.CancellationToken);

        var thisPc = vm.Options.Single(o => o.Value == "This PC");
        thisPc.IsAvailable.ShouldBeFalse();
        thisPc.Reason.ShouldBe("RunTargetReason_NotInstalled");
    }

    [Fact]
    public async Task A_capable_PC_whose_engine_sees_no_cuda_is_greyed_out_for_that()
    {
        Readiness(Capable(), Healthy(cuda: false));
        var vm = Create();

        await vm.RefreshAsync(TestContext.Current.CancellationToken);

        vm.Options.Single(o => o.Value == "This PC").Reason.ShouldBe("RunTargetReason_NoCuda");
    }

    [Fact]
    public async Task A_capable_PC_with_a_healthy_engine_enables_This_PC()
    {
        Readiness(Capable(), Healthy());
        var vm = Create();

        await vm.RefreshAsync(TestContext.Current.CancellationToken);

        var thisPc = vm.Options.Single(o => o.Value == "This PC");
        thisPc.IsAvailable.ShouldBeTrue();
        thisPc.Reason.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_readiness_check_that_throws_greys_out_This_PC_instead_of_throwing()
    {
        _readiness.CheckAsync(Arg.Any<CancellationToken>()).Returns<LocalReadiness>(_ => throw new InvalidOperationException("boom"));
        var vm = Create();

        await vm.RefreshAsync(TestContext.Current.CancellationToken);

        vm.Options.Single(o => o.Value == "This PC").IsAvailable.ShouldBeFalse();
    }

    [Fact]
    public async Task A_saved_This_PC_that_cannot_run_now_keeps_the_choice_and_explains_it_rather_than_switching_to_the_cloud()
    {
        _settings.Values[RunTargetPickerViewModel.SettingKey] = "This PC";
        Readiness(Blocked(LocalBlocker.NoNvidiaGpu), LocalEngineHealth.NotInstalled());
        var vm = Create();

        await vm.RefreshAsync(TestContext.Current.CancellationToken);

        vm.SelectedTarget.ShouldBe("This PC");
        vm.Note.ShouldBe("RunTargetReason_NoNvidiaGpu");
    }

    [Fact]
    public async Task Choosing_a_greyed_out_This_PC_is_refused_keeps_the_old_choice_saves_nothing_and_says_why()
    {
        // MEASURED 2026-10-15 (review of #173): IsEnabled on the item's content greys the text but the row is still clickable, so This PC could be picked.
        Readiness(Blocked(LocalBlocker.NoNvidiaGpu), LocalEngineHealth.NotInstalled());
        var vm = Create();
        await vm.RefreshAsync(TestContext.Current.CancellationToken);
        vm.SelectedTarget = "Auto";
        var writes = _settings.Writes;

        vm.SelectedTarget = "This PC";

        vm.SelectedTarget.ShouldBe("Auto");
        _settings.Values[RunTargetPickerViewModel.SettingKey].ShouldBe("Auto");
        _settings.Writes.ShouldBe(writes);
        vm.Note.ShouldBe("RunTargetReason_NoNvidiaGpu");
    }

    [Fact]
    public async Task Choosing_This_PC_when_it_can_run_is_accepted_and_saved()
    {
        Readiness(Capable(), Healthy());
        var vm = Create();
        await vm.RefreshAsync(TestContext.Current.CancellationToken);

        vm.SelectedTarget = "This PC";

        vm.SelectedTarget.ShouldBe("This PC");
        _settings.Values[RunTargetPickerViewModel.SettingKey].ShouldBe("This PC");
    }

    [Fact]
    public async Task Auto_says_where_it_will_go()
    {
        Readiness(Blocked(LocalBlocker.NoNvidiaGpu), LocalEngineHealth.NotInstalled());
        var vm = Create();
        await vm.RefreshAsync(TestContext.Current.CancellationToken);

        vm.SelectedTarget = "Auto";
        vm.Note.ShouldBe("RunTargetAutoCloud");

        Readiness(Capable(), Healthy());
        await vm.RefreshAsync(TestContext.Current.CancellationToken);
        vm.Note.ShouldBe("RunTargetAutoLocal");
    }

    [Fact]
    public void Reset_all_settings_puts_the_picker_back_to_Cloud_without_saving_anything()
    {
        CommunityToolkit.Mvvm.Messaging.IMessenger messenger = new CommunityToolkit.Mvvm.Messaging.WeakReferenceMessenger();
        _settings.Values[RunTargetPickerViewModel.SettingKey] = "Auto";
        var vm = new RunTargetPickerViewModel(_settings, _readiness, _strings, messenger);
        vm.SelectedTarget.ShouldBe("Auto");
        _settings.Values.Clear();
        var writes = _settings.Writes;

        CommunityToolkit.Mvvm.Messaging.IMessengerExtensions.Send(messenger, new DnaEntropyGraph.Presentation.Messaging.SettingsResetMessage());

        vm.SelectedTarget.ShouldBe("Cloud");
        _settings.Writes.ShouldBe(writes);
        _settings.Values.ShouldNotContainKey(RunTargetPickerViewModel.SettingKey);
    }

    [Fact]
    public void A_settings_file_that_cannot_be_written_shows_a_message_and_does_not_throw()
    {
        var vm = Create();
        _settings.Unavailable = true;

        vm.SelectedTarget = "Auto";

        vm.SaveStatus.ShouldBe("RunTargetNotSaved");
    }

    private sealed class MemorySettings : DnaEntropyGraph.Core.Abstractions.ISettingsStore
    {
        public Dictionary<string, string> Values { get; } = [];

        public bool Unavailable { get; set; }

        public int Writes { get; private set; }

        public string? GetString(string key)
            => Unavailable ? throw new DnaEntropyGraph.Core.Abstractions.SettingsUnavailableException("locked") : Values.GetValueOrDefault(key);

        public void SetString(string key, string value)
        {
            if (Unavailable)
            {
                throw new DnaEntropyGraph.Core.Abstractions.SettingsUnavailableException("locked");
            }

            Writes++;
            Values[key] = value;
        }
    }
}
