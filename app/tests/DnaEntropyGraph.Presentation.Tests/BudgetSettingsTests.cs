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

        // The list phrasing is resw copy (Hard Rule 13): distinctive formats, so a joiner written in code instead of read from these keys shows.
        _strings.GetString("BudgetThresholdItem").Returns("i{0}");
        _strings.GetString("BudgetThresholdsJoiner").Returns("+");
        _strings.GetString("BudgetThresholdsAnd").Returns("{0}&{1}");
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

    [Fact]
    public async Task Failing_to_switch_the_budget_API_on_names_a_project_owner_and_links_the_API_page_not_a_billing_administrator()
    {
        // Issue #148 review M1: the missing right is on the project, so the billing-administrator request is the wrong action.
        _budgets.SaveAsync(Project, 50m, Arg.Any<CancellationToken>()).Returns<Task<BudgetInfo>>(_ => throw Failure(CloudErrorKind.Permission, BudgetErrorCodes.ApiEnablePermission));
        var vm = Open();

        await vm.SaveCommand.ExecuteAsync(null);

        vm.ErrorText.ShouldBe("BudgetError_ApiEnablePermission");
        vm.ConsoleLink!.ToString().ShouldBe(BudgetLinks.ForApi(Project));
        vm.ShowCopyRequest.ShouldBeFalse();
    }

    [Fact]
    public async Task A_refused_remove_says_it_could_not_remove_and_does_not_offer_the_create_request()
    {
        // Issue #148 review L5.
        _budgets.GetAsync(Project, Arg.Any<CancellationToken>()).Returns(Existing(50m));
        _budgets.RemoveAsync(Project, Arg.Any<CancellationToken>()).Returns<Task<bool>>(_ => throw Failure(CloudErrorKind.Permission));
        var vm = Open();
        await vm.RefreshCommand.ExecuteAsync(null);

        await vm.RemoveCommand.ExecuteAsync(null);

        vm.ErrorText.ShouldBe("BudgetError_PermissionRemove");
        vm.ShowCopyRequest.ShouldBeFalse();
        vm.ConsoleLink!.ToString().ShouldBe("https://console.cloud.google.com/billing/AAA/budgets");
        vm.HasBudget.ShouldBeTrue();
    }

    [Fact]
    public async Task A_remove_that_found_nothing_to_remove_says_so_instead_of_removed()
    {
        // Issue #148 review L6.
        _budgets.GetAsync(Project, Arg.Any<CancellationToken>()).Returns(Existing(50m));
        _budgets.RemoveAsync(Project, Arg.Any<CancellationToken>()).Returns(false);
        var vm = Open();
        await vm.RefreshCommand.ExecuteAsync(null);

        await vm.RemoveCommand.ExecuteAsync(null);

        vm.StatusText.ShouldBe("BudgetRemovedNone");
        vm.HasBudget.ShouldBeFalse();
    }

    [Theory]
    [InlineData("abc", "50")]
    [InlineData("", "50")]
    [InlineData("75", "75")]
    [InlineData(" $12.5 ", "12.5")]
    public void The_copied_request_names_the_parsed_amount_or_the_default_never_the_raw_text(string typed, string expected)
    {
        // Issue #148 review L4. Invariant culture so "12.5" reads as 12.5 whatever the machine's culture.
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
        try
        {
            _strings.GetString("BudgetRequestText").Returns("{0}|{1}");
            _shell.CopyText(Arg.Any<string>()).Returns(true);
            var vm = Open();
            vm.AmountText = typed;

            vm.CopyRequestCommand.Execute(null);

            _shell.Received(1).CopyText(Project + "|" + expected);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public async Task A_budget_whose_amount_follows_last_months_spend_is_shown_without_an_amount_and_leaves_the_box_alone()
    {
        // Issue #148 review L3.
        _budgets.GetAsync(Project, Arg.Any<CancellationToken>()).Returns(Existing(0m) with { AmountKnown = false });
        var vm = Open();

        await vm.RefreshCommand.ExecuteAsync(null);

        vm.HasBudget.ShouldBeTrue();
        vm.StatusText.ShouldBe("BudgetStatusExistsNoAmount");
        vm.AmountText.ShouldBeEmpty("a budget with no fixed amount has nothing to show, and the default 50 would turn it into a fixed 50 on Save");
    }

    [Fact]
    public async Task Saving_with_the_box_empty_after_opening_a_follow_last_month_budget_asks_for_an_amount_and_changes_nothing()
    {
        _budgets.GetAsync(Project, Arg.Any<CancellationToken>()).Returns(Existing(0m) with { AmountKnown = false });
        var vm = Open();
        await vm.RefreshCommand.ExecuteAsync(null);

        await vm.SaveCommand.ExecuteAsync(null);

        vm.ErrorText.ShouldBe("BudgetAmountInvalid");
        await _budgets.DidNotReceive().SaveAsync(Arg.Any<string>(), Arg.Any<decimal>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Text_typed_before_the_first_look_finishes_survives_it()
    {
        // Issue #148 review round 4 (L): the project-change reset also ran on the first open and overwrote text typed before the look began.
        _budgets.GetAsync(Project, Arg.Any<CancellationToken>()).Returns(Existing(75m));
        var vm = Open();
        vm.AmountText = "30";

        await vm.RefreshCommand.ExecuteAsync(null);

        vm.AmountText.ShouldBe("30");
        vm.HasBudget.ShouldBeTrue();
    }

    [Fact]
    public async Task Switching_to_another_project_shows_that_projects_budget_even_after_typing_in_the_box()
    {
        // Issue #148 review L7: the "typed, so Google's late answer must not overwrite it" guard belongs to one project.
        _budgets.GetAsync(Project, Arg.Any<CancellationToken>()).Returns((BudgetInfo?)null);
        _budgets.GetAsync("other-lab", Arg.Any<CancellationToken>()).Returns(Existing(20m));
        var vm = Open();
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.AmountText = "99";

        _account.SelectedProjectId.Returns("other-lab");
        await vm.RefreshCommand.ExecuteAsync(null);

        vm.AmountText.ShouldBe(20m.ToString("0.##", System.Globalization.CultureInfo.CurrentCulture));
    }

    [Fact]
    public async Task Switching_to_a_project_with_no_budget_clears_the_amount_typed_for_the_other_one()
    {
        // Issue #148 review round 3 (L): text typed for A must never be saved as B's budget.
        _budgets.GetAsync(Project, Arg.Any<CancellationToken>()).Returns((BudgetInfo?)null);
        _budgets.GetAsync("other-lab", Arg.Any<CancellationToken>()).Returns((BudgetInfo?)null);
        var vm = Open();
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.AmountText = "99";

        _account.SelectedProjectId.Returns("other-lab");
        await vm.RefreshCommand.ExecuteAsync(null);

        vm.AmountText.ShouldBe("50", "the box goes back to the default instead of carrying the other project's typed amount");
    }

    [Fact]
    public async Task The_status_names_the_thresholds_the_budget_really_has_and_does_not_say_Google_emails_you()
    {
        // Issue #148 review round 3 (L): the app sets no notification rule, so the mail goes to the billing administrators; the thresholds come from the budget.
        _strings.GetString("BudgetStatusExists").Returns("with:{0}|{1}");
        _strings.GetString("BudgetStatusExistsNoAmount").Returns("noamount:{0}");
        _budgets.GetAsync(Project, Arg.Any<CancellationToken>()).Returns(Existing(75m) with { ThresholdPercents = [25, 75] });
        var vm = Open();

        await vm.RefreshCommand.ExecuteAsync(null);

        vm.StatusText.ShouldBe("with:" + 75m.ToString("0.##", System.Globalization.CultureInfo.CurrentCulture) + "|i25&i75");

        _budgets.GetAsync(Project, Arg.Any<CancellationToken>()).Returns(Existing(0m) with { AmountKnown = false, ThresholdPercents = [50, 90, 100] });
        await vm.RefreshCommand.ExecuteAsync(null);

        vm.StatusText.ShouldBe("noamount:i50+i90&i100");
    }

    [Fact]
    public async Task One_threshold_is_just_the_item_phrase_with_no_joiner()
    {
        _strings.GetString("BudgetStatusExistsNoAmount").Returns("noamount:{0}");
        _budgets.GetAsync(Project, Arg.Any<CancellationToken>()).Returns(Existing(0m) with { AmountKnown = false, ThresholdPercents = [12.5] });
        var vm = Open();

        await vm.RefreshCommand.ExecuteAsync(null);

        vm.StatusText.ShouldBe("noamount:i" + 12.5.ToString("0.##", System.Globalization.CultureInfo.CurrentCulture));
    }

    [Fact]
    public async Task A_budget_with_no_thresholds_says_so_instead_of_naming_percentages()
    {
        _budgets.GetAsync(Project, Arg.Any<CancellationToken>()).Returns(Existing(75m) with { ThresholdPercents = [] });
        var vm = Open();

        await vm.RefreshCommand.ExecuteAsync(null);

        vm.StatusText.ShouldBe("BudgetStatusNoThresholds");
    }

    [Fact]
    public async Task A_saved_budget_reports_its_own_thresholds_not_a_fixed_three()
    {
        _strings.GetString("BudgetSaved").Returns("saved:{0}|{1}");
        _budgets.SaveAsync(Project, 120m, Arg.Any<CancellationToken>()).Returns(Existing(120m));
        var vm = Open();
        vm.AmountText = "120";

        await vm.SaveCommand.ExecuteAsync(null);

        vm.StatusText.ShouldBe("saved:120|i50+i90&i100");
    }

    [Fact]
    public async Task Refreshing_the_same_project_keeps_what_the_user_typed()
    {
        _budgets.GetAsync(Project, Arg.Any<CancellationToken>()).Returns(Existing(20m));
        var vm = Open();
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.AmountText = "99";

        await vm.RefreshCommand.ExecuteAsync(null);

        vm.AmountText.ShouldBe("99");
    }
}
