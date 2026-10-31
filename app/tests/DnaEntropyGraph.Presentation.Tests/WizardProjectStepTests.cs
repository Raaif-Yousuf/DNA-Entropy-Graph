using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Cloud;
using DnaEntropyGraph.Presentation.Services;
using DnaEntropyGraph.Presentation.ViewModels;
using NSubstitute;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Presentation.Tests;

/// <summary>Issue #520: choosing a project in the wizard stores it on the account, and a refusal shows its named message and action.</summary>
public class WizardProjectStepTests
{
    private static IStringResourceProvider Strings()
    {
        var strings = Substitute.For<IStringResourceProvider>();
        strings.GetString(Arg.Any<string>()).Returns(call => call.Arg<string>());
        return strings;
    }

    // NSubstitute answers "" for an unset string property; a real account answers null until a project is chosen.
    private static IGcpAccount NewAccount()
    {
        var account = Substitute.For<IGcpAccount>();
        account.SelectedProjectId.Returns((string?)null);
        account.SelectedScope().Returns(TestScopes.Of(null));
        return account;
    }

    private static WizardViewModel NewViewModel(IGcpAccount account, IDispatcher? dispatcher = null)
        => new(account, Substitute.For<IDialogService>(), Substitute.For<INavigator>(), Strings(), dispatcher);

