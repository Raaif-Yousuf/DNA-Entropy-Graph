using DnaEntropyGraph.Core.Abstractions;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Core.Tests;

/// <summary>Issue #103: the remembered output choice per input kind (the read side is the point: a setting saved and never read is the house bug).</summary>
public sealed class OutputSelectionStoreTests
{
    private sealed class MemorySettings : ISettingsStore
    {
        public Dictionary<string, string> Values { get; } = [];

        public string? GetString(string key) => Values.GetValueOrDefault(key);

        public void SetString(string key, string value) => Values[key] = value;
    }

    [Theory]
    [InlineData(OutputInputKinds.GenBank)]
    [InlineData(OutputInputKinds.Sequence)]
    public void Nothing_saved_reads_as_the_catalog_defaults(OutputInputKinds group)
        => new OutputSelectionStore(new MemorySettings()).Load(group).ShouldBe(OutputCatalog.Defaults);

    [Fact]
    public void A_saved_choice_is_read_back_by_a_new_store_over_the_same_settings()
    {
        var settings = new MemorySettings();
        var chosen = OutputFileKinds.BedGraph | OutputFileKinds.Stats | OutputFileKinds.Probs;

        new OutputSelectionStore(settings).Save(OutputInputKinds.GenBank, chosen);

        new OutputSelectionStore(settings).Load(OutputInputKinds.GenBank).ShouldBe(chosen);
    }

    [Fact]
    public void The_two_input_groups_remember_separately()
    {
        var store = new OutputSelectionStore(new MemorySettings());

        store.Save(OutputInputKinds.GenBank, OutputFileKinds.Stats);

        store.Load(OutputInputKinds.Sequence).ShouldBe(OutputCatalog.Defaults);
        store.Load(OutputInputKinds.GenBank).ShouldBe(OutputFileKinds.Stats);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("no_such_file,another")]
    public void A_stored_value_naming_no_known_file_falls_back_to_the_defaults(string stored)
    {
        var settings = new MemorySettings();
        settings.Values[OutputSelectionStore.KeyFor(OutputInputKinds.Sequence)] = stored;

        new OutputSelectionStore(settings).Load(OutputInputKinds.Sequence).ShouldBe(OutputCatalog.Defaults);
    }

    [Fact]
    public void Saving_nothing_is_refused_because_a_run_must_return_at_least_one_file()
        => Should.Throw<ArgumentException>(() => new OutputSelectionStore(new MemorySettings()).Save(OutputInputKinds.GenBank, OutputFileKinds.None));

    [Fact]
    public void The_stored_value_is_wire_names_so_a_reordered_enum_cannot_change_it()
    {
        var settings = new MemorySettings();

        new OutputSelectionStore(settings).Save(OutputInputKinds.Sequence, OutputFileKinds.Wig | OutputFileKinds.Regions);

        settings.Values[OutputSelectionStore.KeyFor(OutputInputKinds.Sequence)].ShouldBe("wig,regions");
    }

    [Fact]
    public void A_stored_choice_naming_both_track_formats_loads_as_the_bedgraph_only()
    {
        var settings = new MemorySettings();
        settings.Values[OutputSelectionStore.KeyFor(OutputInputKinds.Sequence)] = "bedgraph,wig,stats";

        new OutputSelectionStore(settings).Load(OutputInputKinds.Sequence).ShouldBe(OutputFileKinds.BedGraph | OutputFileKinds.Stats);
    }
}
