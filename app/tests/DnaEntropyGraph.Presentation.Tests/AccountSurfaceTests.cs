using CommunityToolkit.Mvvm.Messaging;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Cloud;
using DnaEntropyGraph.Presentation.Services;
using DnaEntropyGraph.Presentation.ViewModels;
using NSubstitute;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Presentation.Tests;

/// <summary>Issue #48 on the Presentation side: the pill follows the signed-in account, and a failed sign-in shows its named message and action instead of crashing.</summary>
public class AccountSurfaceTests
{
    private static IStringResourceProvider Strings()
    {
        var strings = Substitute.For<IStringResourceProvider>();
        strings.GetString(Arg.Any<string>()).Returns(call => call.Arg<string>());
        strings.GetString("StatusPillSignedIn").Returns("Signed in - {0}");
        return strings;
    }

    [Fact]
    public void The_pill_shows_the_account_email_and_follows_AccountChanged()
    {
        var account = Substitute.For<IGcpAccount>();
        var viewModel = new ShellViewModel(Substitute.For<INavigator>(), account, new WeakReferenceMessenger(), Strings());
        viewModel.StatusPillText.ShouldBe("StatusPillNotSignedIn");

        account.IsSignedIn.Returns(true);
        account.CurrentAccount.Returns(new AccountInfo("1001", "first@example.test"));
        account.AccountChanged += Raise.Event();

        viewModel.StatusPillText.ShouldBe("Signed in - first@example.test");

        account.IsSignedIn.Returns(false);
        account.CurrentAccount.Returns((AccountInfo?)null);
        account.AccountChanged += Raise.Event();

        viewModel.StatusPillText.ShouldBe("StatusPillNotSignedIn");
    }

    [Fact]
    public void The_pill_update_goes_through_the_dispatcher_when_there_is_one()
    {
        var account = Substitute.For<IGcpAccount>();
        var dispatcher = Substitute.For<IDispatcher>();
        Action? queued = null;
        dispatcher.When(d => d.Enqueue(Arg.Any<Action>())).Do(call => queued = call.Arg<Action>());
        var viewModel = new ShellViewModel(Substitute.For<INavigator>(), account, new WeakReferenceMessenger(), Strings(), dispatcher);

        account.IsSignedIn.Returns(true);
        account.CurrentAccount.Returns(new AccountInfo("1001", "first@example.test"));
        account.AccountChanged += Raise.Event();

        viewModel.StatusPillText.ShouldBe("StatusPillNotSignedIn", "the property must not change off the UI thread");
        queued.ShouldNotBeNull();
        queued!();
        viewModel.StatusPillText.ShouldBe("Signed in - first@example.test");
    }

    [Theory]
    [InlineData(AuthErrorCodes.OAuthClientMissing, "AuthError_OAUTH_CLIENT_MISSING", "")]
    [InlineData(AuthErrorCodes.SigninExpired, "AuthError_SIGNIN_EXPIRED", "AuthAction_SignInAgain")]
    [InlineData(AuthErrorCodes.NetworkUnavailable, "AuthError_SIGNIN_NETWORK", "AuthAction_TryAgain")]
    public async Task A_failed_sign_in_shows_its_message_and_action_and_does_not_navigate(string code, string messageKey, string actionKey)
    {
        var account = Substitute.For<IGcpAccount>();
        account.SignInAsync(Arg.Any<CancellationToken>()).Returns(Task.FromException(new AccountAuthException(code)));
        var navigator = Substitute.For<INavigator>();
        var viewModel = new WizardViewModel(account, Substitute.For<IDialogService>(), navigator, Strings());

        await viewModel.SignInCommand.ExecuteAsync(null);

        viewModel.SignInErrorText.ShouldBe(messageKey);
        viewModel.SignInActionText.ShouldBe(actionKey);
        viewModel.IsSignedIn.ShouldBeFalse();
        navigator.DidNotReceive().NavigateTo(Arg.Any<string>());
    }

    [Fact]
    public async Task A_good_sign_in_after_a_failed_one_clears_the_message_and_moves_on()
    {
        var account = Substitute.For<IGcpAccount>();
        account.SignInAsync(Arg.Any<CancellationToken>()).Returns(Task.FromException(new AccountAuthException(AuthErrorCodes.SigninCancelled)), Task.CompletedTask);
        var navigator = Substitute.For<INavigator>();
        var viewModel = new WizardViewModel(account, Substitute.For<IDialogService>(), navigator, Strings());
        await viewModel.SignInCommand.ExecuteAsync(null);
        viewModel.SignInErrorText.ShouldNotBeEmpty();

        account.IsSignedIn.Returns(true);
        await viewModel.SignInCommand.ExecuteAsync(null);

        viewModel.SignInErrorText.ShouldBeEmpty();
        viewModel.SignInActionText.ShouldBeEmpty();
        navigator.Received(1).NavigateTo("Wizard/Project");
    }
}
