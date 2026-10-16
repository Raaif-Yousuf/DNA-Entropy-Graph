using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Cloud;
using DnaEntropyGraph.Presentation.Services;
using DnaEntropyGraph.Presentation.ViewModels;
using NSubstitute;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Presentation.Tests;

/// <summary>
/// Issues #148 and #222: the Settings "Budget alert" group. Every refusal must land on one plain sentence and one action
/// (Hard Rule 13): a link to the console budgets page for a missing role (the common case, a project owner is often not a
/// billing administrator) and a copyable request for the billing administrator.
/// </summary>
public sealed class BudgetSettingsTests
{
    private const string Project = "my-lab";

    private readonly IBudgetService _budgets = Substitute.For<IBudgetService>();
    private readonly IGcpAccount _account = Substitute.For<IGcpAccount>();
    private readonly IShellLauncher _shell = Substitute.For<IShellLauncher>();
    private readonly IStringResourceProvider _strings = Substitute.For<IStringResourceProvider>();

    public BudgetSettingsTests()
    {
        _strings.GetString(Arg.Any<string>()).Returns(call => call.Arg<string>());
        _account.SelectedProjectId.Returns(Project);
        _budgets.ConsoleLinkAsync(Project, Arg.Any<CancellationToken>()).Returns("https://console.cloud.google.com/billing/AAA/budgets");
    }

    private BudgetSettingsViewModel Open() => new(_budgets, _account, _shell, _strings);

    private static BudgetInfo Existing(decimal amount) => new("billingAccounts/AAA/budgets/b-1", "DNA Entropy Graph inst-a", "billingAccounts/AAA", amount, [50, 90, 100]);

    private static CloudOperationException Failure(CloudErrorKind kind, string? code = null)
        => new(new CloudError(code, 403, "raw google text"), kind);

    [Fact]
    public async Task Opening_with_no_budget_offers_the_default_amount_and_no_remove()
    {
        _budgets.GetAsync(Project, Arg.Any<CancellationToken>()).Returns((BudgetInfo?)null);
        var vm = Open();

        await vm.RefreshCommand.ExecuteAsync(null);

        vm.HasProject.ShouldBeTrue();
        vm.HasBudget.ShouldBeFalse();
        vm.AmountText.ShouldBe("50");
        vm.RemoveCommand.CanExecute(null).ShouldBeFalse();
    }

    [Fact]
    public async Task Opening_with_a_budget_shows_its_amount_and_allows_remove()
    {
        _budgets.GetAsync(Project, Arg.Any<CancellationToken>()).Returns(Existing(75.5m));
        var vm = Open();

        await vm.RefreshCommand.ExecuteAsync(null);

        vm.HasBudget.ShouldBeTrue();
        vm.AmountText.ShouldBe(75.5m.ToString("0.##", System.Globalization.CultureInfo.CurrentCulture));
        vm.StatusText.ShouldBe("BudgetStatusExists");
        vm.RemoveCommand.CanExecute(null).ShouldBeTrue();
    }

