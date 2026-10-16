using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Contract;
using DnaEntropyGraph.Core.Presets;
using DnaEntropyGraph.Core.Runs;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Core.Tests;

/// <summary>Issue #136: built-in and user option presets, their JSON round trip, and what applying one changes.</summary>
public sealed class RunPresetTests
{
    private sealed class MemorySettings : ISettingsStore
    {
        public Dictionary<string, string> Values { get; } = [];

        public string? GetString(string key) => Values.GetValueOrDefault(key);

        public void SetString(string key, string value) => Values[key] = value;
    }

    private static RunOptions Base() => new() { ModelId = "evo2_7b", RunTarget = "Cloud" };

    [Fact]
    public void Plasmid_QC_is_circular_both_combined_with_the_region_calls_on()
    {
        var plasmid = BuiltInPresets.PlasmidQc;

        plasmid.Topology.ShouldBe(Topology.Circular);
        plasmid.Direction.ShouldBe(Direction.BothCombined);
        plasmid.OutputFiles.HasFlag(OutputFileKinds.Regions).ShouldBeTrue();
    }

    [Fact]
    public void The_three_built_ins_have_distinct_ids_each_with_a_real_output_choice()
    {
        BuiltInPresets.All.Select(p => p.Id).Distinct().Count().ShouldBe(3);
        BuiltInPresets.All.ShouldAllBe(p => p.BuiltIn && p.OutputFiles != OutputFileKinds.None);
        BuiltInPresets.All.ShouldAllBe(p => OutputCatalog.Normalize(p.OutputFiles) == p.OutputFiles, "a built-in never ticks both track formats");
        BuiltInPresets.QuickLook.Direction.ShouldBe(Direction.ForwardOnly);
    }

    [Fact]
    public void Applying_a_preset_sets_its_options_and_records_its_name_and_leaves_the_rest()
    {
        var applied = BuiltInPresets.PlasmidQc.ApplyTo(Base() with { TreatAsRna = true, InputPath = "x.gb" });

        applied.Topology.ShouldBe(Topology.Circular);
        applied.Direction.ShouldBe(Direction.BothCombined);
        applied.OutputFiles.ShouldBe(BuiltInPresets.PlasmidQc.OutputFiles);
        applied.PresetId.ShouldBe(BuiltInPresets.PlasmidQc.Id);
        applied.TreatAsRna.ShouldBeTrue();
        applied.InputPath.ShouldBe("x.gb");
        applied.TrackFormat.ShouldBe(TrackFormat.BedGraph);
    }

    [Fact]
    public void A_preset_applied_to_options_survives_the_run_history_json_and_shows_its_id()
    {
        var json = RunOptionsJson.Serialize(BuiltInPresets.PlasmidQc.ApplyTo(Base()));

        var back = RunOptionsJson.TryDeserialize(json)!;

        back.PresetId.ShouldBe(BuiltInPresets.PlasmidQc.Id);
        back.Topology.ShouldBe(Topology.Circular);
    }

    [Fact]
    public void A_saved_user_preset_is_read_back_by_a_new_store_over_the_same_settings()
    {
        var settings = new MemorySettings();
        var outputs = OutputFileKinds.BedGraph | OutputFileKinds.Probs;

        new PresetStore(settings).Save("Bench QC", Direction.ForwardOnly, 2048, Topology.Linear, outputs).Result.ShouldBe(PresetSaveResult.Saved);

        var read = new PresetStore(settings).List().Single(p => !p.BuiltIn);
        read.Name.ShouldBe("Bench QC");
        read.Direction.ShouldBe(Direction.ForwardOnly);
        read.ContextLength.ShouldBe(2048);
        read.Topology.ShouldBe(Topology.Linear);
        read.OutputFiles.ShouldBe(outputs);
        new PresetStore(settings).List().Take(3).ShouldAllBe(p => p.BuiltIn, "built-ins come first");
    }

