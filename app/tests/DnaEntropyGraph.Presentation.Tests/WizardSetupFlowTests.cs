using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Cloud;
using DnaEntropyGraph.Core.Setup;
using DnaEntropyGraph.Presentation.Services;
using DnaEntropyGraph.Presentation.ViewModels;
using NSubstitute;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Presentation.Tests;

/// <summary>Issue #99: the wizard page's flow over <see cref="WizardViewModel"/>: sign in, find or make a project, then the OK/Fix rows, then finish.</summary>
public class WizardSetupFlowTests
{
    private const string Project = "dna-entropy-abc12345";

    private readonly IGcpAccount _account = Substitute.For<IGcpAccount>();
    private readonly ISetupCheckService _service = Substitute.For<ISetupCheckService>();
    private readonly INavigator _navigator = Substitute.For<INavigator>();
    private readonly ISettingsStore _settings = Substitute.For<ISettingsStore>();
    private readonly IStringResourceProvider _strings = Substitute.For<IStringResourceProvider>();
    private readonly IShellLauncher _shell = Substitute.For<IShellLauncher>();

    public WizardSetupFlowTests()
    {
        _strings.GetString(Arg.Any<string>()).Returns(call => call.Arg<string>());
        _account.SelectedProjectId.Returns((string?)null);
        _account.IsSignedIn.Returns(true);
        _account.SignInAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        _service.CheckBillingAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new SetupStepResult(SetupStep.Billing, SetupStatus.Ok));
        _service.EnableServicesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new SetupStepResult(SetupStep.Services, SetupStatus.Ok));
        _service.EnsureStorageAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new SetupStepResult(SetupStep.Storage, SetupStatus.Ok));
        _service.CheckQuotaAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new SetupStepResult(SetupStep.Quota, SetupStatus.Ok));
        _service.CheckProjectAsync(Project, Arg.Any<CancellationToken>()).Returns(new SetupStepResult(SetupStep.Project, SetupStatus.Ok) { ProjectId = Project });
        _account.SelectProjectAsync(Project, Arg.Any<CancellationToken>()).Returns(_ =>
        {
            _account.SelectedProjectId.Returns(Project);
            return Task.CompletedTask;
        });
    }

    private WizardViewModel NewViewModel()
    {
        var steps = new SetupStepsViewModel(_service, _account, _shell, _strings);
        return new WizardViewModel(_account, Substitute.For<IDialogService>(), _navigator, _strings, null, _service, steps, _settings);
    }

    private static readonly ProjectSummary[] Listed =
    [
        new("lab-project", "Lab", ProjectLifecycleState.Active, false),
        new(Project, "DNA Entropy Graph", ProjectLifecycleState.Active, true),
    ];

    [Fact]
    public async Task Signing_in_with_a_project_already_chosen_checks_it_and_runs_the_remaining_steps()
    {
        _account.SelectedProjectId.Returns(Project);
        var viewModel = NewViewModel();

        await viewModel.SignInCommand.ExecuteAsync(null);

        viewModel.ProjectRow.Status.ShouldBe(SetupStatus.Ok);
        viewModel.SetupSteps!.Rows.ShouldAllBe(r => r.Status == SetupStatus.Ok);
        viewModel.HasProjects.ShouldBeFalse();
        _navigator.DidNotReceive().NavigateTo(Arg.Any<string>());
    }

    [Fact]
    public async Task Signing_in_with_no_project_offers_the_list_and_runs_nothing_else()
    {
        _service.ListProjectsAsync(Arg.Any<CancellationToken>()).Returns(new SetupStepResult(SetupStep.Project, SetupStatus.Ok) { Projects = Listed });
        var viewModel = NewViewModel();

        await viewModel.SignInCommand.ExecuteAsync(null);

        viewModel.ProjectRow.Status.ShouldBe(SetupStatus.NeedsAction);
        viewModel.ProjectRow.MessageText.ShouldBe("WizardProject_Pick");
        viewModel.Projects.Select(p => p.ProjectId).ShouldBe(["lab-project", Project]);
        viewModel.HasProjects.ShouldBeTrue();
        viewModel.SetupSteps!.Rows.ShouldAllBe(r => r.Status == SetupStatus.Pending);
    }

    [Fact]
    public async Task A_new_account_with_no_projects_is_told_to_make_one()
    {
        _service.ListProjectsAsync(Arg.Any<CancellationToken>()).Returns(new SetupStepResult(SetupStep.Project, SetupStatus.Ok));
        var viewModel = NewViewModel();

        await viewModel.SignInCommand.ExecuteAsync(null);

        viewModel.ProjectRow.MessageText.ShouldBe("WizardProject_None");
        viewModel.HasProjects.ShouldBeFalse();
        viewModel.CreateProjectCommand.CanExecute(null).ShouldBeTrue();
    }

    [Fact]
    public async Task A_failed_listing_shows_its_message_and_action()
    {
        _service.ListProjectsAsync(Arg.Any<CancellationToken>()).Returns(new SetupStepResult(SetupStep.Project, SetupStatus.NeedsAction)
        {
            Code = SetupErrorCodes.Permission,
            Action = new SetupAction(SetupActionKind.CopyText, "SetupAction_CopyRequestForOwner"),
        });
        var viewModel = NewViewModel();

        await viewModel.SignInCommand.ExecuteAsync(null);

        viewModel.ProjectRow.MessageText.ShouldBe("SetupError_PERMISSION");
        viewModel.ProjectRow.ActionText.ShouldBe("SetupAction_CopyRequestForOwner");
    }

    [Fact]
    public async Task Picking_a_listed_project_stores_it_checks_it_and_runs_the_remaining_steps()
    {
        _service.ListProjectsAsync(Arg.Any<CancellationToken>()).Returns(new SetupStepResult(SetupStep.Project, SetupStatus.Ok) { Projects = Listed });
        var viewModel = NewViewModel();
        await viewModel.SignInCommand.ExecuteAsync(null);

        await viewModel.Projects[1].ChooseCommand.ExecuteAsync(null);

        await _account.Received(1).SelectProjectAsync(Project, Arg.Any<CancellationToken>());
        viewModel.ProjectRow.Status.ShouldBe(SetupStatus.Ok);
        viewModel.HasProjects.ShouldBeFalse();
        viewModel.SetupSteps!.Rows.ShouldAllBe(r => r.Status == SetupStatus.Ok);
    }

    [Fact]
    public async Task Creating_a_project_makes_a_legal_id_labels_it_with_the_installation_and_continues()
    {
        string? madeId = null;
        _service.CreateProjectAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            madeId = call.ArgAt<string>(0);
            return new SetupStepResult(SetupStep.Project, SetupStatus.Ok) { ProjectId = Project };
        });
        var viewModel = NewViewModel();

        await viewModel.CreateProjectCommand.ExecuteAsync(null);

        madeId.ShouldNotBeNull();
        ProjectIdGenerator.IsValid(madeId).ShouldBeTrue();
        await _service.Received(1).CreateProjectAsync(madeId, Arg.Is<string>(id => InstallationId.IsValid(id)), Arg.Any<CancellationToken>());
        await _account.Received(1).SelectProjectAsync(Project, Arg.Any<CancellationToken>());
        viewModel.SetupSteps!.Rows.ShouldAllBe(r => r.Status == SetupStatus.Ok);
    }

    [Fact]
    public async Task A_project_limit_names_the_pick_an_existing_project_action_and_lists_them()
    {
        _service.CreateProjectAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new SetupStepResult(SetupStep.Project, SetupStatus.NeedsAction)
        {
            Code = SetupErrorCodes.ProjectQuota,
            Action = new SetupAction(SetupActionKind.PickProject, "SetupAction_PickExistingProject"),
        });
        _service.ListProjectsAsync(Arg.Any<CancellationToken>()).Returns(new SetupStepResult(SetupStep.Project, SetupStatus.Ok) { Projects = Listed });
        var viewModel = NewViewModel();

        await viewModel.CreateProjectCommand.ExecuteAsync(null);

        viewModel.ProjectRow.MessageText.ShouldBe("SetupError_PROJECT_QUOTA");
        viewModel.ProjectRow.ActionText.ShouldBe("SetupAction_PickExistingProject");
        viewModel.Projects.Count.ShouldBe(2);
        await _account.DidNotReceiveWithAnyArgs().SelectProjectAsync(default!, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_project_the_account_cannot_use_goes_back_to_the_list()
    {
        _account.SelectedProjectId.Returns("gone-project");
        _service.CheckProjectAsync("gone-project", Arg.Any<CancellationToken>()).Returns(new SetupStepResult(SetupStep.Project, SetupStatus.NeedsAction)
        {
            Code = AuthErrorCodes.ProjectInvalid,
            Action = new SetupAction(SetupActionKind.PickProject, "SetupAction_PickExistingProject"),
        });
        _service.ListProjectsAsync(Arg.Any<CancellationToken>()).Returns(new SetupStepResult(SetupStep.Project, SetupStatus.Ok) { Projects = Listed });
        var viewModel = NewViewModel();

        await viewModel.SignInCommand.ExecuteAsync(null);

        viewModel.ProjectRow.Status.ShouldBe(SetupStatus.NeedsAction);
        viewModel.ProjectRow.MessageText.ShouldBe("AuthError_PROJECT_INVALID");
        viewModel.HasProjects.ShouldBeTrue();
        await _service.DidNotReceiveWithAnyArgs().CheckBillingAsync(default!, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Finish_goes_to_the_new_run_page_only_when_the_checks_allow_it()
    {
        _account.SelectedProjectId.Returns(Project);
        var viewModel = NewViewModel();
        viewModel.FinishCommand.CanExecute(null).ShouldBeFalse();

        await viewModel.SignInCommand.ExecuteAsync(null);
        viewModel.FinishCommand.CanExecute(null).ShouldBeTrue();
        viewModel.FinishCommand.Execute(null);

        _navigator.Received(1).NavigateTo("NewRun");
    }

    [Fact]
    public void A_wizard_built_without_the_setup_services_still_signs_in()
    {
        var viewModel = new WizardViewModel(_account, Substitute.For<IDialogService>(), _navigator, _strings);

        viewModel.SetupSteps.ShouldBeNull();
        viewModel.IsSignedIn.ShouldBeTrue();
    }
}