    [Fact]
    public async Task Choosing_a_project_stores_it_through_the_account_and_shows_it()
    {
        var account = NewAccount();
        account.SelectProjectAsync("my-project-1", Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        account.SelectedProjectId.Returns("my-project-1");
        account.SelectedScope().Returns(TestScopes.Of("my-project-1"));
        var viewModel = NewViewModel(account);

        await viewModel.ChooseProjectCommand.ExecuteAsync("my-project-1");

        await account.Received(1).SelectProjectAsync("my-project-1", Arg.Any<CancellationToken>());
        viewModel.SelectedProjectId.ShouldBe("my-project-1");
        viewModel.ProjectErrorText.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_project_chosen_in_the_wizard_is_recorded_in_the_account_list_and_a_refused_one_is_not()
    {
        // Issue #260 review: nothing but the Settings switcher and a run starting recorded a project for the account, so a wizard choice vanished from the list after a switch.
        var account = NewAccount();
        account.CurrentAccount.Returns(new AccountInfo("sub-1", "a@example.test"));
        account.SelectProjectAsync("my-project-1", Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        account.SelectProjectAsync("refused-1", Arg.Any<CancellationToken>()).Returns(Task.FromException(new AccountAuthException(AuthErrorCodes.SigninExpired)));
        var store = Substitute.For<IProjectSettingsStore>();
        var viewModel = new WizardViewModel(account, Substitute.For<IDialogService>(), Substitute.For<INavigator>(), Strings(), projectStore: store);

        await viewModel.ChooseProjectCommand.ExecuteAsync("my-project-1");
        await viewModel.ChooseProjectCommand.ExecuteAsync("refused-1");

        await store.Received(1).EnsureAsync("my-project-1", "sub-1", Arg.Any<CancellationToken>());
        await store.DidNotReceive().EnsureAsync("refused-1", Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_project_chosen_while_the_account_changes_is_not_filed_under_the_other_account()
    {
        var account = NewAccount();
        account.CurrentAccount.Returns(new AccountInfo("sub-1", "a@example.test"));
        account.SelectProjectAsync("my-project-1", Arg.Any<CancellationToken>()).Returns(Task.CompletedTask)
            .AndDoes(_ => account.CurrentAccount.Returns(new AccountInfo("sub-2", "b@example.test")));
        var store = Substitute.For<IProjectSettingsStore>();
        var viewModel = new WizardViewModel(account, Substitute.For<IDialogService>(), Substitute.For<INavigator>(), Strings(), projectStore: store);

        await viewModel.ChooseProjectCommand.ExecuteAsync("my-project-1");

        await store.DidNotReceive().EnsureAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_store_that_cannot_be_written_never_turns_a_good_choice_into_an_error()
    {
        var account = NewAccount();
        account.CurrentAccount.Returns(new AccountInfo("sub-1", "a@example.test"));
        account.SelectProjectAsync("my-project-1", Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        account.SelectedProjectId.Returns("my-project-1");
        var store = Substitute.For<IProjectSettingsStore>();
        store.EnsureAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(Task.FromException(new IOException("disk")));
        var viewModel = new WizardViewModel(account, Substitute.For<IDialogService>(), Substitute.For<INavigator>(), Strings(), projectStore: store);

        await viewModel.ChooseProjectCommand.ExecuteAsync("my-project-1");

        viewModel.ProjectErrorText.ShouldBeEmpty();
        viewModel.SelectedProjectId.ShouldBe("my-project-1");
    }

    [Theory]
    [InlineData(AuthErrorCodes.ProjectInvalid, "AuthError_PROJECT_INVALID", "")]
    [InlineData(AuthErrorCodes.SigninExpired, "AuthError_SIGNIN_EXPIRED", "AuthAction_SignInAgain")]
    [InlineData(AuthErrorCodes.ProjectSaveFailed, "AuthError_PROJECT_SAVE_FAILED", "AuthAction_TryAgain")]
    public async Task A_refused_choice_shows_its_message_and_action_and_keeps_no_project(string code, string messageKey, string actionKey)
    {
        var account = NewAccount();
        account.SelectProjectAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.FromException(new AccountAuthException(code)));
        var viewModel = NewViewModel(account);

        await viewModel.ChooseProjectCommand.ExecuteAsync("whatever");

        viewModel.ProjectErrorText.ShouldBe(messageKey);
        viewModel.ProjectActionText.ShouldBe(actionKey);
        viewModel.SelectedProjectId.ShouldBeNull();
    }

    [Fact]
    public async Task A_good_choice_after_a_refused_one_clears_the_message()
    {
        var account = NewAccount();
        account.SelectProjectAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new AccountAuthException(AuthErrorCodes.ProjectInvalid)), Task.CompletedTask);
        var viewModel = NewViewModel(account);
        await viewModel.ChooseProjectCommand.ExecuteAsync("bad");
        viewModel.ProjectErrorText.ShouldNotBeEmpty();

        account.SelectedProjectId.Returns("my-project-1");

        account.SelectedScope().Returns(TestScopes.Of("my-project-1"));
        await viewModel.ChooseProjectCommand.ExecuteAsync("my-project-1");

        viewModel.ProjectErrorText.ShouldBeEmpty();
        viewModel.ProjectActionText.ShouldBeEmpty();
        viewModel.SelectedProjectId.ShouldBe("my-project-1");
    }

    [Fact]
    public async Task The_results_are_applied_on_the_UI_thread_when_there_is_a_dispatcher()
    {
        var account = NewAccount();
        account.SelectProjectAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        var dispatcher = Substitute.For<IDispatcher>();
        var queued = new List<Action>();
        dispatcher.When(d => d.Enqueue(Arg.Any<Action>())).Do(call => queued.Add(call.Arg<Action>()));
        var viewModel = NewViewModel(account, dispatcher);
        account.SelectedProjectId.Returns("my-project-1");
        account.SelectedScope().Returns(TestScopes.Of("my-project-1"));

        await viewModel.ChooseProjectCommand.ExecuteAsync("my-project-1");

        viewModel.SelectedProjectId.ShouldBeNull("the property must not change off the UI thread");
        queued.ForEach(a => a());
        viewModel.SelectedProjectId.ShouldBe("my-project-1");
    }

    [Fact]
    public void The_shown_project_follows_the_account_when_it_changes()
    {
        var account = NewAccount();
        var viewModel = NewViewModel(account);
        viewModel.SelectedProjectId.ShouldBeNull();

        account.SelectedProjectId.Returns("second-project");

        account.SelectedScope().Returns(TestScopes.Of("second-project"));
        account.AccountChanged += Raise.Event();
        viewModel.SelectedProjectId.ShouldBe("second-project");

        account.SelectedProjectId.Returns((string?)null);

        account.SelectedScope().Returns(TestScopes.Of(null));
        account.AccountChanged += Raise.Event();
        viewModel.SelectedProjectId.ShouldBeNull();
    }

    [Fact]
    public async Task An_account_change_clears_the_previous_accounts_project_error()
    {
        var account = NewAccount();
        account.SelectProjectAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.FromException(new AccountAuthException(AuthErrorCodes.ProjectInvalid)));
        var viewModel = NewViewModel(account);
        await viewModel.ChooseProjectCommand.ExecuteAsync("bad");
        viewModel.ProjectErrorText.ShouldNotBeEmpty();

        account.AccountChanged += Raise.Event();

        viewModel.ProjectErrorText.ShouldBeEmpty();
        viewModel.ProjectActionText.ShouldBeEmpty();
    }

    [Fact]
    public void The_account_change_is_applied_on_the_UI_thread_when_there_is_a_dispatcher()
    {
        var account = NewAccount();
        var dispatcher = Substitute.For<IDispatcher>();
        var queued = new List<Action>();
        dispatcher.When(d => d.Enqueue(Arg.Any<Action>())).Do(call => queued.Add(call.Arg<Action>()));
        var viewModel = NewViewModel(account, dispatcher);

        account.SelectedProjectId.Returns("second-project");

        account.SelectedScope().Returns(TestScopes.Of("second-project"));
        account.AccountChanged += Raise.Event();

        viewModel.SelectedProjectId.ShouldBeNull("the property must not change off the UI thread");
        queued.ForEach(a => a());
        viewModel.SelectedProjectId.ShouldBe("second-project");
    }

    [Fact]
    public async Task Signing_in_shows_the_signed_in_accounts_project()
    {
        var account = NewAccount();
        var viewModel = NewViewModel(account);
        account.SignInAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask).AndDoes(_ => account.SelectedProjectId.Returns("signed-in-project"));

        await viewModel.SignInCommand.ExecuteAsync(null);

        viewModel.SelectedProjectId.ShouldBe("signed-in-project");
    }

    [Fact]
    public void The_wizard_opens_showing_the_project_the_account_already_has()
    {
        var account = NewAccount();
        account.SelectedProjectId.Returns("my-project-1");
        account.SelectedScope().Returns(TestScopes.Of("my-project-1"));

        NewViewModel(account).SelectedProjectId.ShouldBe("my-project-1");
    }
}
