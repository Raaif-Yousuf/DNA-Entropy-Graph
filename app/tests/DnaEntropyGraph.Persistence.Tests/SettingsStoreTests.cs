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
        store.SetString("OutputFolder", @"C:\Users\lab\Downloads");
        store.SetString("Theme", "Light");

        store.GetString("Theme").ShouldBe("Light");
        store.GetString("OutputFolder").ShouldBe(@"C:\Users\lab\Downloads");
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
}
