using DnaEntropyGraph.App.Startup;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Presentation.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Guards.Tests;

/// <summary>
/// Issue #624's "wired to nothing" check against the real container: with <c>accounts.json</c> held open (FileShare.None) while the app starts, the wizard's
/// sign-in card shows the locked message and a Try again command that can run; after the hold is released, running it shows the account and raises AccountChanged.
/// </summary>
public class AccountsFileLockedWiringTests
{
    [Fact]
    public async Task A_list_held_open_at_startup_shows_the_message_and_Try_again_after_the_release_shows_the_account()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "the seeded token file is DPAPI-protected");
        var root = Path.Combine(Path.GetTempPath(), $"deg-guard-locked-{Guid.NewGuid():n}");
        var auth = Path.Combine(root, "auth");
        Directory.CreateDirectory(auth);
        try
        {
            var protector = new DnaEntropyGraph.Cloud.Auth.DpapiSecretProtector();
            File.WriteAllBytes(Path.Combine(auth, "1001.tok"), protector.Protect(System.Text.Encoding.UTF8.GetBytes("{\"access_token\":\"a\",\"refresh_token\":\"r\"}")));
            var accounts = Path.Combine(auth, "accounts.json");
            File.WriteAllText(accounts, "{\"activeSub\":\"1001\",\"accounts\":[{\"sub\":\"1001\",\"email\":\"first@example.test\",\"needsSignIn\":false}]}");

            var services = new ServiceCollection();
            services.AddDnaEntropyGraph(appDataRoot: root, cloud: CloudBackend.Fake);
            var hold = new FileStream(accounts, FileMode.Open, FileAccess.Read, FileShare.None);
            await using var provider = services.BuildServiceProvider();
            try
            {
                var account = provider.GetRequiredService<IGcpAccount>();
                var changes = 0;
                account.AccountChanged += (_, _) => Interlocked.Increment(ref changes);
                var wizard = provider.GetRequiredService<WizardViewModel>();

                wizard.IsAccountsFileLocked.ShouldBeTrue();
                wizard.AccountsFileLockedText.ShouldNotBeNullOrWhiteSpace();
                wizard.AccountsFileLockedText.ShouldNotBe("AuthError_ACCOUNTS_FILE_LOCKED", "the real string table answers, not the key");
                wizard.AccountsFileRetryText.ShouldNotBeNullOrWhiteSpace();
                wizard.RetryAccountsFileCommand.CanExecute(null).ShouldBeTrue();
                wizard.IsSignedIn.ShouldBeFalse();

                hold.Dispose();
                wizard.RetryAccountsFileCommand.Execute(null);

                Volatile.Read(ref changes).ShouldBeGreaterThan(0);
                wizard.IsAccountsFileLocked.ShouldBeFalse();
                wizard.IsSignedIn.ShouldBeTrue();
                account.CurrentAccount!.Email.ShouldBe("first@example.test");
            }
            finally
            {
                hold.Dispose();
            }
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
