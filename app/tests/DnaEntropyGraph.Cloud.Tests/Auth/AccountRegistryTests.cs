using System.Text;
using System.Text.Json;
using DnaEntropyGraph.Cloud.Auth;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Cloud.Tests.Auth;

/// <summary>Issue #616: <c>accounts.json</c> survives two writers and is never overwritten when it cannot be parsed.</summary>
public class AccountRegistryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "deg-reg-" + Guid.NewGuid().ToString("n"));

    private string FilePath => Path.Combine(_dir, "accounts.json");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static AccountsFile One(string sub) => new(sub, [new AccountRecord(sub, sub + "@example.test", NeedsSignIn: false)]);

    [Fact]
    public void A_missing_file_is_an_empty_list_and_is_not_set_aside()
    {
        var registry = new AccountRegistry(_dir);

        registry.Load().ShouldBe(AccountsFile.Empty);

        registry.QuarantinedTo.ShouldBeNull();
        Directory.Exists(_dir).ShouldBeFalse("a missing file creates nothing");
    }

    [Fact]
    public void A_file_that_does_not_parse_is_set_aside_byte_for_byte_and_the_next_save_does_not_lose_it()
    {
        Directory.CreateDirectory(_dir);
        var original = Encoding.UTF8.GetBytes("{\"activeSub\":\"1001\",\"accounts\":[{\"sub\":\"1001\",\"em");
        File.WriteAllBytes(FilePath, original);
        var registry = new AccountRegistry(_dir);

        registry.Load().ShouldBe(AccountsFile.Empty);
        registry.QuarantinedTo.ShouldBe(FilePath + ".bad");
        registry.Save(One("2002"));

        File.ReadAllBytes(FilePath + ".bad").ShouldBe(original);
        registry.Load().ActiveSub.ShouldBe("2002");
    }

    [Fact]
    public void A_second_bad_file_never_clobbers_an_earlier_one()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath + ".bad", "first");
        File.WriteAllText(FilePath, "second");
        var registry = new AccountRegistry(_dir);

        registry.Load();

        File.ReadAllText(FilePath + ".bad").ShouldBe("first");
        registry.QuarantinedTo.ShouldNotBeNull().ShouldNotBe(FilePath + ".bad");
        File.ReadAllText(registry.QuarantinedTo!).ShouldBe("second");
    }

    [Fact]
    public void A_zero_byte_file_is_set_aside_too()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllBytes(FilePath, []);
        var registry = new AccountRegistry(_dir);

        registry.Load().ShouldBe(AccountsFile.Empty);

        registry.QuarantinedTo.ShouldNotBeNull();
    }

    [Fact]
    public void A_file_that_exists_but_cannot_be_read_is_not_empty_not_set_aside_and_not_overwritten()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, "{\"activeSub\":\"1001\",\"accounts\":[{\"sub\":\"1001\",\"email\":\"a@example.test\",\"needsSignIn\":false}]}");
        var original = File.ReadAllBytes(FilePath);
        var registry = new AccountRegistry(_dir);

        using (new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            registry.Load().ShouldBe(AccountsFile.Empty);
            registry.Unreadable.ShouldBeTrue();
            registry.QuarantinedTo.ShouldBeNull("a locked file is not a damaged one");
            Should.Throw<TokenStorageException>(() => registry.Save(One("2002")));
        }

        Should.Throw<TokenStorageException>(() => registry.Save(One("2002")), "the lock is gone but the file was never read, so Save still refuses");
        File.ReadAllBytes(FilePath).ShouldBe(original);
        File.Exists(FilePath + ".bad").ShouldBeFalse();
        registry.Load().ActiveSub.ShouldBe("1001", "after the lock clears the real file is read");
        registry.Unreadable.ShouldBeFalse();
        registry.Save(One("2002"));
    }

    [Fact]
    public void A_mutex_the_system_refuses_to_open_is_a_locked_file_not_a_crash()
    {
        // THEORY (unverified): an elevated and a normal copy of the app cannot open each other's Local\ mutex; docs/ToTest.md has the real two-instance row.
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, "{\"activeSub\":null,\"accounts\":[]}");
        var registry = new AccountRegistry(_dir, _ => throw new UnauthorizedAccessException());

        registry.Load().ShouldBe(AccountsFile.Empty);

        registry.Unreadable.ShouldBeTrue();
        Should.Throw<AccountsFileLockedException>(() => registry.Save(One("2002")));
    }

    [Fact]
    public void A_mutex_that_cannot_be_opened_at_all_is_also_a_locked_file_not_a_crash()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, "{\"activeSub\":null,\"accounts\":[]}");
        var registry = new AccountRegistry(_dir, _ => throw new WaitHandleCannotBeOpenedException());

        registry.Load().ShouldBe(AccountsFile.Empty);

        registry.Unreadable.ShouldBeTrue();
        Should.Throw<AccountsFileLockedException>(() => registry.Save(One("2002")));
    }

    [Fact]
    public void A_save_that_cannot_take_the_lock_in_time_is_a_lock_failure_and_writes_nothing()
    {
        var registry = new AccountRegistry(_dir, name => new Mutex(false, name), saveLockWait: TimeSpan.FromMilliseconds(200));
        registry.Save(One("1001"));
        var before = File.ReadAllBytes(FilePath);

        using (new MutexHolder(_dir))
        {
            Should.Throw<AccountsFileLockedException>(() => registry.Save(One("2002")));
        }

        File.ReadAllBytes(FilePath).ShouldBe(before);
        registry.Save(One("2002"));
    }

    [Fact]
    public void A_disk_failure_in_a_save_is_a_storage_failure_not_a_lock_failure()
    {
        Directory.CreateDirectory(FilePath);
        var registry = new AccountRegistry(_dir);

        var failure = Should.Throw<TokenStorageException>(() => registry.Save(One("1001")));

        failure.GetType().ShouldBe(typeof(TokenStorageException));
    }

    [Fact]
    public void A_damaged_file_that_cannot_be_moved_aside_is_flagged_as_a_folder_problem_and_left_alone()
    {
        Directory.CreateDirectory(_dir);
        var original = Encoding.UTF8.GetBytes("{\"activeSub\":");
        File.WriteAllBytes(FilePath, original);
        var registry = new AccountRegistry(_dir);

        using (new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            registry.Load().ShouldBe(AccountsFile.Empty);
        }

        registry.QuarantineFailed.ShouldBeTrue();
        registry.Unreadable.ShouldBeTrue("Save refuses");
        registry.QuarantinedTo.ShouldBeNull();
        File.ReadAllBytes(FilePath).ShouldBe(original);
        Should.Throw<TokenStorageException>(() => registry.Save(One("2002")));
    }

    [Fact]
    public void The_worst_case_a_load_can_block_the_caller_is_a_few_hundred_milliseconds()
    {
        // Load runs from the State getter, which can be on the UI thread: one bounded mutex wait plus the read retries.
        AccountRegistry.WorstCaseLoadBudget.ShouldBeLessThanOrEqualTo(TimeSpan.FromMilliseconds(300));
        AccountRegistry.WorstCaseLoadBudget.ShouldBeGreaterThan(TimeSpan.Zero);
    }

    [Fact]
    public void A_load_while_another_copy_holds_the_lock_gives_up_and_leaves_the_file_alone()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, "{\"activeSub\":null,\"accounts\":[]}");
        var registry = new AccountRegistry(_dir);

        using (new MutexHolder(_dir))
        {
            registry.Load().ShouldBe(AccountsFile.Empty);
        }

        registry.Unreadable.ShouldBeTrue();
        registry.QuarantineFailed.ShouldBeFalse("a held lock is not a folder problem");
    }

    [Fact]
    public void A_save_removes_old_orphan_temp_files_and_keeps_fresh_ones_and_everything_else()
    {
        Directory.CreateDirectory(_dir);
        var old = Path.Combine(_dir, "accounts.json.aaaa.tmp");
        var fresh = Path.Combine(_dir, "accounts.json.bbbb.tmp");
        var bad = Path.Combine(_dir, "accounts.json.bad");
        var other = Path.Combine(_dir, "other.tmp");
        foreach (var f in new[] { old, fresh, bad, other })
        {
            File.WriteAllText(f, "x");
        }

        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddMinutes(-30));

        new AccountRegistry(_dir).Save(One("1001"));

        File.Exists(old).ShouldBeFalse();
        File.Exists(fresh).ShouldBeTrue("a save in another process may be using it");
        File.Exists(bad).ShouldBeTrue();
        File.Exists(other).ShouldBeTrue();
    }

    [Fact]
    public void A_read_that_fails_once_and_then_clears_is_retried_and_succeeds()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, "{\"activeSub\":\"1001\",\"accounts\":[]}");
        var registry = new AccountRegistry(_dir);
        var hold = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.None);
        var release = new Thread(() =>
        {
            Thread.Sleep(120);
            hold.Dispose();
        });
        release.Start();

        var loaded = registry.Load();
        release.Join();

        loaded.ActiveSub.ShouldBe("1001");
        registry.Unreadable.ShouldBeFalse();
    }

    [Fact]
    public void Two_registries_saving_at_once_never_corrupt_the_file_or_throw()
    {
        // Two instances on one path in two threads share nothing but the named, machine-wide mutex, so this exercises the same lock a second process would take.
        var failures = new List<Exception>();
        var a = new AccountRegistry(_dir);
        var b = new AccountRegistry(_dir);
        using var start = new ManualResetEventSlim();

        Thread Writer(AccountRegistry registry, string sub) => new(() =>
        {
            start.Wait();
            for (var i = 0; i < 150; i++)
            {
                try
                {
                    registry.Save(One(sub));
                }
                catch (Exception ex)
                {
                    lock (failures)
                    {
                        failures.Add(ex);
                    }
                }
            }
        });

        var threads = new[] { Writer(a, "1001"), Writer(b, "2002") };
        foreach (var t in threads)
        {
            t.Start();
        }

        start.Set();
        foreach (var t in threads)
        {
            t.Join();
        }

        failures.ShouldBeEmpty();
        var final = JsonSerializer.Deserialize<AccountsFile>(File.ReadAllText(FilePath));
        final.ShouldNotBeNull().Accounts.Count.ShouldBe(1);
        new[] { "1001", "2002" }.ShouldContain(final.ActiveSub!);
        Directory.GetFiles(_dir).Select(Path.GetFileName).ToArray().ShouldBe(["accounts.json"], "no temp file is left behind");    }
}
