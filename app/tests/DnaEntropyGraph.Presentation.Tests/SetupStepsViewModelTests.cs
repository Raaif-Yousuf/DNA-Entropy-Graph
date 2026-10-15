using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Cloud;
using DnaEntropyGraph.Core.Setup;
using DnaEntropyGraph.Presentation.Services;
using DnaEntropyGraph.Presentation.ViewModels;
using NSubstitute;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Presentation.Tests;

/// <summary>Issue #99: the OK/Fix rows of wizard steps 4 to 7 (billing, services, storage and identity, GPU allowance) run in order, stop at the first red blocking row, and every red row carries one action.</summary>
public class SetupStepsViewModelTests
{
    private const string Project = "dna-entropy-abc12345";

    private readonly ISetupCheckService _service = Substitute.For<ISetupCheckService>();
    private readonly IGcpAccount _account = Substitute.For<IGcpAccount>();
    private readonly IShellLauncher _shell = Substitute.For<IShellLauncher>();
    private readonly IStringResourceProvider _strings = Substitute.For<IStringResourceProvider>();

    public SetupStepsViewModelTests()
    {
        _strings.GetString(Arg.Any<string>()).Returns(call => call.Arg<string>());
        _account.SelectedProjectId.Returns(Project);
        Ok(SetupStep.Billing, _service.CheckBillingAsync(Project, Arg.Any<CancellationToken>()));
        Ok(SetupStep.Services, _service.EnableServicesAsync(Project, Arg.Any<CancellationToken>()));
        Ok(SetupStep.Storage, _service.EnsureStorageAsync(Project, Arg.Any<CancellationToken>()));
        Ok(SetupStep.Quota, _service.CheckQuotaAsync(Project, Arg.Any<CancellationToken>()));
    }

    private static void Ok(SetupStep step, Task<SetupStepResult> call) => call.Returns(new SetupStepResult(step, SetupStatus.Ok));

    private SetupStepsViewModel NewViewModel() => new(_service, _account, _shell, _strings);

    private static SetupStepResult Fix(SetupStep step, string code, SetupActionKind kind, string? label = "SetupAction_TryAgain")
        => new(step, SetupStatus.NeedsAction) { Code = code, Action = new SetupAction(kind, label) };

