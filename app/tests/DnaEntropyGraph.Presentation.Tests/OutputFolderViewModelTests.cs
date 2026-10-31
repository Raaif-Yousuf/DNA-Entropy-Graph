using System.Text;
using CommunityToolkit.Mvvm.Messaging;
using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Cost;
using DnaEntropyGraph.Core.Inputs;
using DnaEntropyGraph.Presentation.Messaging;
using DnaEntropyGraph.Presentation.Services;
using DnaEntropyGraph.Presentation.ViewModels;
using NSubstitute;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Presentation.Tests;

/// <summary>Issue #1037: the "Save results to" folder is picked, checked, remembered, read back and sent with the run.</summary>
public sealed class OutputFolderViewModelTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "deg-outfolder-" + Guid.NewGuid().ToString("N"));
    private readonly MemorySettings _settings = new();
    private readonly IFilePicker _picker = Substitute.For<IFilePicker>();
    private readonly IStringResourceProvider _strings = Substitute.For<IStringResourceProvider>();

    public OutputFolderViewModelTests()
    {
        Directory.CreateDirectory(_dir);
        _strings.GetString(Arg.Any<string>()).Returns(call => call.Arg<string>());
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

    private OutputFolderViewModel NewVm(IMessenger? messenger = null) => new(_settings, _picker, _strings, messenger);

    [Fact]
    public void With_nothing_saved_the_folder_is_the_default_and_says_so()
    {
        var vm = NewVm();

        vm.FolderPath.ShouldBeNull();
        vm.DisplayText.ShouldBe("OutputFolder_Default_Text");
        vm.HasMessage.ShouldBeFalse();
    }

    [Fact]
    public async Task A_picked_folder_is_saved_and_a_new_view_model_reads_it_back()
    {
        _picker.PickOutputFolderAsync(Arg.Any<CancellationToken>()).Returns(@"D:\Lab\Results");
        var vm = NewVm();

        await vm.ChangeCommand.ExecuteAsync(null);

        vm.FolderPath.ShouldBe(@"D:\Lab\Results");
        vm.DisplayText.ShouldBe(@"D:\Lab\Results");
        _settings.Values[OutputFolderViewModel.SettingKey].ShouldBe(@"D:\Lab\Results");
        NewVm().FolderPath.ShouldBe(@"D:\Lab\Results");
    }

    [Theory]
    [InlineData(@"D:\")]
    [InlineData(@"c:\")]
    public async Task A_drive_root_is_refused_beside_the_control_and_not_saved(string picked)
    {
        _settings.Values[OutputFolderViewModel.SettingKey] = @"D:\Lab";
        _picker.PickOutputFolderAsync(Arg.Any<CancellationToken>()).Returns(picked);
        var vm = NewVm();

        await vm.ChangeCommand.ExecuteAsync(null);

        vm.HasMessage.ShouldBeTrue();
        vm.Message.ShouldBe(OutputFolderChoice.DriveRootMessage);
        vm.FolderPath.ShouldBe(@"D:\Lab", "the earlier choice stays");
        _settings.Values[OutputFolderViewModel.SettingKey].ShouldBe(@"D:\Lab");
    }

    [Fact]
    public async Task A_cancelled_picker_changes_nothing_and_clears_an_old_message()
    {
        _settings.Values[OutputFolderViewModel.SettingKey] = @"D:\Lab";
        _picker.PickOutputFolderAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<string?>(@"D:\"), Task.FromResult<string?>(null));
        var vm = NewVm();
        await vm.ChangeCommand.ExecuteAsync(null);
        vm.HasMessage.ShouldBeTrue();

        await vm.ChangeCommand.ExecuteAsync(null);

        vm.FolderPath.ShouldBe(@"D:\Lab");
        vm.HasMessage.ShouldBeFalse();
        _settings.Values[OutputFolderViewModel.SettingKey].ShouldBe(@"D:\Lab");
    }

    [Fact]
    public async Task Use_Downloads_clears_the_saved_choice()
    {
        _settings.Values[OutputFolderViewModel.SettingKey] = @"D:\Lab";
        var vm = NewVm();

        vm.UseDownloadsCommand.Execute(null);

        vm.FolderPath.ShouldBeNull();
        NewVm().FolderPath.ShouldBeNull("a new view model must not read the old folder back");
        await Task.CompletedTask;
    }

    [Fact]
    public async Task A_locked_settings_file_keeps_the_old_folder_and_says_what_to_do()
    {
        _settings.Values[OutputFolderViewModel.SettingKey] = @"D:\Lab";
        var vm = NewVm();
        _settings.Unavailable = true;
        _picker.PickOutputFolderAsync(Arg.Any<CancellationToken>()).Returns(@"E:\Other");

        await vm.ChangeCommand.ExecuteAsync(null);

        vm.FolderPath.ShouldBe(@"D:\Lab");
        vm.Message.ShouldBe("OutputFolder_SaveFailed_Message");
    }

    [Fact]
    public void An_unreadable_settings_file_means_the_default_not_a_crash()
    {
        _settings.Unavailable = true;

        NewVm().FolderPath.ShouldBeNull();
    }

    [Fact]
    public void Reset_all_settings_puts_the_default_back()
    {
        _settings.Values[OutputFolderViewModel.SettingKey] = @"D:\Lab";
        var messenger = new WeakReferenceMessenger();
        var vm = NewVm(messenger);
        vm.FolderPath.ShouldBe(@"D:\Lab");
        _settings.Values.Clear();

        messenger.Send(new SettingsResetMessage());

        vm.FolderPath.ShouldBeNull();
    }

    [Fact]
    public async Task The_next_run_carries_the_remembered_folder_from_a_freshly_built_view_model()
    {
        _settings.Values[OutputFolderViewModel.SettingKey] = @"D:\Lab\Results";
        var engine = Substitute.For<IJobEngine>();
        engine.StartRunAsync(Arg.Any<RunOptions>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult("job-1"));
        var estimates = Substitute.For<ICostEstimateService>();
        estimates.EstimateAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<long?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(CostEstimateResult.Unavailable(EstimateUnavailable.UnknownSize)));
        var vm = new NewRunViewModel(
            _picker,
            engine,
            Substitute.For<INavigator>(),
            _strings,
            new LocalPastedInputStore(Path.Combine(_dir, "appdata")),
            estimates,
            new OutputSelectionStore(_settings),
            NullDiagnosticsLog.Instance,
            TestGpu.Plan(),
            outputFolder: NewVm());
        var path = Path.Combine(_dir, "a.fasta");
        await File.WriteAllTextAsync(path, ">a\nACGTACGTACGTACGT\n", new UTF8Encoding(false), TestContext.Current.CancellationToken);
        await vm.AddDroppedCommand.ExecuteAsync(new DroppedItems([path], 0, false));

        await vm.StartRunCommand.ExecuteAsync(null);

        var options = (RunOptions)engine.ReceivedCalls().Single(c => c.GetMethodInfo().Name == nameof(IJobEngine.StartRunAsync)).GetArguments()[0]!;
        options.OutputFolder.ShouldBe(@"D:\Lab\Results");
    }

    [Fact]
    public void The_setting_key_is_listed_as_never_exported_in_the_settings_catalog()
    {
        DnaEntropyGraph.Core.SettingsPortability.SettingsCatalog.Excluded.ShouldContainKey(OutputFolderViewModel.SettingKey);
    }

    private sealed class MemorySettings : ISettingsStore
    {
        public Dictionary<string, string> Values { get; } = [];

        public bool Unavailable { get; set; }

        public string? GetString(string key)
            => Unavailable ? throw new SettingsUnavailableException("locked") : Values.GetValueOrDefault(key);

        public void SetString(string key, string value)
        {
            if (Unavailable)
            {
                throw new SettingsUnavailableException("locked");
            }

            Values[key] = value;
        }
    }
}
