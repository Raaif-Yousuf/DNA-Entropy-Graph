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
    [InlineData("""{"installation_id":"abc",,"Theme":"Dark"}""")]
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
        // The id no longer touches settings.json, so the damaged file is still untouched here...
        File.ReadAllText(paths.SettingsPath).ShouldBe("garbage");
        Directory.GetFiles(paths.Directory, "settings.json.unreadable-*").ShouldBeEmpty();

        // ...and the first real settings write keeps a copy before replacing it.
        store.SetString("Theme", "Dark");
        Directory.GetFiles(paths.Directory, "settings.json.unreadable-*").Length.ShouldBe(1);
        store.GetString("installation_id").ShouldBe(id);
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

    // ---- #558 round 2: transient IO, typed exception, never the recovered flag ----

    [Fact]
    public void A_persistent_lock_retries_a_bounded_number_of_times_then_throws_the_typed_exception_and_changes_nothing()
    {
        using var paths = new TempPaths();
        const string Original = """{"Theme":"Dark"}""";
        File.WriteAllText(paths.SettingsPath, Original);
        var time = new ManualTimeProvider();
        var store = new SettingsStore(paths.SettingsPath) { Clock = time, Pause = time.Advance };

        using (new FileStream(paths.SettingsPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            // Virtual time (#625): five 40 ms pauses (160 ms) fit the 300 ms budget, so the count is the attempt limit, not a race.
            Should.Throw<SettingsUnavailableException>(() => store.GetString("Theme"));
            store.LastReadAttempts.ShouldBe(SettingsStore.ReadAttemptLimit);
            Should.Throw<SettingsUnavailableException>(() => store.SetString("Theme", "Light"));
        }

        store.RecoveredFromUnreadableFile.ShouldBeFalse();
        File.ReadAllText(paths.SettingsPath).ShouldBe(Original);
        Directory.GetFiles(paths.Directory, "settings.json.unreadable-*").ShouldBeEmpty();
        store.GetString("Theme").ShouldBe("Dark");
    }

    [Fact]
    public void A_persistent_lock_gives_up_when_the_wait_budget_is_spent_before_the_attempt_limit()
    {
        using var paths = new TempPaths();
        File.WriteAllText(paths.SettingsPath, """{"Theme":"Dark"}""");
        var time = new ManualTimeProvider();
        // Each pause costs 150 ms of the 300 ms budget: attempt 1, pause, attempt 2, pause, attempt 3 finds the budget spent.
        var store = new SettingsStore(paths.SettingsPath) { Clock = time, Pause = _ => time.Advance(TimeSpan.FromMilliseconds(150)) };

        using var held = new FileStream(paths.SettingsPath, FileMode.Open, FileAccess.Read, FileShare.None);
        Should.Throw<SettingsUnavailableException>(() => store.GetString("Theme"));

        store.LastReadAttempts.ShouldBe(3);
        store.LastReadAttempts.ShouldBeLessThan(SettingsStore.ReadAttemptLimit);
    }

    /// <summary>A clock that moves only when the test (or the store's pause) moves it.</summary>
    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 3, 8, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    [Fact]
    public void A_lock_that_clears_within_the_retry_budget_is_read_normally()
    {
        using var paths = new TempPaths();
        File.WriteAllText(paths.SettingsPath, """{"Theme":"Dark"}""");
        var store = new SettingsStore(paths.SettingsPath);

        var holder = new FileStream(paths.SettingsPath, FileMode.Open, FileAccess.Read, FileShare.None);
        var release = new Thread(() =>
        {
            Thread.Sleep(80);
            holder.Dispose();
        });
        release.Start();

        store.GetString("Theme").ShouldBe("Dark");
        release.Join();
        store.LastReadAttempts.ShouldBeGreaterThan(1);
        store.RecoveredFromUnreadableFile.ShouldBeFalse();
    }

    // ---- #558 round 2: the Utf8JsonReader salvage ----

    [Fact]
    public void A_number_cut_off_at_end_of_input_is_dropped_but_the_complete_id_before_it_is_kept()
    {
        using var paths = new TempPaths();
        File.WriteAllText(paths.SettingsPath, """{"installation_id":"abc","hours":12""");
        var store = new SettingsStore(paths.SettingsPath);

        store.GetString("hours").ShouldBeNull();
        Core.Cloud.InstallationId.GetOrCreate(store).ShouldBe("abc");
    }

    [Fact]
    public void A_number_followed_by_more_text_is_complete_and_keeps_its_json_type_after_salvage()
    {
        using var paths = new TempPaths();
        File.WriteAllText(paths.SettingsPath, """{"hours":12,"flag":true,"nothing":null,"Theme":"Dark" "x":1}""");
        var store = new SettingsStore(paths.SettingsPath);

        store.SetString("other", "v");

        using var doc = JsonDocument.Parse(File.ReadAllText(paths.SettingsPath));
        doc.RootElement.GetProperty("hours").ValueKind.ShouldBe(JsonValueKind.Number);
        doc.RootElement.GetProperty("hours").GetInt32().ShouldBe(12);
        doc.RootElement.GetProperty("flag").ValueKind.ShouldBe(JsonValueKind.True);
        doc.RootElement.GetProperty("nothing").ValueKind.ShouldBe(JsonValueKind.Null);
        doc.RootElement.GetProperty("Theme").GetString().ShouldBe("Dark");
        doc.RootElement.TryGetProperty("x", out _).ShouldBeFalse();
    }

    [Fact]
    public void Nested_keys_and_text_inside_strings_are_never_promoted_by_salvage()
    {
        using var paths = new TempPaths();
        File.WriteAllText(
            paths.SettingsPath,
            """{"outer":{"inner":"v","deep":[{"k":"w"}]},"note":"\"fake\":\"x\"","keep":"1" "broken":""");
        var store = new SettingsStore(paths.SettingsPath);

        store.GetString("inner").ShouldBeNull();
        store.GetString("k").ShouldBeNull();
        store.GetString("fake").ShouldBeNull();
        store.GetString("keep").ShouldBe("1");
        store.GetString("note").ShouldBe("\"fake\":\"x\"");
        store.GetString("outer").ShouldBeNull();
    }

    // ---- #558 round 2: the write-once installation id file ----

    private static string IdPath(TempPaths paths) => Path.Combine(paths.Directory, "installation_id");

    [Fact]
    public void The_installation_id_lives_in_its_own_file_and_not_in_settings_json()
    {
        using var paths = new TempPaths();
        var store = new SettingsStore(paths.SettingsPath);

        var id = Core.Cloud.InstallationId.GetOrCreate(store);

        File.ReadAllText(IdPath(paths)).Trim().ShouldBe(id);
        File.Exists(paths.SettingsPath).ShouldBeFalse();
        Core.Cloud.InstallationId.GetOrCreate(new SettingsStore(paths.SettingsPath)).ShouldBe(id);
    }

    [Theory]
    [InlineData("garbage ][")]
    [InlineData("")]
    [InlineData("""{"installation_id":"other","Theme":"Da""")]
    public void A_corrupt_settings_json_never_changes_an_existing_id_file(string settingsContent)
    {
        using var paths = new TempPaths();
        File.WriteAllText(IdPath(paths), "keepme-123");
        File.WriteAllText(paths.SettingsPath, settingsContent);
        var store = new SettingsStore(paths.SettingsPath);

        Core.Cloud.InstallationId.GetOrCreate(store).ShouldBe("keepme-123");
        store.SetString("Theme", "Dark");
        Core.Cloud.InstallationId.GetOrCreate(new SettingsStore(paths.SettingsPath)).ShouldBe("keepme-123");
        File.ReadAllText(IdPath(paths)).Trim().ShouldBe("keepme-123");
    }

    [Fact]
    public void An_id_in_settings_json_is_migrated_into_the_id_file_once()
    {
        using var paths = new TempPaths();
        File.WriteAllText(paths.SettingsPath, """{"installation_id":"legacy-id","Theme":"Dark"}""");
        var store = new SettingsStore(paths.SettingsPath);

        Core.Cloud.InstallationId.GetOrCreate(store).ShouldBe("legacy-id");

        File.ReadAllText(IdPath(paths)).Trim().ShouldBe("legacy-id");
        // A later settings.json edit cannot change the id any more.
        File.WriteAllText(paths.SettingsPath, """{"installation_id":"someone-else"}""");
        Core.Cloud.InstallationId.GetOrCreate(new SettingsStore(paths.SettingsPath)).ShouldBe("legacy-id");
    }

    [Theory]
    [InlineData("""{"installation_id":123}""")]
    [InlineData("""{"installation_id":"NOT VALID!"}""")]
    [InlineData("""{"installation_id":null}""")]
    [InlineData("""{"x":{"installation_id":"nested-id"}}""")]
    public void Only_a_complete_valid_string_id_is_migrated_otherwise_a_new_id_is_minted(string content)
    {
        using var paths = new TempPaths();
        File.WriteAllText(paths.SettingsPath, content);
        var store = new SettingsStore(paths.SettingsPath);

        var id = Core.Cloud.InstallationId.GetOrCreate(store);

        id.ShouldNotBeNullOrEmpty();
        id.ShouldNotBe("nested-id");
        id.ShouldNotBe("123");
        File.ReadAllText(IdPath(paths)).Trim().ShouldBe(id);
    }

    [Fact]
    public void An_invalid_id_file_is_never_overwritten_never_replaced_by_a_new_id_and_is_kept_aside()
    {
        using var paths = new TempPaths();
        File.WriteAllText(IdPath(paths), "Not A Valid Id!");
        var store = new SettingsStore(paths.SettingsPath);

        Should.Throw<InstallationIdUnusableException>(() => Core.Cloud.InstallationId.GetOrCreate(store));
        Should.Throw<InstallationIdUnusableException>(() => Core.Cloud.InstallationId.GetOrCreate(store));

        File.ReadAllText(IdPath(paths)).ShouldBe("Not A Valid Id!");
        var aside = Directory.GetFiles(paths.Directory, "installation_id.invalid-*");
        aside.Length.ShouldBe(1);
        File.ReadAllText(aside[0]).ShouldBe("Not A Valid Id!");
        Should.Throw<InstallationIdUnusableException>(() => store.SetString(Core.Cloud.InstallationId.SettingsKey, "fresh-id"));
        File.ReadAllText(IdPath(paths)).ShouldBe("Not A Valid Id!");
    }

    [Fact]
    public void The_id_file_is_created_with_CreateNew_so_a_loser_never_overwrites_the_winner()
    {
        using var paths = new TempPaths();
        var store = new SettingsStore(paths.SettingsPath);

        store.WriteIdFileIfAbsent("winner-id");
        store.WriteIdFileIfAbsent("loser-id");

        File.ReadAllText(IdPath(paths)).ShouldBe("winner-id");
    }

    [Fact]
    public void An_empty_id_file_left_by_a_crashed_first_write_is_treated_as_absent()
    {
        using var paths = new TempPaths();
        File.WriteAllText(IdPath(paths), string.Empty);
        var store = new SettingsStore(paths.SettingsPath);

        var id = Core.Cloud.InstallationId.GetOrCreate(store);

        File.ReadAllText(IdPath(paths)).Trim().ShouldBe(id);
    }

    [Fact]
    public void Two_stores_racing_on_a_first_run_converge_on_one_id()
    {
        for (var round = 0; round < 25; round++)
        {
            using var paths = new TempPaths();
            var a = new SettingsStore(paths.SettingsPath);
            var b = new SettingsStore(paths.SettingsPath);
            string? seenByA = null;
            string? seenByB = null;

            RunConcurrently(
                () =>
                {
                    a.SetString(Core.Cloud.InstallationId.SettingsKey, "id-from-a");
                    seenByA = a.GetString(Core.Cloud.InstallationId.SettingsKey);
                },
                () =>
                {
                    b.SetString(Core.Cloud.InstallationId.SettingsKey, "id-from-b");
                    seenByB = b.GetString(Core.Cloud.InstallationId.SettingsKey);
                });

            seenByA.ShouldBe(seenByB);
            new[] { "id-from-a", "id-from-b" }.ShouldContain(seenByA!);
            File.ReadAllText(IdPath(paths)).Trim().ShouldBe(seenByA!);
        }
    }

    [Fact]
    public void GetOrCreate_returns_the_id_that_won_not_the_one_it_minted_when_another_writer_got_there_first()
    {
        using var paths = new TempPaths();
        var inner = new SettingsStore(paths.SettingsPath);
        var racing = new WinnerAlreadyWroteStore(inner, "winner-id");

        Core.Cloud.InstallationId.GetOrCreate(racing).ShouldBe("winner-id");
    }

    private sealed class WinnerAlreadyWroteStore(SettingsStore inner, string winner) : ISettingsStore
    {
        private bool _raced;

        public string? GetString(string key) => inner.GetString(key);

        public void SetString(string key, string value)
        {
            if (!_raced)
            {
                _raced = true;
                inner.SetString(key, winner);
            }

            inner.SetString(key, value);
        }
    }

    /// <summary>Starts every action on its own thread at the same instant and rethrows the first failure.</summary>
    private static void RunConcurrently(params Action[] actions)
    {
        using var barrier = new Barrier(actions.Length);
        var failures = new List<Exception>();
        var threads = actions.Select(action => new Thread(() =>
        {
            try
            {
                barrier.SignalAndWait();
                action();
            }
            catch (Exception ex)
            {
                lock (failures)
                {
                    failures.Add(ex);
                }
            }
        })).ToList();

        threads.ForEach(t => t.Start());
        threads.ForEach(t => t.Join());
        if (failures.Count > 0)
        {
            throw new AggregateException(failures);
        }
    }

    // ---- #558 round 2: the cross-process lock ----

    [Fact]
    public void Two_store_instances_on_one_path_never_drop_each_others_keys()
    {
        using var paths = new TempPaths();
        var a = new SettingsStore(paths.SettingsPath);
        var b = new SettingsStore(paths.SettingsPath);
        a.SetString("seed", "1");
        const int Count = 40;

        RunConcurrently(
            () =>
            {
                for (var i = 0; i < Count; i++)
                {
                    a.SetString($"a{i}", "x");
                }
            },
            () =>
            {
                for (var i = 0; i < Count; i++)
                {
                    b.SetString($"b{i}", "x");
                }
            });

        var reopened = new SettingsStore(paths.SettingsPath);
        reopened.GetString("seed").ShouldBe("1");
        for (var i = 0; i < Count; i++)
        {
            reopened.GetString($"a{i}").ShouldBe("x");
            reopened.GetString($"b{i}").ShouldBe("x");
        }
    }
}