    [Fact]
    public async Task Run_all_checks_every_step_in_order_and_turns_every_row_green()
    {
        var viewModel = NewViewModel();

        await viewModel.RunAllCommand.ExecuteAsync(null);

        viewModel.Rows.Select(r => r.Step).ShouldBe([SetupStep.Billing, SetupStep.Services, SetupStep.Storage, SetupStep.Quota]);
        viewModel.Rows.ShouldAllBe(r => r.Status == SetupStatus.Ok && r.StatusText == "WizardStatus_Ok");
        Received.InOrder(() =>
        {
            _service.CheckBillingAsync(Project, Arg.Any<CancellationToken>());
            _service.EnableServicesAsync(Project, Arg.Any<CancellationToken>());
            _service.EnsureStorageAsync(Project, Arg.Any<CancellationToken>());
            _service.CheckQuotaAsync(Project, Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public async Task A_red_billing_row_stops_the_chain_so_nobody_pays_for_a_step_that_cannot_work()
    {
        _service.CheckBillingAsync(Project, Arg.Any<CancellationToken>()).Returns(Fix(SetupStep.Billing, SetupErrorCodes.NoBilling, SetupActionKind.OpenLink, "SetupAction_LinkBilling") with { Link = BillingLinks.ForProject(Project) });
        var viewModel = NewViewModel();

        await viewModel.RunAllCommand.ExecuteAsync(null);

        var billing = viewModel.Rows[0];
        billing.Status.ShouldBe(SetupStatus.NeedsAction);
        billing.StatusText.ShouldBe("WizardStatus_Fix");
        billing.MessageText.ShouldBe("SetupError_NO_BILLING");
        billing.ActionText.ShouldBe("SetupAction_LinkBilling");
        billing.HasLinkAction.ShouldBeTrue();
        billing.LinkUri.ShouldBe(new Uri(BillingLinks.ForProject(Project)));
        billing.HasRecheck.ShouldBeTrue();
        billing.ShowDoItByHand.ShouldBeTrue();
        viewModel.Rows.Skip(1).ShouldAllBe(r => r.Status == SetupStatus.Pending);
        await _service.DidNotReceiveWithAnyArgs().EnableServicesAsync(default!, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Without_a_chosen_project_nothing_is_called()
    {
        _account.SelectedProjectId.Returns((string?)null);
        var viewModel = NewViewModel();

        await viewModel.RunAllCommand.ExecuteAsync(null);

        viewModel.Rows.ShouldAllBe(r => r.Status == SetupStatus.Pending);
        await _service.DidNotReceiveWithAnyArgs().CheckBillingAsync(default!, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task The_row_button_for_a_retryable_failure_runs_that_step_again_and_carries_on_down_the_list()
    {
        _service.EnableServicesAsync(Project, Arg.Any<CancellationToken>())
            .Returns(Fix(SetupStep.Services, SetupErrorCodes.ApiDisabled, SetupActionKind.Retry, "SetupAction_TurnItOn"), new SetupStepResult(SetupStep.Services, SetupStatus.Ok));
        var viewModel = NewViewModel();
        await viewModel.RunAllCommand.ExecuteAsync(null);
        viewModel.Rows[1].ActionText.ShouldBe("SetupAction_TurnItOn");
        await _service.DidNotReceiveWithAnyArgs().EnsureStorageAsync(default!, Arg.Any<CancellationToken>());

        await viewModel.Rows[1].ActionCommand!.ExecuteAsync(null);

        viewModel.Rows.ShouldAllBe(r => r.Status == SetupStatus.Ok);
        await _service.Received(1).EnsureStorageAsync(Project, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Check_again_after_a_link_reruns_the_step()
    {
        _service.CheckBillingAsync(Project, Arg.Any<CancellationToken>())
            .Returns(Fix(SetupStep.Billing, SetupErrorCodes.NoBilling, SetupActionKind.OpenLink, "SetupAction_LinkBilling") with { Link = BillingLinks.ForProject(Project) }, new SetupStepResult(SetupStep.Billing, SetupStatus.Ok));
        var viewModel = NewViewModel();
        await viewModel.RunAllCommand.ExecuteAsync(null);

        await viewModel.Rows[0].RecheckCommand!.ExecuteAsync(null);

        viewModel.Rows.ShouldAllBe(r => r.Status == SetupStatus.Ok);
    }

    [Fact]
    public async Task The_billing_request_text_names_the_account_that_was_refused()
    {
        // The #51 review note: the {account} placeholder was never filled by anything.
        _service.CheckBillingAsync(Project, Arg.Any<CancellationToken>())
            .Returns(Fix(SetupStep.Billing, SetupErrorCodes.BillingNoPermission, SetupActionKind.CopyText, "SetupAction_CopyBillingRequest") with { AccountId = "billingAccounts/0X-1" });
        _strings.GetString("SetupBillingRequestText").Returns("Link {project} to {account}.");
        _shell.CopyText(Arg.Any<string>()).Returns(true);
        var viewModel = NewViewModel();
        await viewModel.RunAllCommand.ExecuteAsync(null);

        await viewModel.Rows[0].ActionCommand!.ExecuteAsync(null);

        _shell.Received(1).CopyText($"Link {Project} to billingAccounts/0X-1.");
        viewModel.NoticeText.ShouldBe("WizardCopied");
    }

    [Fact]
    public async Task A_help_request_for_the_owner_carries_the_project_and_the_problem()
    {
        _service.EnableServicesAsync(Project, Arg.Any<CancellationToken>()).Returns(Fix(SetupStep.Services, SetupErrorCodes.Permission, SetupActionKind.CopyText, "SetupAction_CopyRequestForOwner"));
        _strings.GetString("SetupHelpRequestText").Returns("About {project}: {problem}");
        _strings.GetString("SetupError_PERMISSION").Returns("You may not do that.");
        _shell.CopyText(Arg.Any<string>()).Returns(true);
        var viewModel = NewViewModel();
        await viewModel.RunAllCommand.ExecuteAsync(null);

        await viewModel.Rows[1].ActionCommand!.ExecuteAsync(null);

        _shell.Received(1).CopyText($"About {Project}: You may not do that.");
    }

    [Fact]
    public async Task A_busy_clipboard_says_so_and_names_an_action()
    {
        _service.EnableServicesAsync(Project, Arg.Any<CancellationToken>()).Returns(Fix(SetupStep.Services, SetupErrorCodes.Permission, SetupActionKind.CopyText, "SetupAction_CopyRequestForOwner"));
        _shell.CopyText(Arg.Any<string>()).Returns(false);
        var viewModel = NewViewModel();
        await viewModel.RunAllCommand.ExecuteAsync(null);

        await viewModel.Rows[1].ActionCommand!.ExecuteAsync(null);

        viewModel.NoticeText.ShouldBe("WizardCopyFailed");
    }

    [Fact]
    public async Task With_several_billing_accounts_each_choice_links_that_account_and_the_chain_goes_on()
    {
        var accounts = new[] { new BillingAccountSummary("a1", "Lab card"), new BillingAccountSummary("a2", "Department") };
        _service.CheckBillingAsync(Project, Arg.Any<CancellationToken>())
            .Returns(new SetupStepResult(SetupStep.Billing, SetupStatus.NeedsAction) { Accounts = accounts, Action = new SetupAction(SetupActionKind.PickBillingAccount, "SetupAction_PickAnotherBillingAccount") });
        _service.LinkBillingAsync(Project, "a2", Arg.Any<CancellationToken>()).Returns(new SetupStepResult(SetupStep.Billing, SetupStatus.Ok) { AccountId = "a2" });
        var viewModel = NewViewModel();
        await viewModel.RunAllCommand.ExecuteAsync(null);

        viewModel.BillingChoices.Select(c => c.DisplayName).ShouldBe(["Lab card", "Department"]);
        viewModel.HasBillingChoices.ShouldBeTrue();
        await viewModel.BillingChoices[1].ChooseCommand.ExecuteAsync(null);

        await _service.Received(1).LinkBillingAsync(Project, "a2", Arg.Any<CancellationToken>());
        viewModel.Rows.ShouldAllBe(r => r.Status == SetupStatus.Ok);
        viewModel.HasBillingChoices.ShouldBeFalse();
    }

    [Fact]
    public async Task A_missing_gpu_allowance_does_not_block_and_can_be_skipped_for_now()
    {
        _service.CheckQuotaAsync(Project, Arg.Any<CancellationToken>())
            .Returns(new SetupStepResult(SetupStep.Quota, SetupStatus.NeedsAction)
            {
                Link = QuotaLinks.ForProject(Project),
                CanSkip = true,
                Action = new SetupAction(SetupActionKind.OpenLink, "SetupAction_RequestGpuAllowance"),
                Tiers = [new QuotaTierResult("L4", QuotaMetrics.NvidiaL4Gpus, 0, QuotaState.None)],
            });
        var viewModel = NewViewModel();
        await viewModel.RunAllCommand.ExecuteAsync(null);

        var quota = viewModel.Rows[3];
        quota.Status.ShouldBe(SetupStatus.NeedsAction);
        quota.MessageText.ShouldContain("WizardQuota_None");
        quota.MessageText.ShouldContain("WizardQuota_NotYet");
        quota.HasSkip.ShouldBeTrue();

        await quota.SkipCommand!.ExecuteAsync(null);

        quota.Status.ShouldBe(SetupStatus.Skipped);
        quota.StatusText.ShouldBe("WizardStatus_Skipped");
        viewModel.IsAllDone.ShouldBeTrue();
    }

    [Fact]
    public async Task Allowed_gpu_tiers_are_listed_with_their_region_counts_and_the_note_for_the_default_account_shows()
    {
        _service.EnsureStorageAsync(Project, Arg.Any<CancellationToken>())
            .Returns(new SetupStepResult(SetupStep.Storage, SetupStatus.Ok) { Bucket = "deg-1-abcdef", NoteCode = SetupErrorCodes.WorkerDefaultAccount });
        _service.CheckQuotaAsync(Project, Arg.Any<CancellationToken>())
            .Returns(new SetupStepResult(SetupStep.Quota, SetupStatus.Ok) { Tiers = [new QuotaTierResult("L4", QuotaMetrics.NvidiaL4Gpus, 3, QuotaState.Available)] });
        _strings.GetString("WizardQuota_Allowed").Returns("{0}: allowed in {1} regions");
        _strings.GetString("WizardStorage_Ok").Returns("Results bucket {0}");
        var viewModel = NewViewModel();

        await viewModel.RunAllCommand.ExecuteAsync(null);

        viewModel.Rows[3].MessageText.ShouldBe("L4: allowed in 3 regions");
        viewModel.Rows[2].MessageText.ShouldBe("Results bucket deg-1-abcdef");
        viewModel.Rows[2].NoteText.ShouldBe("SetupError_WORKER_DEFAULT_ACCOUNT");
        viewModel.IsAllDone.ShouldBeTrue();
    }

    [Fact]
    public async Task Running_again_clears_the_old_message_first()
    {
        _service.CheckBillingAsync(Project, Arg.Any<CancellationToken>())
            .Returns(Fix(SetupStep.Billing, SetupErrorCodes.NoBilling, SetupActionKind.OpenLink, "SetupAction_LinkBilling") with { Link = BillingLinks.ForProject(Project) }, new SetupStepResult(SetupStep.Billing, SetupStatus.Ok));
        var viewModel = NewViewModel();
        await viewModel.RunAllCommand.ExecuteAsync(null);
        viewModel.Rows[0].HasMessage.ShouldBeTrue();

        await viewModel.RunAllCommand.ExecuteAsync(null);

        viewModel.Rows[0].HasMessage.ShouldBeFalse();
        viewModel.Rows[0].HasAction.ShouldBeFalse();
    }
}
