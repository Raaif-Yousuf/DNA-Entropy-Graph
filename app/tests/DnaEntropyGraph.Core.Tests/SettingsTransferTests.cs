using System.Text.Json.Nodes;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Presets;
using DnaEntropyGraph.Core.SettingsPortability;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Core.Tests;

/// <summary>Issue #242: settings and presets as versioned JSON. Secrets and machine paths never leave; an unknown future version is refused.</summary>
public sealed class SettingsTransferTests
{
    private sealed class MemorySettings : ISettingsStore
    {
        public Dictionary<string, string> Values { get; } = [];

        public string? GetString(string key) => Values.GetValueOrDefault(key);

        public void SetString(string key, string value) => Values[key] = value;
    }

    private static (SettingsTransfer Transfer, MemorySettings Store) Make(MemorySettings? store = null)
    {
        store ??= new MemorySettings();
        return (new SettingsTransfer(store, new PresetStore(store)), store);
    }

    private static string File(int schema, JsonObject settings, JsonArray? presets = null)
    {
        var root = new JsonObject { ["format"] = SettingsTransfer.FormatName, ["schema"] = schema, ["settings"] = settings };
        if (presets is not null)
        {
            root["presets"] = new JsonObject { ["schema"] = 1, ["presets"] = presets };
        }

        return root.ToJsonString();
    }

    [Fact]
    public void Round_trip_moves_defaults_and_presets_to_another_install()
    {
        var (a, aStore) = Make();
        aStore.Values["Theme"] = "Dark";
        aStore.Values["notify_when_done"] = "false";
        aStore.Values["output_files_genbank"] = "bedgraph,stats";
        new PresetStore(aStore).Save("Lab default", Direction.ForwardOnly, 8192, Topology.Circular, OutputFileKinds.BedGraph).Result.ShouldBe(PresetSaveResult.Saved);

        var (b, bStore) = Make();
        var plan = b.Plan(a.Export());
        plan.Status.ShouldBe(SettingsImportStatus.Ok);
        plan.Changes.Select(c => c.Key).ShouldBe(["Theme", "notify_when_done", "output_files_genbank"], ignoreOrder: true);
        plan.PresetCount.ShouldBe(1);

        b.Apply(plan);

        bStore.Values["Theme"].ShouldBe("Dark");
        bStore.Values["notify_when_done"].ShouldBe("false");
        bStore.Values["output_files_genbank"].ShouldBe("bedgraph,stats");
        new PresetStore(bStore).List().ShouldContain(p => p.Name == "Lab default");
    }

    [Fact]
    public void Export_never_carries_a_secret_a_path_or_an_id()
    {
        var (t, store) = Make();
        foreach (var key in SettingsCatalog.Excluded.Keys)
        {
            store.Values[key] = "SECRET-" + key;
        }

        // user_presets holds a non-preset marker here; the presets block is built from the preset store, not from the raw key.
        var json = t.Export();

        json.ShouldNotContain("SECRET-");
        JsonNode.Parse(json)!["schema"]!.GetValue<int>().ShouldBe(SettingsTransfer.SchemaVersion);
    }

    [Fact]
    public void An_excluded_key_in_an_imported_file_is_ignored_and_counted()
    {
        var (t, store) = Make();
        var plan = t.Plan(File(1, new JsonObject { ["installation_id"] = "evil", ["Viewer.IgvPath"] = "C:\\x.exe", ["developer_mode"] = "true", ["Theme"] = "Light" }));

        plan.Changes.Select(c => c.Key).ShouldBe(["Theme"]);
        plan.IgnoredCount.ShouldBe(3);
        t.Apply(plan);
        store.Values.Keys.ShouldBe(["Theme"]);
    }

