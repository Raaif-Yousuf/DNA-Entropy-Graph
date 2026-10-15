using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Inputs;
using DnaEntropyGraph.Presentation.Services;
using DnaEntropyGraph.Presentation.ViewModels;
using NSubstitute;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Presentation.Tests;

/// <summary>Issue #640: the Settings Run defaults group edits the same <see cref="OutputSelectionStore"/> the New run page reads.</summary>
public sealed class RunDefaultsSettingsTests
{
    private readonly IStringResourceProvider _strings = Substitute.For<IStringResourceProvider>();
    private readonly MemorySettings _settings = new();

    public RunDefaultsSettingsTests() => _strings.GetString(Arg.Any<string>()).Returns(call => call.Arg<string>());

    private readonly QueueDispatcher _dispatcher = new();

    private RunDefaultsSettingsViewModel Create() => new(new OutputSelectionStore(_settings), _strings, _dispatcher);

    private static OutputChoiceItem Box(IEnumerable<OutputChoiceItem> list, string wire) => list.Single(c => c.WireName == wire);

    [Fact]
    public void A_fresh_install_shows_the_catalog_defaults_for_both_input_groups()
    {
        var vm = Create();

        vm.GenBankChoices.Where(c => c.IsChecked).Select(c => c.WireName)
            .ShouldBe(OutputCatalog.ToWireNames(OutputCatalog.Defaults));
        vm.SequenceChoices.Where(c => c.IsChecked).Select(c => c.WireName)
            .ShouldBe(OutputCatalog.ToWireNames(OutputCatalog.Defaults));
    }

    [Fact]
    public void Unticking_a_file_is_what_the_New_run_store_loads_for_that_group_only()
    {
        var vm = Create();

        Box(vm.GenBankChoices, "bedgraph").IsChecked = false;

        var store = new OutputSelectionStore(_settings);
        store.Load(OutputInputKinds.GenBank).HasFlag(OutputFileKinds.BedGraph).ShouldBeFalse();
        store.Load(OutputInputKinds.Sequence).HasFlag(OutputFileKinds.BedGraph).ShouldBeTrue();
    }

    [Fact]
    public void A_choice_saved_by_New_run_is_what_Settings_shows()
    {
        new OutputSelectionStore(_settings).Save(OutputInputKinds.Sequence, OutputFileKinds.Stats | OutputFileKinds.Fasta);

        var vm = Create();

        vm.SequenceChoices.Where(c => c.IsChecked).Select(c => c.WireName).ShouldBe(["fasta", "stats"]);
    }

    [Fact]
    public void The_last_ticked_file_cannot_be_unticked_and_the_row_says_what_to_do()
    {
        new OutputSelectionStore(_settings).Save(OutputInputKinds.GenBank, OutputFileKinds.Stats);
        var vm = Create();

        Box(vm.GenBankChoices, "stats").IsChecked = false;
        _dispatcher.Flush();

        Box(vm.GenBankChoices, "stats").IsChecked.ShouldBeTrue();
        vm.GenBankStatus.ShouldBe("RunDefaultsNoneChosen");
        new OutputSelectionStore(_settings).Load(OutputInputKinds.GenBank).ShouldBe(OutputFileKinds.Stats);
    }

    [Fact]
    public void Ticking_WIG_unticks_the_bedGraph_because_the_worker_writes_one_track_format()
    {
        var vm = Create();

        Box(vm.SequenceChoices, "wig").IsChecked = true;

        Box(vm.SequenceChoices, "bedgraph").IsChecked.ShouldBeFalse();
        var saved = new OutputSelectionStore(_settings).Load(OutputInputKinds.Sequence);
        saved.HasFlag(OutputFileKinds.Wig).ShouldBeTrue();
        saved.HasFlag(OutputFileKinds.BedGraph).ShouldBeFalse();
    }

    [Fact]
    public void A_good_change_clears_an_earlier_refusal()
    {
        new OutputSelectionStore(_settings).Save(OutputInputKinds.GenBank, OutputFileKinds.Stats);
        var vm = Create();
        Box(vm.GenBankChoices, "stats").IsChecked = false;

        Box(vm.GenBankChoices, "tsv").IsChecked = true;

        vm.GenBankStatus.ShouldBeEmpty();
    }

    [Fact]
    public void The_refused_box_is_put_back_after_the_click_finishes_not_inside_it()
    {
        // THEORY (unverified): setting IsChecked inside its own change notification may not repaint through a TwoWay x:Bind.
        // So the put-back is queued on the UI dispatcher and runs after the binding has finished writing the click.
        new OutputSelectionStore(_settings).Save(OutputInputKinds.GenBank, OutputFileKinds.Stats);
        var vm = Create();

        Box(vm.GenBankChoices, "stats").IsChecked = false;

        _dispatcher.Pending.ShouldBe(1);
        _dispatcher.Flush();
        Box(vm.GenBankChoices, "stats").IsChecked.ShouldBeTrue();
    }

    [Fact]
    public void A_failed_save_puts_every_box_back_to_what_is_saved_and_names_one_action()
    {
        var vm = Create();
        _settings.Unavailable = true;

        Box(vm.GenBankChoices, "bedgraph").IsChecked = false;
        _dispatcher.Flush();

        Box(vm.GenBankChoices, "bedgraph").IsChecked.ShouldBeTrue();
        vm.GenBankChoices.Where(c => c.IsChecked).Select(c => c.WireName)
            .ShouldBe(OutputCatalog.ToWireNames(OutputCatalog.Defaults));
        vm.GenBankStatus.ShouldBe("RunDefaultsNotSaved");
    }

    [Fact]
    public void A_failed_save_of_a_WIG_tick_puts_the_bedGraph_back_too()
    {
        var vm = Create();
        _settings.Unavailable = true;

        Box(vm.SequenceChoices, "wig").IsChecked = true;
        _dispatcher.Flush();

        Box(vm.SequenceChoices, "wig").IsChecked.ShouldBeFalse();
        Box(vm.SequenceChoices, "bedgraph").IsChecked.ShouldBeTrue();
    }

    [Fact]
    public void A_locked_settings_file_neither_crashes_the_page_nor_pretends_to_save()
    {
        var vm = Create();
        _settings.Unavailable = true;

        Box(vm.GenBankChoices, "bedgraph").IsChecked = false;

        vm.GenBankStatus.ShouldBe("RunDefaultsNotSaved");
    }

    [Fact]
    public void Opening_the_page_with_a_locked_settings_file_shows_the_defaults()
    {
        _settings.Unavailable = true;

        var vm = Create();

        vm.GenBankChoices.Count(c => c.IsChecked).ShouldBe(OutputCatalog.ToWireNames(OutputCatalog.Defaults).Count);
    }

    [Fact]
    public void Opening_the_page_does_not_save_what_it_just_read()
    {
        _ = Create();

        _settings.Values.ShouldBeEmpty();
    }

    private sealed class QueueDispatcher : IDispatcher
    {
        private readonly Queue<Action> _queue = new();

        public int Pending => _queue.Count;

        public void Enqueue(Action action) => _queue.Enqueue(action);

        public void Flush()
        {
            while (_queue.Count > 0)
            {
                _queue.Dequeue()();
            }
        }
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
