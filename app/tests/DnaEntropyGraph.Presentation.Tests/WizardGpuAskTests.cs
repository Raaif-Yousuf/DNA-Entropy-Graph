using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Cloud;
using DnaEntropyGraph.Core.Setup;
using DnaEntropyGraph.Presentation.Services;
using DnaEntropyGraph.Presentation.ViewModels;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Presentation.Tests;

/// <summary>
/// Issue #99, the wizard's GPU step: "Ask Google for a GPU" runs <see cref="GpuQuotaRequester"/> for the chosen project and shows one action per outcome.
/// The decisive observables are the requests that reached the quota gateway and the one button each outcome leaves on the row.
/// </summary>
public class WizardGpuAskTests
{
    private const string Project = "dna-entropy-abc12345";
    private const string L4 = QuotaMetrics.NvidiaL4Gpus;
    private const string All = QuotaMetrics.GpusAllRegions;

    private readonly ISetupCheckService _service = Substitute.For<ISetupCheckService>();
    private readonly IGcpAccount _account = Substitute.For<IGcpAccount>();
    private readonly IShellLauncher _shell = Substitute.For<IShellLauncher>();
    private readonly IStringResourceProvider _strings = Substitute.For<IStringResourceProvider>();
    private readonly IQuotaGateway _quota = Substitute.For<IQuotaGateway>();

    public WizardGpuAskTests()
    {
        _strings.GetString(Arg.Any<string>()).Returns(call => call.Arg<string>());
        _account.SelectedProjectId.Returns(Project);
        _account.SelectedScope().Returns(new CloudScope(Project, "sub-1"));
        _account.ActiveSnapshot().Returns(new ActiveAccountSnapshot("sub-1", Project, false));
        _account.CurrentAccount.Returns(new AccountInfo("sub-1", "lab@example.org"));
        _service.CheckBillingAsync(Project, Arg.Any<CancellationToken>()).Returns(new SetupStepResult(SetupStep.Billing, SetupStatus.Ok));
        _service.EnableServicesAsync(Project, Arg.Any<CancellationToken>()).Returns(new SetupStepResult(SetupStep.Services, SetupStatus.Ok));
        _service.EnsureStorageAsync(Project, Arg.Any<CancellationToken>()).Returns(new SetupStepResult(SetupStep.Storage, SetupStatus.Ok));
        _service.CheckQuotaAsync(Project, Arg.Any<CancellationToken>()).Returns(ZeroQuota());
        _quota.ReadQuotaAsync(new CloudScope(Project, "sub-1"), Arg.Any<CancellationToken>()).Returns(new QuotaReadResult(
            new Dictionary<string, IReadOnlyList<QuotaMetric>> { ["us-central1"] = new List<QuotaMetric> { new(L4, 0, 0) } },
            new QuotaMetric(All, 0, 0),
            []));
        _quota.GetQuotaIncreaseEligibilityAsync(new CloudScope(Project, "sub-1"), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => new QuotaEligibility(call.ArgAt<string>(1), true));
        _quota.RequestQuotaIncreaseAsync(new CloudScope(Project, "sub-1"), Arg.Any<QuotaIncreaseRequest>(), Arg.Any<CancellationToken>())
            .Returns(new QuotaIncreaseResult("prefs/x", 1, null, true, "Pending review"));
    }

    private static SetupStepResult ZeroQuota() => new(SetupStep.Quota, SetupStatus.NeedsAction)
    {
        Link = QuotaLinks.ForProject(Project),
        CanSkip = true,
        Action = new SetupAction(SetupActionKind.OpenLink, "SetupAction_RequestGpuAllowance"),
        Tiers = [new QuotaTierResult("L4", L4, 0, QuotaState.None)],
    };

    private SetupStepsViewModel NewViewModel(bool withRequester = true)
        => new(_service, _account, _shell, _strings, withRequester ? new GpuQuotaRequester(_quota, _account) : null);

    private async Task<(SetupStepsViewModel Steps, SetupStepRow Row)> ZeroQuotaRowAsync(bool withRequester = true)
    {
        var steps = NewViewModel(withRequester);
        await steps.RunAllCommand.ExecuteAsync(null);
        return (steps, steps.Rows.Single(r => r.Step == SetupStep.Quota));
    }

    private static CloudOperationException Failure(CloudErrorKind kind) => new(new CloudError(kind.ToString(), null, "x"), kind);

    [Fact]
    public async Task A_zero_allowance_row_offers_the_ask_button_and_a_row_without_a_requester_does_not()
    {
        var (steps, row) = await ZeroQuotaRowAsync();
        row.HasAsk.ShouldBeTrue();
        row.AskCommand.ShouldNotBeNull();
        steps.Rows[0].HasAsk.ShouldBeFalse("only the GPU row asks");

        var (_, noRequester) = await ZeroQuotaRowAsync(withRequester: false);
        noRequester.HasAsk.ShouldBeFalse("nothing is wired to answer the button");
    }

    [Fact]
    public async Task Asking_sends_one_request_per_missing_quota_to_the_gateway_with_the_account_email_and_says_google_is_reviewing()
    {
        var (_, row) = await ZeroQuotaRowAsync();

        await row.AskCommand!.ExecuteAsync(null);

        await _quota.Received(1).RequestQuotaIncreaseAsync(new CloudScope(Project, "sub-1"), Arg.Is<QuotaIncreaseRequest>(r => r.Metric == L4 && r.Region == CloudJobRequestFactory.DefaultRegion && r.ContactEmail == "lab@example.org"), Arg.Any<CancellationToken>());
        await _quota.Received(1).RequestQuotaIncreaseAsync(new CloudScope(Project, "sub-1"), Arg.Is<QuotaIncreaseRequest>(r => r.Metric == All), Arg.Any<CancellationToken>());
        row.AskResultText.ShouldBe("WizardGpuAsk_Requested");
        row.HasAskResult.ShouldBeTrue();
        row.AskActionText.ShouldBe("WizardGpuAsk_CheckAgain");
        row.HasAskActionCommand.ShouldBeTrue();
        row.HasAskLink.ShouldBeFalse();
    }

