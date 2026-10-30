using DnaEntropyGraph.Cloud.Auth;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Cloud.Tests.Auth;

/// <summary>
/// Issue #624: an <c>accounts.json</c> held open at launch is reported as locked (not as signed out), and the service raises
/// <see cref="GoogleAccountService.AccountChanged"/> once the file can be read, so the account appears without a restart.
/// </summary>
public class AccountsFileLockedAtLaunchTests
{
    private static string AccountsPath(AuthHarness harness) => Path.Combine(harness.AuthDirectory, "accounts.json");

    [Fact]
    public async Task A_list_held_open_at_launch_is_locked_and_after_the_release_Try_again_shows_the_account_and_raises_AccountChanged()
    {
        using var harness = new AuthHarness();
        await harness.SignedInAsync("1001", "first@example.test");
        var restarted = harness.NewService();
        var changes = 0;
        restarted.AccountChanged += (_, _) => changes++;

        var hold = new FileStream(AccountsPath(harness), FileMode.Open, FileAccess.Read, FileShare.None);
        try
        {
            restarted.IsAccountsFileLocked.ShouldBeTrue();
            restarted.IsSignedIn.ShouldBeFalse("the list is unread, so there is nothing to show");
            restarted.RecheckAccountsFile();
            restarted.IsAccountsFileLocked.ShouldBeTrue("still held");
            changes.ShouldBe(0, "nothing changed while it is held");
        }
        finally
        {
            hold.Dispose();
        }

        restarted.RecheckAccountsFile();

        restarted.IsAccountsFileLocked.ShouldBeFalse();
        restarted.IsSignedIn.ShouldBeTrue();
        restarted.CurrentAccount!.Email.ShouldBe("first@example.test");
        changes.ShouldBe(1);
    }

    [Fact]
    public async Task Try_again_does_not_wait_for_the_two_second_window()
    {
        using var harness = new AuthHarness();
        await harness.SignedInAsync("1001", "first@example.test");
        harness.Clock = new ManualClock();
        var restarted = harness.NewService();

        using (new FileStream(AccountsPath(harness), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            restarted.IsAccountsFileLocked.ShouldBeTrue();
        }

        restarted.RecheckAccountsFile();

        restarted.IsSignedIn.ShouldBeTrue("the clock never moved, so only an explicit re-read could have found it");
    }

    [Fact]
    public async Task An_ordinary_read_that_finds_the_hold_gone_after_the_window_raises_AccountChanged_once()
    {
        using var harness = new AuthHarness();
        await harness.SignedInAsync("1001", "first@example.test");
        var clock = new ManualClock();
        harness.Clock = clock;
        var restarted = harness.NewService();
        var changes = 0;
        restarted.AccountChanged += (_, _) => changes++;

        using (new FileStream(AccountsPath(harness), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            restarted.IsAccountsFileLocked.ShouldBeTrue();
        }

        clock.Advance(TimeSpan.FromSeconds(3));
        restarted.IsSignedIn.ShouldBeTrue();
        _ = restarted.CurrentAccount;

        changes.ShouldBe(1);
    }

    [Fact]
    public async Task A_readable_list_is_not_locked_and_Try_again_changes_nothing()
    {
        using var harness = new AuthHarness();
        await harness.SignedInAsync("1001", "first@example.test");
        var restarted = harness.NewService();
        var changes = 0;
        restarted.AccountChanged += (_, _) => changes++;

        restarted.IsAccountsFileLocked.ShouldBeFalse();
        restarted.RecheckAccountsFile();

        restarted.IsSignedIn.ShouldBeTrue();
        changes.ShouldBe(0);
    }

    [Fact]
    public void A_missing_list_is_not_locked()
    {
        using var harness = new AuthHarness();

        harness.NewService().IsAccountsFileLocked.ShouldBeFalse();
    }
}
