using System.Diagnostics;
using System.Text;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Cloud;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Persistence.Tests;

/// <summary>#558 round 3: typed write-path failures, lenient id recovery, atomic id file, whitespace id file, bounded UI-thread wait.</summary>
public class SettingsStoreHardeningTests
{
    private const string ValidId = "0123456789abcdef0123456789abcdef";

    private static string IdPath(TempPaths paths) => Path.Combine(paths.Directory, "installation_id");

    private static Action<string> ThrowAt(string operation, Exception failure) => op =>
    {
        if (op == operation)
        {
            throw failure;
        }
    };

    // ---- 1. write-path exceptions are typed ----

    [Theory]
    [InlineData("mkdir", false)]
    [InlineData("temp-create", false)]
    [InlineData("mutex", false)]
    [InlineData("mkdir", true)]
    [InlineData("temp-create", true)]
    [InlineData("mutex", true)]
    public void A_write_path_failure_surfaces_as_the_typed_exception_with_the_cause_kept(string operation, bool accessDenied)
    {
        using var paths = new TempPaths();
        Exception cause = accessDenied ? new UnauthorizedAccessException("denied") : new IOException("disk full");
        var store = new SettingsStore(paths.SettingsPath) { FaultHook = ThrowAt(operation, cause) };

        var ex = Should.Throw<SettingsUnavailableException>(() => store.SetString("Theme", "Dark"));

        ex.InnerException.ShouldBeSameAs(cause);
    }

    [Fact]
    public void A_failing_keep_aside_copy_surfaces_as_the_typed_exception_and_changes_nothing()
    {
        using var paths = new TempPaths();
        File.WriteAllText(paths.SettingsPath, """{"Theme":"Da""");
        var store = new SettingsStore(paths.SettingsPath) { FaultHook = ThrowAt("copy", new UnauthorizedAccessException("denied")) };

        Should.Throw<SettingsUnavailableException>(() => store.SetString("Theme", "Dark"));

        File.ReadAllText(paths.SettingsPath).ShouldBe("""{"Theme":"Da""");
    }

    [Theory]
    [InlineData("mkdir")]
    [InlineData("temp-create")]
    [InlineData("mutex")]
    public void An_installation_id_write_failure_surfaces_as_the_typed_exception(string operation)
    {
        using var paths = new TempPaths();
        var store = new SettingsStore(paths.SettingsPath) { FaultHook = ThrowAt(operation, new UnauthorizedAccessException("denied")) };

        Should.Throw<SettingsUnavailableException>(() => InstallationId.GetOrCreate(store));
        File.Exists(IdPath(paths)).ShouldBeFalse();
    }

    [Fact]
    public void A_settings_folder_that_cannot_be_created_surfaces_as_the_typed_exception()
    {
        using var paths = new TempPaths();
        var blocker = Path.Combine(paths.Directory, "blocker");
        File.WriteAllText(blocker, "a file where a folder must go");
        var store = new SettingsStore(Path.Combine(blocker, "settings.json"));

        Should.Throw<SettingsUnavailableException>(() => store.SetString("Theme", "Dark"));
        Should.Throw<SettingsUnavailableException>(() => InstallationId.GetOrCreate(store));
    }

    // ---- 2. a corrupt settings.json never mints a new id when one is recoverable ----

    [Theory]
    [InlineData("{\"a\":\"abc, \"installation_id\":\"0123456789abcdef0123456789abcdef\"}")]
    [InlineData("{\"x\":[1,2, \"installation_id\":\"0123456789abcdef0123456789abcdef\", \"Theme\":\"Da")]
    [InlineData("{ \"Theme\" : \"Da\"\n, \"installation_id\"  :\n \"0123456789abcdef0123456789abcdef\" ,")]
    public void An_id_after_the_corruption_point_is_recovered_not_replaced(string content)
    {
        using var paths = new TempPaths();
        File.WriteAllText(paths.SettingsPath, content);
        var store = new SettingsStore(paths.SettingsPath);

        InstallationId.GetOrCreate(store).ShouldBe(ValidId);

        File.ReadAllText(IdPath(paths)).Trim().ShouldBe(ValidId);
    }

    [Theory]
    [InlineData("{\"Theme\":\"Da\", \"installation_id\":\"0123456789ab")]
    [InlineData("{\"Theme\":\"Da\", \"installation_id\":\"NOT VALID!\", \"x\":")]
    [InlineData("{\"Theme\":\"Da\", \"installation_id\":")]
    public void A_mention_with_no_recoverable_value_never_mints_and_keeps_the_file_aside(string content)
    {
        using var paths = new TempPaths();
        File.WriteAllText(paths.SettingsPath, content);
        var store = new SettingsStore(paths.SettingsPath);

        Should.Throw<InstallationIdUnusableException>(() => InstallationId.GetOrCreate(store));
        Should.Throw<InstallationIdUnusableException>(() => InstallationId.GetOrCreate(store));

        File.Exists(IdPath(paths)).ShouldBeFalse();
        var kept = Directory.GetFiles(paths.Directory, "settings.json.unreadable-*");
        kept.Length.ShouldBe(1, "kept aside once, not once per read");
        File.ReadAllText(kept[0]).ShouldBe(content);
    }

    [Fact]
    public void A_corrupt_file_that_never_mentions_an_id_still_mints_one()
    {
        using var paths = new TempPaths();
        File.WriteAllText(paths.SettingsPath, """{"Theme":"Da""");
        var store = new SettingsStore(paths.SettingsPath);

        var id = InstallationId.GetOrCreate(store);

        File.ReadAllText(IdPath(paths)).Trim().ShouldBe(id);
    }