    [Fact]
    public async Task A_grant_at_once_rechecks_the_step_so_the_row_turns_green()
    {
        _quota.RequestQuotaIncreaseAsync(new CloudScope(Project, "sub-1"), Arg.Any<QuotaIncreaseRequest>(), Arg.Any<CancellationToken>())
            .Returns(new QuotaIncreaseResult("prefs/x", 1, 1, false, "Granted"));
        _service.CheckQuotaAsync(Project, Arg.Any<CancellationToken>())
            .Returns(ZeroQuota(), new SetupStepResult(SetupStep.Quota, SetupStatus.Ok));
        var (steps, row) = await ZeroQuotaRowAsync();

        await row.AskCommand!.ExecuteAsync(null);

        await _service.Received(2).CheckQuotaAsync(Project, Arg.Any<CancellationToken>());
        row.Status.ShouldBe(SetupStatus.Ok);
        row.HasAskResult.ShouldBeFalse();
        row.HasAsk.ShouldBeFalse();
        steps.Rows.ShouldAllBe(r => r.Status == SetupStatus.Ok);
    }

    [Fact]
    public async Task A_project_with_no_billing_account_is_told_to_fix_billing_and_the_action_rechecks_billing()
    {
        _quota.GetQuotaIncreaseEligibilityAsync(new CloudScope(Project, "sub-1"), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => new QuotaEligibility(call.ArgAt<string>(1), false, QuotaIneligibilityReason.NoValidBillingAccount));
        var (_, row) = await ZeroQuotaRowAsync();

        await row.AskCommand!.ExecuteAsync(null);

        row.AskResultText.ShouldBe("WizardGpuAsk_NoBilling");
        row.AskActionText.ShouldBe("WizardGpuAsk_CheckBilling");
        await _quota.DidNotReceiveWithAnyArgs().RequestQuotaIncreaseAsync(default!, default!, Arg.Any<CancellationToken>());
        await row.AskActionCommand!.ExecuteAsync(null);
        await _service.Received(2).CheckBillingAsync(Project, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(QuotaIneligibilityReason.NotEnoughUsageHistory, "WizardGpuAsk_NoHistory")]
    [InlineData(QuotaIneligibilityReason.NotSupported, "WizardGpuAsk_NotEligible")]
    [InlineData(QuotaIneligibilityReason.Other, "WizardGpuAsk_NotEligible")]
    public async Task Another_ineligible_reason_sends_the_user_to_googles_quota_page_by_hand(QuotaIneligibilityReason reason, string expectedKey)
    {
        _quota.GetQuotaIncreaseEligibilityAsync(new CloudScope(Project, "sub-1"), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => new QuotaEligibility(call.ArgAt<string>(1), false, reason));
        var (_, row) = await ZeroQuotaRowAsync();

        await row.AskCommand!.ExecuteAsync(null);

        row.AskResultText.ShouldBe(expectedKey);
        row.HasAskLink.ShouldBeTrue();
        row.AskLinkUri.ShouldBe(new Uri(QuotaLinks.ForProject(Project)));
        row.AskActionText.ShouldBe("WizardGpuAsk_OpenQuotaPage");
        row.HasAskActionCommand.ShouldBeFalse();
    }

    [Fact]
    public async Task A_refused_permission_names_the_quota_page_and_a_transient_failure_offers_try_again_which_asks_again()
    {
        _quota.GetQuotaIncreaseEligibilityAsync(new CloudScope(Project, "sub-1"), Arg.Any<string>(), Arg.Any<CancellationToken>()).ThrowsAsync(Failure(CloudErrorKind.Permission));
        var (_, row) = await ZeroQuotaRowAsync();

        await row.AskCommand!.ExecuteAsync(null);
        row.AskResultText.ShouldBe("WizardGpuAsk_Permission");
        row.HasAskLink.ShouldBeTrue();

        _quota.GetQuotaIncreaseEligibilityAsync(new CloudScope(Project, "sub-1"), Arg.Any<string>(), Arg.Any<CancellationToken>()).ThrowsAsync(Failure(CloudErrorKind.Network));
        await row.AskCommand!.ExecuteAsync(null);
        row.AskResultText.ShouldBe("WizardGpuAsk_Failed");
        row.AskActionText.ShouldBe("WizardGpuAsk_TryAgain");
        var before = _quota.ReceivedCalls().Count();
        await row.AskActionCommand!.ExecuteAsync(null);
        _quota.ReceivedCalls().Count().ShouldBeGreaterThan(before, "Try again must ask again, not just clear the message");
    }

    [Fact]
    public async Task A_fresh_check_of_the_step_replaces_the_old_answer()
    {
        var (steps, row) = await ZeroQuotaRowAsync();
        await row.AskCommand!.ExecuteAsync(null);
        row.HasAskResult.ShouldBeTrue();

        await row.RecheckCommand!.ExecuteAsync(null);

        row.HasAskResult.ShouldBeFalse();
        steps.IsBusy.ShouldBeFalse();
    }
}