    [Fact]
    public void A_future_schema_is_refused_with_its_own_status()
    {
        var (t, store) = Make();
        var plan = t.Plan(File(SettingsTransfer.SchemaVersion + 1, new JsonObject { ["Theme"] = "Light" }));

        plan.Status.ShouldBe(SettingsImportStatus.NewerVersion);
        plan.Changes.ShouldBeEmpty();
        t.Apply(plan).ShouldBe(0);
        store.Values.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    public void Unreadable_text_is_NotJson(string text)
        => Make().Transfer.Plan(text).Status.ShouldBe(SettingsImportStatus.NotJson);

    [Fact]
    public void A_duplicate_key_is_NotJson_not_a_crash()
        => Make().Transfer.Plan("{\"format\":\"x\",\"format\":\"y\"}").Status.ShouldBe(SettingsImportStatus.NotJson);

    [Fact]
    public void Preview_counts_presets_against_this_pcs_own_store()
    {
        var (a, aStore) = Make();
        var presets = new PresetStore(aStore);
        presets.Save("Lab default", Direction.ForwardOnly, 8192, Topology.Circular, OutputFileKinds.BedGraph);
        presets.Save("Second", Direction.ForwardOnly, 4096, Topology.Linear, OutputFileKinds.BedGraph);
        var file = a.Export();

        // The same file into the same store changes nothing, so the preview must not promise two presets.
        a.Plan(file).PresetCount.ShouldBe(0);

        // A fresh PC gets both; a PC that has one of them identical gets only the other.
        var (b, bStore) = Make();
        b.Plan(file).PresetCount.ShouldBe(2);
        new PresetStore(bStore).Save("Second", Direction.ForwardOnly, 4096, Topology.Linear, OutputFileKinds.BedGraph);
        b.Plan(file).PresetCount.ShouldBe(1);

        // Planning never writes.
        new PresetStore(bStore).List().Count(p => !p.BuiltIn).ShouldBe(1);
    }

    [Fact]
    public void A_json_file_that_is_not_ours_is_refused()
        => Make().Transfer.Plan("{\"schema\":1,\"settings\":{\"Theme\":\"Dark\"}}").Status.ShouldBe(SettingsImportStatus.NotOurFile);

    [Theory]
    [InlineData("Theme", "Purple")]
    [InlineData("notify_when_done", "maybe")]
    [InlineData("update_channel", "nightly")]
    [InlineData("idle_stopped_vm_hours", "-3")]
    [InlineData("results_retention_days", "abc")]
    [InlineData("output_files_genbank", "bedgraph,nonsense")]
    [InlineData("run.target", "Moon")]
    [InlineData("Viewer.IgvPort", "99999")]
    public void A_value_the_app_would_not_have_written_is_dropped(string key, string value)
    {
        var plan = Make().Transfer.Plan(File(1, new JsonObject { [key] = value }));

        plan.Changes.ShouldBeEmpty();
        plan.RejectedCount.ShouldBe(1);
        plan.Status.ShouldBe(SettingsImportStatus.NothingToImport);
    }

    [Fact]
    public void A_non_string_value_is_rejected_not_thrown_on()
    {
        var plan = Make().Transfer.Plan(File(1, new JsonObject { ["Theme"] = new JsonObject { ["x"] = 1 }, ["notify_when_done"] = true }));

        plan.Changes.ShouldBeEmpty();
        plan.RejectedCount.ShouldBe(2);
    }

    [Fact]
    public void The_plan_lists_only_what_would_change_and_shows_the_current_value()
    {
        var (t, store) = Make();
        store.Values["Theme"] = "Dark";
        store.Values["notify_when_done"] = "true";

        var plan = t.Plan(File(1, new JsonObject { ["Theme"] = "Dark", ["notify_when_done"] = "false" }));

        var change = plan.Changes.ShouldHaveSingleItem();
        change.Key.ShouldBe("notify_when_done");
        change.Current.ShouldBe("true");
        change.Incoming.ShouldBe("false");
    }

    [Fact]
    public void Planning_writes_nothing()
    {
        var (t, store) = Make();
        t.Plan(File(1, new JsonObject { ["Theme"] = "Light" }));
        store.Values.ShouldBeEmpty();
    }

    [Fact]
    public void A_file_with_a_bad_preset_still_imports_the_good_settings()
    {
        var (t, store) = Make();
        var plan = t.Plan(File(1, new JsonObject { ["Theme"] = "Light" }, [new JsonObject { ["name"] = "", ["direction"] = "bogus" }]));

        plan.Status.ShouldBe(SettingsImportStatus.Ok);
        t.Apply(plan);
        store.Values["Theme"].ShouldBe("Light");
    }

    [Fact]
    public void Every_exported_key_has_a_label_and_no_key_is_both_exported_and_excluded()
    {
        SettingsCatalog.Exported.ShouldAllBe(e => !string.IsNullOrWhiteSpace(e.LabelKey));
        SettingsCatalog.Exported.Select(e => e.Key).Intersect(SettingsCatalog.Excluded.Keys).ShouldBeEmpty();
        SettingsCatalog.Excluded.Values.ShouldAllBe(reason => reason.Length > 0);
    }
}