    // ---- 3. the id file is written atomically ----

    [Fact]
    public void The_id_is_published_by_a_move_so_no_reader_can_see_a_torn_prefix()
    {
        using var paths = new TempPaths();
        var observed = new List<(bool IdExists, int Temps)>();
        var store = new SettingsStore(paths.SettingsPath)
        {
            FaultHook = op =>
            {
                if (op == "id-before-move")
                {
                    observed.Add((File.Exists(IdPath(paths)), Directory.GetFiles(paths.Directory, "installation_id.tmp-*").Length));
                }
            },
        };

        InstallationId.GetOrCreate(store);

        observed.ShouldBe([(false, 1)]);
        Directory.GetFiles(paths.Directory, "installation_id.tmp-*").ShouldBeEmpty();
    }

    [Fact]
    public void A_loser_keeps_the_winners_file_and_leaves_no_temp_file()
    {
        using var paths = new TempPaths();
        var store = new SettingsStore(paths.SettingsPath);

        store.WriteIdFileIfAbsent("winner-id");
        store.WriteIdFileIfAbsent("loser-id");

        File.ReadAllText(IdPath(paths)).ShouldBe("winner-id");
        Directory.GetFiles(paths.Directory, "installation_id.tmp-*").ShouldBeEmpty();
    }

    // ---- 4. a whitespace-only or BOM-only id file counts as absent and is replaced ----

    [Theory]
    [InlineData("\n", false)]
    [InlineData("  \r\n ", false)]
    [InlineData("", true)]
    [InlineData("\n", true)]
    public void A_whitespace_or_bom_only_id_file_is_replaced_and_every_later_run_reads_the_new_id(string content, bool withBom)
    {
        using var paths = new TempPaths();
        var bytes = (withBom ? new byte[] { 0xEF, 0xBB, 0xBF } : []).Concat(Encoding.UTF8.GetBytes(content)).ToArray();
        bytes.Length.ShouldBeGreaterThan(0, "vacuity guard: the file must not be empty");
        File.WriteAllBytes(IdPath(paths), bytes);
        var store = new SettingsStore(paths.SettingsPath);

        var id = InstallationId.GetOrCreate(store);

        File.ReadAllText(IdPath(paths)).Trim().ShouldBe(id);
        InstallationId.GetOrCreate(new SettingsStore(paths.SettingsPath)).ShouldBe(id);
        Directory.GetFiles(paths.Directory, "installation_id.invalid-*").ShouldBeEmpty();
        Directory.GetFiles(paths.Directory, "installation_id.tmp-*").ShouldBeEmpty();
    }

    [Fact]
    public void A_whitespace_id_file_does_not_stop_a_legacy_id_in_settings_json_being_migrated()
    {
        using var paths = new TempPaths();
        File.WriteAllText(IdPath(paths), "\n");
        File.WriteAllText(paths.SettingsPath, """{"installation_id":"legacy-id"}""");

        InstallationId.GetOrCreate(new SettingsStore(paths.SettingsPath)).ShouldBe("legacy-id");
    }

    [Fact]
    public void A_real_id_file_is_never_overwritten_by_the_whitespace_replacement()
    {
        using var paths = new TempPaths();
        File.WriteAllText(IdPath(paths), "keepme-123");
        var store = new SettingsStore(paths.SettingsPath);

        store.WriteIdFileIfAbsent("intruder-id");

        File.ReadAllText(IdPath(paths)).ShouldBe("keepme-123");
    }

    // ---- 5. the wait is bounded ----

    [Fact]
    public void A_held_mutex_makes_a_write_give_up_within_the_bound_and_change_nothing()
    {
        using var paths = new TempPaths();
        var store = new SettingsStore(paths.SettingsPath);
        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var holder = new Thread(() =>
        {
            using var mutex = new Mutex(false, store.MutexName);
            mutex.WaitOne();
            held.Set();
            release.Wait(TestContext.Current.CancellationToken);
            mutex.ReleaseMutex();
        });
        holder.Start();
        held.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken).ShouldBeTrue();

        try
        {
            var clock = Stopwatch.StartNew();
            Should.Throw<SettingsUnavailableException>(() => store.SetString("Theme", "Dark"));
            Should.Throw<SettingsUnavailableException>(() => InstallationId.GetOrCreate(store));
            clock.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(2), "two calls, each bounded at about 300 ms");
        }
        finally
        {
            release.Set();
            holder.Join();
        }

        File.Exists(paths.SettingsPath).ShouldBeFalse();
        File.Exists(IdPath(paths)).ShouldBeFalse();
    }

    [Fact]
    public void A_locked_file_gives_up_within_the_budget_of_one_call()
    {
        using var paths = new TempPaths();
        File.WriteAllText(paths.SettingsPath, """{"Theme":"Dark"}""");
        var store = new SettingsStore(paths.SettingsPath) { WaitBudget = TimeSpan.FromMilliseconds(100) };

        using var locked = new FileStream(paths.SettingsPath, FileMode.Open, FileAccess.Read, FileShare.None);
        var clock = Stopwatch.StartNew();
        Should.Throw<SettingsUnavailableException>(() => store.SetString("Theme", "Light"));

        clock.Elapsed.ShouldBeLessThan(TimeSpan.FromMilliseconds(1000));
    }
}
