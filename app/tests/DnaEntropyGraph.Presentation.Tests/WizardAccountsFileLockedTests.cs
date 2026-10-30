using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Presentation.Services;
using DnaEntropyGraph.Presentation.ViewModels;
using NSubstitute;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Presentation.Tests;

/// <summary>
/// Issue #624: an account list held open at launch is not a plain signed-out screen. The sign-in card says so (ACCOUNTS_FILE_LOCKED) and offers
/// Try again, which re-reads the list instead of opening the browser; the account appears once the list can be read.
/// </summary>
public class WizardAccountsFileLockedTests
{
    private static IStringResourceProvider Strings()
    {
        var strings = Substitute.For<IStringResourceProvider>();
        strings.GetString(Arg.Any<string>()).Returns(call => call.Arg<string>());
        return strings;
    }

    private static WizardViewModel Wizard(IGcpAccount account)
        => new(account, Substitute.For<IDialogService>(), Substitute.For<INavigator>(), Strings());

    [Fact]
    public void A_locked_list_at_launch_shows_the_locked_message_and_a_Try_again_that_can_run_not_Sign_in()
    {
        var account = Substitute.For<IGcpAccount>();
        account.IsAccountsFileLocked.Returns(true);

        var wizard = Wizard(account);

        wizard.IsAccountsFileLocked.ShouldBeTrue();
        wizard.AccountsFileLockedText.ShouldBe("AuthError_ACCOUNTS_FILE_LOCKED");
        wizard.AccountsFileRetryText.ShouldBe("AuthAction_TryAgain");
        wizard.RetryAccountsFileCommand.CanExecute(null).ShouldBeTrue();
        wizard.ShowSignInButton.ShouldBeFalse("signing in over a list that cannot be read is the wrong action");
        wizard.IsSignedIn.ShouldBeFalse();
    }

    [Fact]
    public void A_readable_list_shows_no_locked_message_and_the_Sign_in_button()
    {
        var wizard = Wizard(Substitute.For<IGcpAccount>());

        wizard.IsAccountsFileLocked.ShouldBeFalse();
        wizard.AccountsFileLockedText.ShouldBeEmpty();
        wizard.AccountsFileRetryText.ShouldBeEmpty();
        wizard.ShowSignInButton.ShouldBeTrue();
    }

    [Fact]
    public void After_the_hold_is_released_Try_again_rereads_the_list_and_the_account_appears()
    {
        var account = Substitute.For<IGcpAccount>();
        account.IsAccountsFileLocked.Returns(true);
        var wizard = Wizard(account);
        account.When(a => a.RecheckAccountsFile()).Do(_ =>
        {
            account.IsAccountsFileLocked.Returns(false);
            account.IsSignedIn.Returns(true);
            account.AccountChanged += Raise.Event();
        });

        wizard.RetryAccountsFileCommand.Execute(null);

        account.Received(1).RecheckAccountsFile();
        wizard.IsAccountsFileLocked.ShouldBeFalse();
        wizard.AccountsFileLockedText.ShouldBeEmpty();
        wizard.IsSignedIn.ShouldBeTrue("the account the list holds is now shown");
        account.DidNotReceive().SignInAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public void While_the_hold_remains_Try_again_keeps_the_message()
    {
        var account = Substitute.For<IGcpAccount>();
        account.IsAccountsFileLocked.Returns(true);
        var wizard = Wizard(account);

        wizard.RetryAccountsFileCommand.Execute(null);

        account.Received(1).RecheckAccountsFile();
        wizard.IsAccountsFileLocked.ShouldBeTrue();
        wizard.AccountsFileLockedText.ShouldBe("AuthError_ACCOUNTS_FILE_LOCKED");
    }

    [Fact]
    public void The_service_raising_AccountChanged_on_its_own_clears_the_message_too()
    {
        var account = Substitute.For<IGcpAccount>();
        account.IsAccountsFileLocked.Returns(true);
        var wizard = Wizard(account);

        account.IsAccountsFileLocked.Returns(false);
        account.IsSignedIn.Returns(true);
        account.AccountChanged += Raise.Event();

        wizard.IsAccountsFileLocked.ShouldBeFalse();
        wizard.IsSignedIn.ShouldBeTrue();
    }
}
