using System.Text;
using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Cost;
using DnaEntropyGraph.Core.Inputs;
using DnaEntropyGraph.Core.Presets;
using DnaEntropyGraph.Presentation.Services;
using DnaEntropyGraph.Presentation.ViewModels;
using NSubstitute;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Presentation.Tests;

/// <summary>Issue #136: choosing an option preset on the New run page sets what the run is started with, and the page says so.</summary>
public sealed class PresetPickerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "deg-preset-" + Guid.NewGuid().ToString("N"));
    private readonly IJobEngine _jobEngine = Substitute.For<IJobEngine>();
    private readonly IShellLauncher _launcher = Substitute.For<IShellLauncher>();
    private readonly IClipboardTextReader _clipboard = Substitute.For<IClipboardTextReader>();
    private readonly MemorySettings _settings = new();
    private readonly NewRunViewModel _viewModel;

    public PresetPickerTests()
    {
        Directory.CreateDirectory(_dir);
        _jobEngine.StartRunAsync(Arg.Any<RunOptions>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult("job-1"));
        var estimates = Substitute.For<ICostEstimateService>();
        estimates.EstimateAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<long?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(CostEstimateResult.Unavailable(EstimateUnavailable.UnknownSize)));
        var strings = new Strings();
        var picker = new PresetPickerViewModel(new PresetStore(_settings), strings, _launcher, _clipboard);
        _viewModel = new NewRunViewModel(
            Substitute.For<IFilePicker>(),
            _jobEngine,
            Substitute.For<INavigator>(),
            strings,
            new LocalPastedInputStore(Path.Combine(_dir, "appdata")),
            estimates,
            new OutputSelectionStore(_settings),
            Substitute.For<IDiagnosticsLog>(),
            presets: picker);
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

    private async Task AddFasta()
    {
        var path = Path.Combine(_dir, "plasmid.fasta");
        File.WriteAllText(path, ">p\nACGTACGTACGTACGT\n", new UTF8Encoding(false));
        await _viewModel.AddDroppedCommand.ExecuteAsync(new DroppedItems([path], 0, false));
    }

    private PresetPickerViewModel Picker => _viewModel.Presets!;

    private PresetItem Item(string id) => Picker.Items.Single(i => i.Preset.Id == id);

    [Fact]
    public async Task Choosing_Plasmid_QC_starts_the_run_circular_both_combined_with_the_region_calls_and_the_summary_lists_them()
    {
        await AddFasta();

        Picker.SelectedItem = Item("plasmid_qc");
        // Read before Run: a started run empties the page (issue #527), and the summary describes the run about to start.
        var summary = Picker.SummaryText;
        await _viewModel.StartRunCommand.ExecuteAsync(null);

        await _jobEngine.Received(1).StartRunAsync(
            Arg.Is<RunOptions>(o => o.Topology == Topology.Circular && o.Direction == Direction.BothCombined
                && o.OutputFiles.HasFlag(OutputFileKinds.Regions) && o.PresetId == "plasmid_qc" && o.PresetName == "Plasmid QC"),
            Arg.Any<CancellationToken>());
        summary.ShouldContain("both strands combined");
        summary.ShouldContain("a circle");
        summary.ShouldContain("Regions");
    }

    [Fact]
    public async Task A_started_run_empties_the_files_but_keeps_the_preset_and_the_ticked_outputs_for_the_next_run()
    {
        // Issue #527 decision: the preset (and the run target, which is the Settings picker) is the user's intent and persists; only per-file state resets.
        await AddFasta();
        Picker.SelectedItem = Item("plasmid_qc");
        var ticked = _viewModel.OutputChoices.Where(c => c.IsChecked).Select(c => c.Entry.Kind).ToList();
        ticked.ShouldNotBeEmpty();

        await _viewModel.StartRunCommand.ExecuteAsync(null);

        _viewModel.Items.ShouldBeEmpty();
        Picker.SelectedItem.ShouldBe(Item("plasmid_qc"));
        await AddFasta();
        _viewModel.OutputChoices.Where(c => c.IsChecked).Select(c => c.Entry.Kind).ShouldBe(ticked);
    }

    [Fact]
    public async Task A_preset_ticks_its_files_in_the_boxes_already_shown()
    {
        await AddFasta();

        Picker.SelectedItem = Item("quick_look");

        _viewModel.OutputChoices.Where(c => c.IsChecked).Select(c => c.Entry.Kind).Aggregate(OutputFileKinds.None, (a, k) => a | k)
            .ShouldBe(BuiltInPresets.QuickLook.OutputFiles);
        Picker.Direction.ShouldBe(Direction.ForwardOnly);
    }

    [Fact]
    public async Task Choosing_a_preset_before_any_file_still_applies_when_the_file_arrives()
    {
        Picker.SelectedItem = Item("plasmid_qc");

        await AddFasta();

        _viewModel.OutputChoices.Single(c => c.Entry.Kind == OutputFileKinds.Regions).IsChecked.ShouldBeTrue();
        _viewModel.OutputChoices.Single(c => c.Entry.Kind == OutputFileKinds.Probs).IsChecked.ShouldBeFalse();
    }

    [Fact]
    public async Task Changing_a_file_box_by_hand_drops_the_preset_from_the_run()
    {
        await AddFasta();
        Picker.SelectedItem = Item("plasmid_qc");

        _viewModel.OutputChoices.Single(c => c.Entry.Kind == OutputFileKinds.Stats).IsChecked = false;
        await _viewModel.StartRunCommand.ExecuteAsync(null);

        Picker.SelectedItem.ShouldBeNull();
        await _jobEngine.Received(1).StartRunAsync(Arg.Is<RunOptions>(o => o.PresetId == null && o.PresetName == null), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task With_no_preset_the_run_uses_the_defaults_it_always_had()
    {
        await AddFasta();

        await _viewModel.StartRunCommand.ExecuteAsync(null);

        await _jobEngine.Received(1).StartRunAsync(
            Arg.Is<RunOptions>(o => o.Topology == Topology.Auto && o.Direction == Direction.BothCombined && o.ContextLength == 4096 && o.PresetId == null),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Save_current_keeps_the_options_as_a_user_preset_that_a_new_picker_lists()
    {
        await AddFasta();
        Picker.SelectedItem = Item("plasmid_qc");
        Picker.NewName = "My plasmid";

        Picker.SaveCurrentCommand.Execute(null);

        var saved = Picker.Items.Single(i => !i.Preset.BuiltIn);
        saved.Preset.Topology.ShouldBe(Topology.Circular);
        saved.Preset.OutputFiles.ShouldBe(BuiltInPresets.PlasmidQc.OutputFiles);
        Picker.SelectedItem.ShouldBe(saved);
        Picker.StatusText.ShouldContain("My plasmid");
        new PresetPickerViewModel(new PresetStore(_settings), new Strings(), _launcher, _clipboard).Items.Count(i => !i.Preset.BuiltIn).ShouldBe(1);
    }

    [Fact]
    public void Save_current_with_no_name_or_no_file_says_what_to_do_and_keeps_nothing()
    {
        Picker.NewName = "x";
        Picker.SaveCurrentCommand.Execute(null);
        Picker.StatusText.ShouldBe("PresetStatus_NeedFile");

        Picker.Items.ShouldAllBe(i => i.Preset.BuiltIn);
    }

    [Fact]
    public async Task Delete_is_for_the_users_own_presets_only()
    {
        await AddFasta();
        Picker.SelectedItem = Item("quick_look");
        Picker.DeleteCommand.CanExecute(null).ShouldBeFalse();
        Picker.NewName = "Mine";
        Picker.SaveCurrentCommand.Execute(null);

        Picker.DeleteCommand.CanExecute(null).ShouldBeTrue();
        Picker.DeleteCommand.Execute(null);

        Picker.Items.ShouldAllBe(i => i.Preset.BuiltIn);
        Picker.SelectedItem.ShouldBeNull();
    }

    [Fact]
    public async Task Copy_puts_the_users_presets_on_the_clipboard_and_Add_reads_them_back_into_a_fresh_install()
    {
        await AddFasta();
        Picker.NewName = "Mine";
        Picker.SaveCurrentCommand.Execute(null);
        string? copied = null;
        _launcher.CopyText(Arg.Do<string>(t => copied = t)).Returns(true);

        Picker.CopyMineCommand.Execute(null);

        copied.ShouldNotBeNull();
        var fresh = new PresetPickerViewModel(new PresetStore(new MemorySettings()), new Strings(), _launcher, _clipboard);
        _clipboard.ReadTextAsync().Returns(Task.FromResult<string?>(copied));
        await fresh.AddFromClipboardCommand.ExecuteAsync(null);
        fresh.Items.Single(i => !i.Preset.BuiltIn).DisplayName.ShouldBe("Mine");

        _clipboard.ReadTextAsync().Returns(Task.FromResult<string?>("not presets"));
        await fresh.AddFromClipboardCommand.ExecuteAsync(null);
        fresh.StatusText.ShouldBe("PresetStatus_ImportNone");
    }

    private sealed class MemorySettings : ISettingsStore
    {
        private readonly Dictionary<string, string> _values = [];

        public string? GetString(string key) => _values.GetValueOrDefault(key);

        public void SetString(string key, string value) => _values[key] = value;
    }

    private sealed class Strings : IStringResourceProvider
    {
        private static readonly Dictionary<string, string> Templates = new()
        {
            ["PresetSummary_Line"] = "{0} | {1} | {2} | {3}",
            ["PresetDirection_BothCombined"] = "both strands combined",
            ["PresetDirection_ForwardOnly"] = "the forward strand only",
            ["PresetTopology_Auto"] = "decided by the file",
            ["PresetTopology_Circular"] = "a circle",
            ["Preset_plasmid_qc_Name"] = "Plasmid QC",
            ["Preset_quick_look_Name"] = "Quick look",
            ["Preset_publication_Name"] = "Publication",
            ["OutputChoice_regions"] = "Regions",
            ["PresetStatus_Saved"] = "Saved {0}",
            ["PresetStatus_Imported"] = "Added {0}",
        };

        public string GetString(string key) => Templates.GetValueOrDefault(key, key);
    }
}
