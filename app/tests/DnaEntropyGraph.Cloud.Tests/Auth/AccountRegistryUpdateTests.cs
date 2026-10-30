using DnaEntropyGraph.Cloud.Auth;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Cloud.Tests.Auth;

/// <summary>Issue #620: <see cref="AccountRegistry.Update"/> applies a change to the file as it is under the lock, so no writer's change is lost.</summary>
public class AccountRegistryUpdateTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "deg-regupd-" + Guid.NewGuid().ToString("n"));

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

    private static AccountsFile Add(AccountsFile file, string sub)
        => file with { Accounts = [.. file.Accounts, new AccountRecord(sub, sub + "@example.test", NeedsSignIn: false)] };

    [Fact]
    public void An_update_sees_what_another_registry_wrote_after_this_one_last_loaded()
    {
        var first = new AccountRegistry(_dir);
        var second = new AccountRegistry(_dir);
        first.Load().ShouldBe(AccountsFile.Empty);
        second.Update(f => Add(f, "2002"));

        var written = first.Update(f => Add(f, "1001"));

        written.Accounts.Select(a => a.Sub).ShouldBe(["2002", "1001"]);
        new AccountRegistry(_dir).Load().Accounts.Select(a => a.Sub).ShouldBe(["2002", "1001"]);
    }

    [Fact]
    public void Concurrent_updates_from_two_registries_each_keep_every_account()
    {
        var a = new AccountRegistry(_dir);
        var b = new AccountRegistry(_dir);
        using var start = new ManualResetEventSlim();
        Thread Writer(AccountRegistry registry, string prefix) => new(() =>
        {
            start.Wait();
            for (var i = 0; i < 15; i++)
            {
                registry.Update(f => Add(f, prefix + i));
            }
        });
        var threads = new[] { Writer(a, "a"), Writer(b, "b") };
        foreach (var t in threads)
        {
            t.Start();
        }

        start.Set();
        foreach (var t in threads)
        {
            t.Join();
        }

        new AccountRegistry(_dir).Load().Accounts.Count.ShouldBe(30, "no update replaced another's");
    }

    [Fact]
    public void An_update_while_the_file_is_held_open_refuses_and_leaves_the_file_alone()
    {
        var registry = new AccountRegistry(_dir);
        registry.Update(f => Add(f, "1001"));
        var before = File.ReadAllBytes(FilePath);

        using (new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Should.Throw<AccountsFileLockedException>(() => registry.Update(f => Add(f, "2002")));
        }

        File.ReadAllBytes(FilePath).ShouldBe(before);
        registry.Update(f => Add(f, "2002")).Accounts.Count.ShouldBe(2, "the lock is gone: the update reads the real file");
    }

    [Fact]
    public void An_update_that_finds_a_damaged_file_sets_it_aside_and_says_where_before_the_change_runs()
    {
        Directory.CreateDirectory(_dir);
        var damaged = System.Text.Encoding.UTF8.GetBytes("{\"activeSub\":");
        File.WriteAllBytes(FilePath, damaged);
        var registry = new AccountRegistry(_dir);
        string? told = null;
        string? toldBeforeChange = null;

        registry.Update(f => { toldBeforeChange = told; return Add(f, "1001"); }, where => told = where);

        told.ShouldBe(FilePath + ".bad");
        toldBeforeChange.ShouldBe(told);
        File.ReadAllBytes(FilePath + ".bad").ShouldBe(damaged);
        new AccountRegistry(_dir).Load().Accounts.Count.ShouldBe(1);
        registry.Update(f => f, where => throw new InvalidOperationException("nothing was set aside this time"));
    }

    [Fact]
    public void LoadWithOutcome_hands_back_the_whole_answer_without_touching_the_properties()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, "{\"activeSub\":null,\"accounts\":[]}");
        var registry = new AccountRegistry(_dir);

        using (new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            registry.LoadWithOutcome().Unreadable.ShouldBeTrue();
        }

        registry.Unreadable.ShouldBeFalse("only Load publishes the properties");
        registry.LoadWithOutcome().Unreadable.ShouldBeFalse();
    }

    [Fact]
    public void A_change_that_throws_writes_nothing()
    {
        var registry = new AccountRegistry(_dir);
        registry.Update(f => Add(f, "1001"));
        var before = File.ReadAllBytes(FilePath);

        Should.Throw<InvalidOperationException>(() => registry.Update(_ => throw new InvalidOperationException("no")));

        File.ReadAllBytes(FilePath).ShouldBe(before);
        Directory.GetFiles(_dir).Select(Path.GetFileName).ToArray().ShouldBe(["accounts.json"]);
    }
}
