using System.Text.Json;
using DnaEntropyGraph.Core.Abstractions;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Persistence.Tests;

public class SettingsStoreTests
{
    /// <summary>Same reasoning as SqliteDatabaseTests's equivalent test - see that file.</summary>
    [Fact]
    public void Constructing_creates_nothing_on_disk_only_a_write_does()
    {
        using var paths = new TempPaths();
        var nestedPath = Path.Combine(paths.Directory, "nested", "does-not-exist-yet", "settings.json");

        var store = new SettingsStore(nestedPath);

        File.Exists(nestedPath).ShouldBeFalse();
        Directory.Exists(Path.GetDirectoryName(nestedPath)).ShouldBeFalse();

        store.SetString("Theme", "Dark");

        File.Exists(nestedPath).ShouldBeTrue();
    }

    [Fact]
    public void A_value_that_was_never_set_reads_back_null()
    {
        using var paths = new TempPaths();
        ISettingsStore store = new SettingsStore(paths.SettingsPath);

        store.GetString("Theme").ShouldBeNull();
    }

    [Fact]
    public void A_value_that_was_set_reads_back_the_same_value()
    {
        using var paths = new TempPaths();
        ISettingsStore store = new SettingsStore(paths.SettingsPath);

        store.SetString("Theme", "Dark");

        store.GetString("Theme").ShouldBe("Dark");
    }

    [Fact]
    public void Multiple_keys_do_not_clobber_each_other()
    {
        using var paths = new TempPaths();
        ISettingsStore store = new SettingsStore(paths.SettingsPath);

        store.SetString("Theme", "Dark");
        store.SetString("OutputFolder", @"%USERPROFILE%\Downloads");
        store.SetString("Theme", "Light");

        store.GetString("Theme").ShouldBe("Light");
        store.GetString("OutputFolder").ShouldBe(@"%USERPROFILE%\Downloads");
    }

    /// <summary>
    /// Same "relaunch" observable as RunRepositoryTests, for the other half
    /// of local state: a setting saved and never read back is this project's
    /// named wired-to-nothing shape; a setting that does not even survive a
    /// process restart is the same bug one layer down.
    /// </summary>
    [Fact]
    public void A_value_persists_across_store_instances_pointing_at_the_same_file()
    {
        using var paths = new TempPaths();
        var firstLaunch = new SettingsStore(paths.SettingsPath);
        firstLaunch.SetString("Theme", "Dark");

        var secondLaunch = new SettingsStore(paths.SettingsPath);
        secondLaunch.GetString("Theme").ShouldBe("Dark");
    }

    [Fact]
    public void A_corrupt_settings_file_is_recovered_from_rather_than_thrown()
    {
        using var paths = new TempPaths();
        File.WriteAllText(paths.SettingsPath, "{ not valid json ][");
        var store = new SettingsStore(paths.SettingsPath);

        var value = store.GetString("Theme");

        value.ShouldBeNull();
        store.RecoveredFromUnreadableFile.ShouldBeTrue();

        // The next write must produce a fresh, valid file - recovery is not
        // a one-time read-only fallback that leaves the file broken forever.
        store.SetString("Theme", "System");
        var reopened = new SettingsStore(paths.SettingsPath);
        reopened.GetString("Theme").ShouldBe("System");
    }

    [Fact]
    public void Every_write_leaves_a_valid_json_file_and_no_temp_file_behind()
    {
        using var paths = new TempPaths();
        var store = new SettingsStore(paths.SettingsPath);

        for (var i = 0; i < 5; i++)
        {
            store.SetString($"Key{i}", $"Value{i}");

            var json = File.ReadAllText(paths.SettingsPath);
            Should.NotThrow(() => JsonDocument.Parse(json));
        }

        Directory.GetFiles(paths.Directory, "*.tmp-*").ShouldBeEmpty();
    }

    // ---- #558: an unreadable or oddly-typed settings.json must never cost the user data ----

    [Fact]
    public void The_issue_558_file_reads_back_the_id_and_the_number_as_a_string()
    {
        using var paths = new TempPaths();
        File.WriteAllText(paths.SettingsPath, """{"installation_id":"abc","idle_stopped_vm_hours":1}""");
        var store = new SettingsStore(paths.SettingsPath);

        store.GetString("installation_id").ShouldBe("abc");
        store.GetString("idle_stopped_vm_hours").ShouldBe("1");
        store.RecoveredFromUnreadableFile.ShouldBeFalse();
    }

    [Fact]
    public void A_SetString_keeps_every_other_key_with_its_original_json_type()
    {
        using var paths = new TempPaths();
        File.WriteAllText(
            paths.SettingsPath,
            """{"installation_id":"abc","idle_stopped_vm_hours":1,"developer_mode":true,"gone":null,"future":{"a":[1,2]}}""");
        var store = new SettingsStore(paths.SettingsPath);

        store.SetString("Theme", "Dark");

        using var doc = JsonDocument.Parse(File.ReadAllText(paths.SettingsPath));
        var root = doc.RootElement;
        root.GetProperty("installation_id").GetString().ShouldBe("abc");
        root.GetProperty("idle_stopped_vm_hours").ValueKind.ShouldBe(JsonValueKind.Number);
        root.GetProperty("idle_stopped_vm_hours").GetInt32().ShouldBe(1);
        root.GetProperty("developer_mode").ValueKind.ShouldBe(JsonValueKind.True);
        root.GetProperty("gone").ValueKind.ShouldBe(JsonValueKind.Null);
        root.GetProperty("future").GetProperty("a").GetArrayLength().ShouldBe(2);
        root.GetProperty("Theme").GetString().ShouldBe("Dark");
    }

