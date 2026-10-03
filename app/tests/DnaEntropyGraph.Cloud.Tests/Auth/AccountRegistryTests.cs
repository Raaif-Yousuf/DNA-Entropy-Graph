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
        Directory.GetFiles(_dir).Select(Path.GetFileName).ToArray().ShouldBe(["accounts.json"], "no temp file is left behind");
    }
}