    [Fact]
    public void A_name_that_is_empty_or_taken_is_refused_and_a_user_name_is_replaced_in_place()
    {
        var store = new PresetStore(new MemorySettings());

        store.Save("  ", Direction.BothCombined, 4096, Topology.Auto, OutputFileKinds.BedGraph).Result.ShouldBe(PresetSaveResult.EmptyName);
        store.Save("plasmid qc", Direction.BothCombined, 4096, Topology.Auto, OutputFileKinds.BedGraph).Result.ShouldBe(PresetSaveResult.NameTaken);
        store.Save("Mine", Direction.BothCombined, 4096, Topology.Auto, OutputFileKinds.BedGraph).Result.ShouldBe(PresetSaveResult.Saved);
        store.Save("MINE", Direction.ForwardOnly, 4096, Topology.Auto, OutputFileKinds.BedGraph).Result.ShouldBe(PresetSaveResult.Saved);

        store.List().Where(p => !p.BuiltIn).Count().ShouldBe(1);
        store.List().Single(p => !p.BuiltIn).Direction.ShouldBe(Direction.ForwardOnly);
    }

    [Fact]
    public void An_empty_output_choice_is_refused()
        => Should.Throw<ArgumentException>(() => new PresetStore(new MemorySettings()).Save("x", Direction.BothCombined, 4096, Topology.Auto, OutputFileKinds.None));

    [Fact]
    public void Delete_removes_a_user_preset_and_never_a_built_in()
    {
        var store = new PresetStore(new MemorySettings());
        var id = store.Save("Mine", Direction.BothCombined, 4096, Topology.Auto, OutputFileKinds.BedGraph).Preset!.Id;

        store.Delete(BuiltInPresets.QuickLook.Id).ShouldBeFalse();
        store.Delete(id).ShouldBeTrue();

        store.List().ShouldAllBe(p => p.BuiltIn);
    }

    [Fact]
    public void Exported_json_imports_into_another_store_with_the_same_content()
    {
        var one = new PresetStore(new MemorySettings());
        one.Save("Bench QC", Direction.ReverseOnly, 1024, Topology.Circular, OutputFileKinds.Wig | OutputFileKinds.Stats);

        var two = new PresetStore(new MemorySettings());
        var result = two.Import(one.ExportJson());

        result.Imported.ShouldBe(1);
        var copy = two.List().Single(p => !p.BuiltIn);
        copy.Name.ShouldBe("Bench QC");
        copy.Direction.ShouldBe(Direction.ReverseOnly);
        copy.Topology.ShouldBe(Topology.Circular);
        copy.OutputFiles.ShouldBe(OutputFileKinds.Wig | OutputFileKinds.Stats);
        one.ExportJson().ShouldNotContain("quick_look", Case.Sensitive, "only the user's own presets are exported");
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"presets\": 5}")]
    [InlineData("{\"schema\":1,\"presets\":[{\"name\":\"x\",\"direction\":\"nope\",\"contextLength\":1,\"topology\":\"auto\",\"outputs\":[\"bedgraph\"]}]}")]
    public void Unreadable_json_imports_nothing_and_says_so(string text)
    {
        var store = new PresetStore(new MemorySettings());

        var result = store.Import(text);

        result.Imported.ShouldBe(0);
        result.Problem.ShouldBeTrue();
        store.List().ShouldAllBe(p => p.BuiltIn);
    }

    [Fact]
    public void A_manifest_names_the_topology_only_when_it_is_not_auto()
    {
        var plasmid = BuiltInPresets.PlasmidQc.ApplyTo(Base());

        Topology(plasmid).ShouldBe("circular");
        Topology(Base()).ShouldBeNull();

        static string? Topology(RunOptions options)
        {
            var json = System.Text.Json.Nodes.JsonNode.Parse(WorkerManifestBuilder.Build(options, "job-1", "inst-1", "1.0.0", null, "bucket", [new StagedInput("x", "a.gb")], DateTimeOffset.UnixEpoch))!;
            return json["analysis"]!["topology"]?.GetValue<string>();
        }
    }
}