    [Fact]
    public void Non_string_values_read_as_their_invariant_text_and_null_reads_as_null()
    {
        using var paths = new TempPaths();
        File.WriteAllText(paths.SettingsPath, """{"n":1.5,"b":false,"z":null,"o":{"a":1}}""");
        var store = new SettingsStore(paths.SettingsPath);

        store.GetString("n").ShouldBe("1.5");
        store.GetString("b").ShouldBe("false");
        store.GetString("z").ShouldBeNull();
        store.GetString("o").ShouldBe("""{"a":1}""");
    }

    [Fact]
    public void A_well_formed_file_is_not_flagged_and_no_unreadable_copy_appears()
    {
        using var paths = new TempPaths();
        var store = new SettingsStore(paths.SettingsPath);
        store.SetString("Theme", "Dark");
        store.SetString("Theme", "Light");

        store.RecoveredFromUnreadableFile.ShouldBeFalse();
        Directory.GetFiles(paths.Directory, "settings.json.unreadable-*").ShouldBeEmpty();
    }

    [Fact]
    public void A_corrupt_file_is_kept_beside_the_original_before_any_write_and_the_flag_is_set()
    {
        using var paths = new TempPaths();
        const string Corrupt = "{ not valid json ][";
        File.WriteAllText(paths.SettingsPath, Corrupt);
        var store = new SettingsStore(paths.SettingsPath);

        store.GetString("Theme").ShouldBeNull();
        store.RecoveredFromUnreadableFile.ShouldBeTrue();
        // A read alone never touches the file.
        File.ReadAllText(paths.SettingsPath).ShouldBe(Corrupt);

        store.SetString("Theme", "System");

        var kept = Directory.GetFiles(paths.Directory, "settings.json.unreadable-*");
        kept.Length.ShouldBe(1);
        File.ReadAllText(kept[0]).ShouldBe(Corrupt);
        System.Text.RegularExpressions.Regex
            .IsMatch(Path.GetFileName(kept[0]), @"^settings\.json\.unreadable-\d{8}-\d{6}")
            .ShouldBeTrue();
        new SettingsStore(paths.SettingsPath).GetString("Theme").ShouldBe("System");
    }

    [Fact]
    public void A_second_unreadable_file_in_the_same_second_does_not_overwrite_the_first_kept_copy()
    {
        using var paths = new TempPaths();
        File.WriteAllText(paths.SettingsPath, "{ first ][");
        new SettingsStore(paths.SettingsPath).SetString("a", "1");
        File.WriteAllText(paths.SettingsPath, "{ second ][");
        new SettingsStore(paths.SettingsPath).SetString("a", "1");

        var contents = Directory.GetFiles(paths.Directory, "settings.json.unreadable-*").Select(File.ReadAllText).ToList();
        contents.Count.ShouldBe(2);
        contents.ShouldContain("{ first ][");
        contents.ShouldContain("{ second ][");
    }

    [Theory]
    [InlineData("""{"installation_id":"abc","Theme":"Da""")]
    [InlineData("""{"installation_id":"abc" "Theme":"Dark"}""")]
    [InlineData("{\"installation_id\":\"abc\"")]
    [InlineData("""{"Theme":"Dark",,"installation_id":"abc"}""")]
    public void An_unparseable_file_never_mints_a_new_installation_id(string content)
    {
        using var paths = new TempPaths();
        File.WriteAllText(paths.SettingsPath, content);
        var store = new SettingsStore(paths.SettingsPath);

        var id = Core.Cloud.InstallationId.GetOrCreate(store);

        content.ShouldContain("abc"); // vacuity: the fixture really holds an id
        id.ShouldBe("abc");
        new SettingsStore(paths.SettingsPath).GetString("installation_id").ShouldBe("abc");
    }

    [Fact]
    public void An_unparseable_file_with_no_id_in_it_still_gets_one_and_keeps_the_copy()
    {
        using var paths = new TempPaths();
        File.WriteAllText(paths.SettingsPath, "garbage");
        var store = new SettingsStore(paths.SettingsPath);

        var id = Core.Cloud.InstallationId.GetOrCreate(store);

        id.ShouldNotBeNullOrEmpty();
        Directory.GetFiles(paths.Directory, "settings.json.unreadable-*").Length.ShouldBe(1);
    }

    [Fact]
    public void Other_string_and_scalar_values_are_salvaged_from_an_unparseable_file()
    {
        using var paths = new TempPaths();
        File.WriteAllText(paths.SettingsPath, """{"Theme":"Dark","hours":2,"path":"C:\\x","cut":"tru""");
        var store = new SettingsStore(paths.SettingsPath);

        store.SetString("other", "v");

        var reopened = new SettingsStore(paths.SettingsPath);
        reopened.GetString("Theme").ShouldBe("Dark");
        reopened.GetString("hours").ShouldBe("2");
        reopened.GetString("path").ShouldBe(@"C:\x");
        reopened.GetString("other").ShouldBe("v");
    }

    [Fact]
    public void A_file_that_cannot_be_read_because_it_is_locked_is_never_overwritten()
    {
        using var paths = new TempPaths();
        File.WriteAllText(paths.SettingsPath, """{"installation_id":"abc"}""");
        var store = new SettingsStore(paths.SettingsPath);

        using (new FileStream(paths.SettingsPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Should.Throw<IOException>(() => store.SetString("Theme", "Dark"));
        }

        File.ReadAllText(paths.SettingsPath).ShouldBe("""{"installation_id":"abc"}""");
    }
}
