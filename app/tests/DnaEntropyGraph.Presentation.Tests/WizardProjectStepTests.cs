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
        var viewModel = NewViewModel(account);

        await viewModel.ChooseProjectCommand.ExecuteAsync("my-project-1");

        await account.Received(1).SelectProjectAsync("my-project-1", Arg.Any<CancellationToken>());
        viewModel.SelectedProjectId.ShouldBe("my-project-1");
        viewModel.ProjectErrorText.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(AuthErrorCodes.ProjectInvalid, "AuthError_PROJECT_INVALID", "")]
    [InlineData(AuthErrorCodes.SigninExpired, "AuthError_SIGNIN_EXPIRED", "AuthAction_SignInAgain")]
    [InlineData(AuthErrorCodes.StorageFailed, "AuthError_SIGNIN_STORAGE", "AuthAction_SignInAgain")]
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

        await viewModel.ChooseProjectCommand.ExecuteAsync("my-project-1");

        viewModel.SelectedProjectId.ShouldBeNull("the property must not change off the UI thread");
        queued.ForEach(a => a());
        viewModel.SelectedProjectId.ShouldBe("my-project-1");
    }

    [Fact]
    public void The_wizard_opens_showing_the_project_the_account_already_has()
    {
        var account = NewAccount();
        account.SelectedProjectId.Returns("my-project-1");

        NewViewModel(account).SelectedProjectId.ShouldBe("my-project-1");
    }
}
