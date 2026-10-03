using System.Text;
using System.Text.Json;
using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Contract;
using DnaEntropyGraph.Core.Cost;
using DnaEntropyGraph.Core.Inputs;
using DnaEntropyGraph.Presentation.Services;
using DnaEntropyGraph.Presentation.ViewModels;
using NSubstitute;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Presentation.Tests;

/// <summary>Issue #103: the New run page offers the output files per input kind, remembers the choice, and sends exactly it.</summary>
public sealed class NewRunViewModelOutputTests : IDisposable
{
    private const string Dna = "ACGTACGTACGTACGT";

    private const string GenBank = "LOCUS       T1                     24 bp    DNA     linear   UNK 01-JAN-1980\n"
        + "FEATURES             Location/Qualifiers\n     gene            1..12\nORIGIN\n"
        + "        1 acgtacgtac gtacgtacgt acgt\n//\n";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "deg-newrun-out-" + Guid.NewGuid().ToString("N"));
    private readonly MemorySettings _settings = new();
    private readonly IJobEngine _jobEngine = Substitute.For<IJobEngine>();
    private readonly ICostEstimateService _estimates = Substitute.For<ICostEstimateService>();
    private readonly IStringResourceProvider _strings = Substitute.For<IStringResourceProvider>();

    public NewRunViewModelOutputTests()
    {
        Directory.CreateDirectory(_dir);
        _jobEngine.StartRunAsync(Arg.Any<RunOptions>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult("job-1"));
        _estimates.EstimateAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<long?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(CostEstimateResult.Unavailable(EstimateUnavailable.UnknownSize)));
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

    private sealed class MemorySettings : ISettingsStore
    {
        private readonly Dictionary<string, string> _values = [];

        public string? GetString(string key) => _values.GetValueOrDefault(key);

        public void SetString(string key, string value) => _values[key] = value;
    }

    private NewRunViewModel NewViewModel() => new(
        Substitute.For<IFilePicker>(),
        _jobEngine,
        Substitute.For<INavigator>(),
        _strings,
        new LocalPastedInputStore(Path.Combine(_dir, "appdata")),
        _estimates,
        new OutputSelectionStore(_settings));

    private async Task AddFile(NewRunViewModel vm, string name, string content)
    {
        var path = Path.Combine(_dir, name);
        await File.WriteAllTextAsync(path, content, new UTF8Encoding(false));
        await vm.AddDroppedCommand.ExecuteAsync(new DroppedItems([path], 0, false));
    }

    private static OutputChoiceItem Choice(NewRunViewModel vm, string wire) => vm.OutputChoices.Single(c => c.WireName == wire);

    private static List<string> Outputs(RunOptions options) => JsonDocument.Parse(WorkerManifestBuilder.Build(
            options,
            "20261003-120000-abcdef",
            "install-1",
            "0.1.0",
            "reg/x@sha256:a",
            "deg-123-abc",
            [new StagedInput(@"C:\runs\j1\input\a.fasta", "a.fasta")],
            DateTimeOffset.UnixEpoch)).RootElement
        .GetProperty("outputs").EnumerateArray().Select(e => e.GetString()!).ToList();

    private RunOptions LastRunOptions() => (RunOptions)_jobEngine.ReceivedCalls().Single(c => c.GetMethodInfo().Name == nameof(IJobEngine.StartRunAsync)).GetArguments()[0]!;

    [Fact]
    public void With_no_file_selected_there_are_no_output_choices()
    {
        var vm = NewViewModel();

        vm.HasOutputChoices.ShouldBeFalse();
        vm.OutputChoices.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("a.fasta", ">a\n" + Dna + "\n")]
    [InlineData("t.gb", GenBank)]
    public async Task A_file_gets_every_catalog_file_as_a_box_with_the_catalog_defaults_checked(string name, string content)
    {
        var vm = NewViewModel();

        await AddFile(vm, name, content);

        vm.HasOutputChoices.ShouldBeTrue();
        vm.OutputChoices.Select(c => c.WireName).ShouldBe(OutputCatalog.Entries.Select(e => e.WireName));
        vm.OutputChoices.Where(c => c.IsChecked).Select(c => c.WireName).ShouldBe(OutputCatalog.ToWireNames(OutputCatalog.Defaults));
        Choice(vm, "probs").IsChecked.ShouldBeFalse();
        Choice(vm, "wig").Label.ShouldBe("OutputChoice_wig", "the label comes from Resources.resw by the catalog's key");
    }

    [Fact]
    public async Task Ticking_wig_sends_a_manifest_with_wig_and_without_bedgraph_and_a_new_page_starts_that_way()
    {
        var vm = NewViewModel();
        await AddFile(vm, "a.fasta", ">a\n" + Dna + "\n");
        Choice(vm, "wig").IsChecked.ShouldBeFalse("WIG is off by default: the worker writes one track format per run");
        Choice(vm, "wig").IsChecked = true;
        Choice(vm, "bedgraph").IsChecked.ShouldBeFalse("the two track formats are alternatives");

        await vm.StartRunCommand.ExecuteAsync(null);

        var options = LastRunOptions();
        options.TrackFormat.ShouldBe(TrackFormat.Wig);
        Outputs(options).ShouldContain("wig");
        Outputs(options).ShouldNotContain("bedgraph");

        // The read side: a page built later over the same settings starts from what was remembered.
        var next = NewViewModel();
        await AddFile(next, "b.fasta", ">b\n" + Dna + "\n");
        Choice(next, "wig").IsChecked.ShouldBeTrue();
        Choice(next, "bedgraph").IsChecked.ShouldBeFalse();
    }

    [Fact]
    public async Task Ticking_bedgraph_again_clears_wig()
    {
        var vm = NewViewModel();
        await AddFile(vm, "a.fasta", ">a\n" + Dna + "\n");
        Choice(vm, "wig").IsChecked = true;

        Choice(vm, "bedgraph").IsChecked = true;

        Choice(vm, "wig").IsChecked.ShouldBeFalse();
        await vm.StartRunCommand.ExecuteAsync(null);
        LastRunOptions().TrackFormat.ShouldBe(TrackFormat.BedGraph);
        Outputs(LastRunOptions()).ShouldContain("bedgraph");
        Outputs(LastRunOptions()).ShouldNotContain("wig");
    }

    [Fact]
    public async Task A_remembered_choice_that_could_not_be_saved_still_opens_the_progress_page_once()
    {
        var settings = Substitute.For<ISettingsStore>();
        settings.When(s => s.SetString(Arg.Any<string>(), Arg.Any<string>())).Do(_ => throw new InvalidOperationException("settings broke"));
        var navigator = Substitute.For<INavigator>();
        var vm = new NewRunViewModel(
            Substitute.For<IFilePicker>(), _jobEngine, navigator, _strings,
            new LocalPastedInputStore(Path.Combine(_dir, "appdata")), _estimates, new OutputSelectionStore(settings));
        await AddFile(vm, "a.fasta", ">a\n" + Dna + "\n");

        await Should.NotThrowAsync(() => vm.StartRunCommand.ExecuteAsync(null));

        navigator.Received(1).NavigateTo("RunProgress", "job-1");
        await _jobEngine.Received(1).StartRunAsync(Arg.Any<RunOptions>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_navigation_that_throws_still_remembers_the_choice_for_the_run_that_started()
    {
        var navigator = Substitute.For<INavigator>();
        navigator.When(n => n.NavigateTo(Arg.Any<string>(), Arg.Any<object?>())).Do(_ => throw new InvalidOperationException("frame gone"));
        var vm = new NewRunViewModel(
            Substitute.For<IFilePicker>(), _jobEngine, navigator, _strings,
            new LocalPastedInputStore(Path.Combine(_dir, "appdata")), _estimates, new OutputSelectionStore(_settings));
        await AddFile(vm, "a.fasta", ">a\n" + Dna + "\n");
        Choice(vm, "wig").IsChecked = true;

        await Should.ThrowAsync<InvalidOperationException>(() => vm.StartRunCommand.ExecuteAsync(null));

        var next = NewViewModel();
        await AddFile(next, "b.fasta", ">b\n" + Dna + "\n");
        Choice(next, "wig").IsChecked.ShouldBeTrue("the run was paid for, so the choice is remembered even though the page did not open");
    }

    [Fact]
    public async Task A_navigation_and_a_store_that_both_throw_surface_only_the_navigation_failure()
    {
        var settings = Substitute.For<ISettingsStore>();
        settings.When(s => s.SetString(Arg.Any<string>(), Arg.Any<string>())).Do(_ => throw new IOException("settings broke"));
        var navigator = Substitute.For<INavigator>();
        navigator.When(n => n.NavigateTo(Arg.Any<string>(), Arg.Any<object?>())).Do(_ => throw new InvalidOperationException("frame gone"));
        var vm = new NewRunViewModel(
            Substitute.For<IFilePicker>(), _jobEngine, navigator, _strings,
            new LocalPastedInputStore(Path.Combine(_dir, "appdata")), _estimates, new OutputSelectionStore(settings));
        await AddFile(vm, "a.fasta", ">a\n" + Dna + "\n");

        await Should.ThrowAsync<InvalidOperationException>(() => vm.StartRunCommand.ExecuteAsync(null));
    }

    [Fact]
    public async Task The_choice_is_remembered_separately_for_a_GenBank_file_and_a_FASTA_file()
    {
        var vm = NewViewModel();
        await AddFile(vm, "t.gb", GenBank);
        Choice(vm, "tsv").IsChecked = false;
        Choice(vm, "probs").IsChecked = true;
        await vm.StartRunCommand.ExecuteAsync(null);

        var next = NewViewModel();
        await AddFile(next, "a.fasta", ">a\n" + Dna + "\n");
        Choice(next, "tsv").IsChecked.ShouldBeTrue("a FASTA file has its own remembered choice");
        Choice(next, "probs").IsChecked.ShouldBeFalse();

        var again = NewViewModel();
        await AddFile(again, "t.gb", GenBank);
        Choice(again, "tsv").IsChecked.ShouldBeFalse();
        Choice(again, "probs").IsChecked.ShouldBeTrue();
        Outputs(LastRunOptions()).ShouldContain("probs");
    }

    [Fact]
    public async Task Switching_between_a_FASTA_and_a_GenBank_pill_swaps_the_boxes_and_keeps_each_unsaved_choice()
    {
        var vm = NewViewModel();
        await AddFile(vm, "a.fasta", ">a\n" + Dna + "\n");
        await AddFile(vm, "t.gb", GenBank);
        Choice(vm, "stats").IsChecked = false;

        vm.SelectedItem = vm.Items[1];
        Choice(vm, "stats").IsChecked.ShouldBeTrue("the GenBank boxes were never touched");

        vm.SelectedItem = vm.Items[0];
        Choice(vm, "stats").IsChecked.ShouldBeFalse();
    }

    [Fact]
    public async Task Choosing_nothing_stops_Run_and_says_to_choose_a_file()
    {
        var vm = NewViewModel();
        await AddFile(vm, "a.fasta", ">a\n" + Dna + "\n");
        vm.StartRunCommand.CanExecute(null).ShouldBeTrue();

        foreach (var choice in vm.OutputChoices)
        {
            choice.IsChecked = false;
        }

        vm.HasNoOutputChosen.ShouldBeTrue();
        vm.StartRunCommand.CanExecute(null).ShouldBeFalse();

        Choice(vm, "stats").IsChecked = true;
        vm.HasNoOutputChosen.ShouldBeFalse();
        vm.StartRunCommand.CanExecute(null).ShouldBeTrue();
    }

    [Fact]
    public async Task A_run_that_could_not_start_does_not_remember_the_choice()
    {
        _jobEngine.StartRunAsync(Arg.Any<RunOptions>(), Arg.Any<CancellationToken>()).Returns<Task<string>>(_ => throw new InvalidOperationException("boom"));
        var vm = NewViewModel();
        await AddFile(vm, "a.fasta", ">a\n" + Dna + "\n");
        Choice(vm, "tsv").IsChecked = false;

        await vm.StartRunCommand.ExecuteAsync(null);

        var next = NewViewModel();
        await AddFile(next, "b.fasta", ">b\n" + Dna + "\n");
        Choice(next, "tsv").IsChecked.ShouldBeTrue();
    }
}