    [Fact]
    public async Task Saving_sends_the_typed_amount_for_the_selected_project()
    {
        _budgets.SaveAsync(Project, 120m, Arg.Any<CancellationToken>()).Returns(Existing(120m));
        var vm = Open();
        vm.AmountText = "120";

        await vm.SaveCommand.ExecuteAsync(null);

        await _budgets.Received(1).SaveAsync(Project, 120m, Arg.Any<CancellationToken>());
        vm.HasBudget.ShouldBeTrue();
        vm.StatusText.ShouldBe("BudgetSaved");
        vm.ErrorText.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("0")]
    [InlineData("-3")]
    [InlineData("9999999999")]
    public async Task An_unusable_amount_is_refused_before_any_cloud_call(string text)
    {
        var vm = Open();
        vm.AmountText = text;

        await vm.SaveCommand.ExecuteAsync(null);

        vm.ErrorText.ShouldBe("BudgetAmountInvalid");
        await _budgets.DidNotReceive().SaveAsync(Arg.Any<string>(), Arg.Any<decimal>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_missing_billing_role_says_who_to_ask_and_offers_the_console_page_and_a_copyable_request()
    {
        _budgets.SaveAsync(Project, 50m, Arg.Any<CancellationToken>()).Returns<Task<BudgetInfo>>(_ => throw Failure(CloudErrorKind.Permission));
        var vm = Open();

        await vm.SaveCommand.ExecuteAsync(null);

        vm.ErrorText.ShouldBe("BudgetError_Permission");
        vm.ShowConsoleLink.ShouldBeTrue();
        vm.ConsoleLink!.ToString().ShouldBe("https://console.cloud.google.com/billing/AAA/budgets");
        vm.ShowCopyRequest.ShouldBeTrue();
        vm.HasBudget.ShouldBeFalse();

        _shell.CopyText(Arg.Any<string>()).Returns(true);
        vm.CopyRequestCommand.Execute(null);
        _shell.Received(1).CopyText("BudgetRequestText");
        vm.StatusText.ShouldBe("BudgetRequestCopied");
    }

    [Fact]
    public async Task A_project_with_no_billing_account_points_at_the_billing_page()
    {
        _budgets.SaveAsync(Project, 50m, Arg.Any<CancellationToken>()).Returns<Task<BudgetInfo>>(_ => throw Failure(CloudErrorKind.Billing, BudgetErrorCodes.NoBilling));
        var vm = Open();

        await vm.SaveCommand.ExecuteAsync(null);

        vm.ErrorText.ShouldBe("BudgetError_NoBilling");
        vm.ShowConsoleLink.ShouldBeTrue();
        vm.ShowCopyRequest.ShouldBeFalse();
    }

    [Theory]
    [InlineData(CloudErrorKind.Network, "BudgetError_Network")]
    [InlineData(CloudErrorKind.Other, "BudgetError_Other")]
    public async Task Every_other_failure_still_says_one_thing_to_try(CloudErrorKind kind, string expected)
    {
        _budgets.SaveAsync(Project, 50m, Arg.Any<CancellationToken>()).Returns<Task<BudgetInfo>>(_ => throw Failure(kind));
        var vm = Open();

        await vm.SaveCommand.ExecuteAsync(null);

        vm.ErrorText.ShouldBe(expected);
        vm.ErrorText.ShouldNotContain("raw google text");
    }

    [Fact]
    public async Task A_refused_look_on_open_is_neutral_info_never_a_red_error_and_offers_no_link()
    {
        _budgets.GetAsync(Project, Arg.Any<CancellationToken>()).Returns<Task<BudgetInfo?>>(_ => throw Failure(CloudErrorKind.Permission));
        var vm = Open();

        await vm.RefreshCommand.ExecuteAsync(null);

        vm.ErrorText.ShouldBeEmpty();
        vm.StatusText.ShouldBe("BudgetStatusNoAccess");
        vm.ShowConsoleLink.ShouldBeFalse();
        vm.AmountText.ShouldBe("50");
    }

    [Fact]
    public async Task Any_other_failed_look_on_open_is_neutral_too()
    {
        _budgets.GetAsync(Project, Arg.Any<CancellationToken>()).Returns<Task<BudgetInfo?>>(_ => throw Failure(CloudErrorKind.Network));
        var vm = Open();

        await vm.RefreshCommand.ExecuteAsync(null);

        vm.ErrorText.ShouldBeEmpty();
        vm.StatusText.ShouldBe("BudgetStatusCouldNotCheck");
    }

    [Fact]
    public async Task A_late_refresh_never_overwrites_an_amount_the_user_typed()
    {
        var gate = new TaskCompletionSource<BudgetInfo?>();
        _budgets.GetAsync(Project, Arg.Any<CancellationToken>()).Returns(gate.Task);
        var vm = Open();

        var refresh = vm.RefreshCommand.ExecuteAsync(null);
        vm.AmountText = "30";
        gate.SetResult(Existing(75m));
        await refresh;

        vm.AmountText.ShouldBe("30");
        vm.HasBudget.ShouldBeTrue();
    }

    [Fact]
    public async Task A_refresh_while_a_save_is_running_does_nothing()
    {
        var gate = new TaskCompletionSource<BudgetInfo>();
        _budgets.SaveAsync(Project, 50m, Arg.Any<CancellationToken>()).Returns(gate.Task);
        var vm = Open();

        var save = vm.SaveCommand.ExecuteAsync(null);
        await vm.RefreshCommand.ExecuteAsync(null);
        gate.SetResult(Existing(50m));
        await save;

        await _budgets.DidNotReceive().GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_organization_policy_refusal_asks_for_the_organizations_administrator_with_a_copyable_request()
    {
        _budgets.SaveAsync(Project, 50m, Arg.Any<CancellationToken>()).Returns<Task<BudgetInfo>>(_ => throw Failure(CloudErrorKind.OrgPolicy));
        var vm = Open();

        await vm.SaveCommand.ExecuteAsync(null);

        vm.ErrorText.ShouldBe("BudgetError_OrgPolicy");
        vm.ShowCopyRequest.ShouldBeTrue();
    }

    [Fact]
    public async Task A_budget_service_that_is_still_off_offers_the_page_that_switches_it_on()
    {
        _budgets.SaveAsync(Project, 50m, Arg.Any<CancellationToken>()).Returns<Task<BudgetInfo>>(_ => throw Failure(CloudErrorKind.ApiDisabled));
        var vm = Open();

        await vm.SaveCommand.ExecuteAsync(null);

        vm.ErrorText.ShouldBe("BudgetError_ApiDisabled");
        vm.ConsoleLink!.ToString().ShouldBe("https://console.cloud.google.com/apis/library/billingbudgets.googleapis.com?project=my-lab");
    }

    [Fact]
    public async Task The_amount_box_cannot_be_edited_while_a_save_runs()
    {
        var gate = new TaskCompletionSource<BudgetInfo>();
        _budgets.SaveAsync(Project, 50m, Arg.Any<CancellationToken>()).Returns(gate.Task);
        var vm = Open();
        vm.CanEdit.ShouldBeTrue();

        var save = vm.SaveCommand.ExecuteAsync(null);
        vm.CanEdit.ShouldBeFalse();
        gate.SetResult(Existing(50m));
        await save;

        vm.CanEdit.ShouldBeTrue();
    }

    [Fact]
    public async Task A_linked_account_that_is_switched_off_has_its_own_message_and_link()
    {
        _budgets.SaveAsync(Project, 50m, Arg.Any<CancellationToken>()).Returns<Task<BudgetInfo>>(_ => throw Failure(CloudErrorKind.Billing, BudgetErrorCodes.BillingDisabled));
        var vm = Open();

        await vm.SaveCommand.ExecuteAsync(null);

        vm.ErrorText.ShouldBe("BudgetError_BillingDisabled");
        vm.ShowConsoleLink.ShouldBeTrue();
    }

    [Fact]
    public async Task The_copied_request_for_an_organization_policy_refusal_is_addressed_to_the_organizations_administrator()
    {
        _budgets.SaveAsync(Project, 50m, Arg.Any<CancellationToken>()).Returns<Task<BudgetInfo>>(_ => throw Failure(CloudErrorKind.OrgPolicy));
        _shell.CopyText(Arg.Any<string>()).Returns(true);
        var vm = Open();
        await vm.SaveCommand.ExecuteAsync(null);

        vm.CopyRequestCommand.Execute(null);

        _shell.Received(1).CopyText("BudgetRequestText_OrgPolicy");
    }

    [Theory]
    [InlineData("de-DE", "12,5", 12.5)]
    [InlineData("en-US", "12.5", 12.5)]
    [InlineData("en-US", "$50", 50)]
    [InlineData("de-DE", "50", 50)]
    [InlineData("fr-FR", "12,5", 12.5)]
    public void The_amount_is_read_in_the_users_own_number_format(string culture, string text, double expected)
    {
        BudgetAmountParser.TryParse(text, new System.Globalization.CultureInfo(culture), out var amount).ShouldBeTrue();

        amount.ShouldBe((decimal)expected);
    }

    [Theory]
    [InlineData("en-US", "12,5")]
    [InlineData("en-US", "1,000")]
    [InlineData("de-DE", "1.000")]
    [InlineData("de-DE", "12.5")]
    [InlineData("en-US", "1.000,5")]
    [InlineData("en-US", "abc")]
    [InlineData("en-US", "")]
    public void An_ambiguous_or_unreadable_amount_is_refused_never_guessed(string culture, string text)
    {
        BudgetAmountParser.TryParse(text, new System.Globalization.CultureInfo(culture), out _).ShouldBeFalse();
    }
    [Fact]
    public async Task Removing_deletes_the_budget_and_goes_back_to_the_no_budget_state()
    {
        _budgets.GetAsync(Project, Arg.Any<CancellationToken>()).Returns(Existing(50m));
        _budgets.RemoveAsync(Project, Arg.Any<CancellationToken>()).Returns(true);
        var vm = Open();
        await vm.RefreshCommand.ExecuteAsync(null);

        await vm.RemoveCommand.ExecuteAsync(null);

        await _budgets.Received(1).RemoveAsync(Project, Arg.Any<CancellationToken>());
        vm.HasBudget.ShouldBeFalse();
        vm.StatusText.ShouldBe("BudgetRemoved");
    }

    [Fact]
    public async Task With_no_project_chosen_nothing_can_be_saved_and_the_page_says_to_set_one_up()
    {
        _account.SelectedProjectId.Returns((string?)null);
        var vm = Open();

        await vm.RefreshCommand.ExecuteAsync(null);

        vm.HasProject.ShouldBeFalse();
        vm.StatusText.ShouldBe("BudgetNeedsProject");
        vm.SaveCommand.CanExecute(null).ShouldBeFalse();
        await _budgets.DidNotReceive().GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
