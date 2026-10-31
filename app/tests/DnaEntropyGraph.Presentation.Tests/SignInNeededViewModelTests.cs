using CommunityToolkit.Mvvm.Messaging;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Cloud;
using DnaEntropyGraph.Presentation.Messaging;
using DnaEntropyGraph.Presentation.Services;
using DnaEntropyGraph.Presentation.ViewModels;
using NSubstitute;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Presentation.Tests;

/// <summary>
/// Issue #830 review M3: when the startup reconcile cannot look at a run because its Google account is signed out, the shell says so with a
/// "Sign in" button naming the account. The reconciler's outcome alone reached nobody.
/// </summary>
public sealed class SignInNeededViewModelTests
{
    private sealed class ImmediateDispatcher : IDispatcher
    {
        public void Enqueue(Action action) => action();
    }

    private sealed class Rig
    {
        public Rig(params AccountInfo[] accounts)
        {
            Accounts = [.. accounts];
            var strings = Substitute.For<IStringResourceProvider>();
            strings.GetString(Arg.Any<string>()).Returns(call => call.Arg<string>());
            strings.GetString("SignInNeeded_Title").Returns("Sign in as {0}");
            strings.GetString("SignInNeeded_Joiner").Returns(" or ");
            Account.Accounts.Returns(_ => Accounts);
            ViewModel = new SignInNeededViewModel(Account, Messenger, strings, new ImmediateDispatcher(), Log);
        }

        public List<AccountInfo> Accounts { get; }

        public List<string> LogLines { get; } = [];

        private IDiagnosticsLog Log => new Recorder(LogLines);

        public IGcpAccount Account { get; } = Substitute.For<IGcpAccount>();

        public WeakReferenceMessenger Messenger { get; } = new();

        public SignInNeededViewModel ViewModel { get; }

        public void Need(string? sub) => Messenger.Send(new AccountSignInNeededMessage(sub));
    }

    private sealed class Recorder(List<string> lines) : IDiagnosticsLog
    {
        public void Warning(string source, string? jobId, string errorClass) => lines.Add($"{source}/{errorClass}");
    }

    private static AccountInfo SignedOut(string sub, string email) => new(sub, email, NeedsSignIn: true);

    [Fact]
    public void A_signed_out_account_opens_the_bar_naming_that_account()
    {
        var rig = new Rig(SignedOut("sub-a", "a@example.org"));

        rig.Need("sub-a");

        rig.ViewModel.IsOpen.ShouldBeTrue();
        rig.ViewModel.Title.ShouldBe("Sign in as a@example.org");
    }

    [Fact]
    public void An_account_the_list_no_longer_knows_gets_the_bar_without_an_email()
    {
        var rig = new Rig();

        rig.Need("sub-gone");

        rig.ViewModel.IsOpen.ShouldBeTrue();
        rig.ViewModel.Title.ShouldBe("SignInNeeded_TitleUnknown");
    }

    [Fact]
    public void Two_accounts_are_both_named_and_one_account_told_twice_is_named_once()
    {
        var rig = new Rig(SignedOut("sub-a", "a@example.org"), SignedOut("sub-b", "b@example.org"));

        rig.Need("sub-a");
        rig.Need("sub-b");
        rig.Need("sub-a");

        rig.ViewModel.Title.ShouldBe("Sign in as a@example.org or b@example.org");
    }

    [Fact]
    public async Task The_sign_in_button_closes_the_bar_only_when_the_named_account_is_signed_in_afterwards()
    {
        var rig = new Rig(SignedOut("sub-a", "a@example.org"));
        rig.Need("sub-a");

        // The person signs in as somebody else: sub-a is still signed out, so the bar stays.
        rig.Account.SignInAsync(Arg.Any<CancellationToken>()).Returns(_ => Task.CompletedTask);
        await rig.ViewModel.SignInCommand.ExecuteAsync(null);
        rig.ViewModel.IsOpen.ShouldBeTrue();

        // They sign in as sub-a.
        rig.Account.SignInAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            rig.Accounts[0] = new AccountInfo("sub-a", "a@example.org");
            return Task.CompletedTask;
        });
        await rig.ViewModel.SignInCommand.ExecuteAsync(null);

        rig.ViewModel.IsOpen.ShouldBeFalse();
    }

    [Fact]
    public async Task A_cancelled_sign_in_keeps_the_bar()
    {
        var rig = new Rig(SignedOut("sub-a", "a@example.org"));
        rig.Need("sub-a");
        rig.Account.SignInAsync(Arg.Any<CancellationToken>()).Returns(Task.FromException(new AccountAuthException(AuthErrorCodes.SigninCancelled)));

        await rig.ViewModel.SignInCommand.ExecuteAsync(null);

        rig.ViewModel.IsOpen.ShouldBeTrue();
    }

    [Fact]
    public void An_account_signed_in_elsewhere_clears_its_entry_when_the_account_list_changes()
    {
        var rig = new Rig(SignedOut("sub-a", "a@example.org"));
        rig.Need("sub-a");

        rig.Accounts[0] = new AccountInfo("sub-a", "a@example.org");
        rig.Account.AccountChanged += Raise.Event();

        rig.ViewModel.IsOpen.ShouldBeFalse();
    }

    [Fact]
    public void A_bar_the_person_closed_stays_closed_until_a_new_account_needs_a_sign_in()
    {
        var rig = new Rig(SignedOut("sub-a", "a@example.org"), SignedOut("sub-b", "b@example.org"));
        rig.Need("sub-a");
        rig.ViewModel.IsOpen = false;

        rig.Account.AccountChanged += Raise.Event();
        rig.ViewModel.IsOpen.ShouldBeFalse();

        rig.Need("sub-b");
        rig.ViewModel.IsOpen.ShouldBeTrue();
    }

    [Theory]
    [InlineData(typeof(HttpRequestException))]
    [InlineData(typeof(OperationCanceledException))]
    public async Task A_sign_in_that_fails_for_a_reason_that_is_not_a_sign_in_answer_does_not_escape_and_the_bar_says_it_failed(Type failure)
    {
        // Issue #830 review F7a.
        var rig = new Rig(SignedOut("sub-a", "a@example.org"));
        rig.Need("sub-a");
        rig.Account.SignInAsync(Arg.Any<CancellationToken>()).Returns(Task.FromException((Exception)Activator.CreateInstance(failure)!));

        await rig.ViewModel.SignInCommand.ExecuteAsync(null);

        rig.ViewModel.IsOpen.ShouldBeTrue("the bar stays so the person can try again");
        rig.ViewModel.Message.ShouldBe("AuthError_SIGNIN_FAILED");
        rig.LogLines.ShouldBe([$"sign-in-needed/{failure.Name}"]);
    }
}
