using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Cloud;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Cloud.Tests;

/// <summary>
/// Issues #148 and #222: the budget policy over <see cref="IBudgetGateway"/>, run against FakeGcp. The decisive observables are the
/// ones a wired-to-nothing budget would get wrong: the budget the fake holds (account, amount, thresholds, display name), the
/// Budgets API being switched on before the first write, and a refusal naming a kind the Settings page can turn into one action.
/// </summary>
public class BudgetServiceTests
{
    private const string Project = "my-lab";
    private const string Account = "billingAccounts/0A1B2C-3D4E5F-6A7B8C";

    private sealed class MemorySettings : ISettingsStore
    {
        public Dictionary<string, string> Values { get; } = new();

        public string? GetString(string key) => Values.TryGetValue(key, out var v) ? v : null;

        public void SetString(string key, string value) => Values[key] = value;
    }

    private static MemorySettings Installation(string id) => new() { Values = { [InstallationId.SettingsKey] = id } };

    private static async Task<FakeGcp> LinkedAsync(string project = Project, string account = Account, FakeGcp? gcp = null)
    {
        gcp ??= new FakeGcp();
        gcp.WithBillingOff(project).WithBillingAccount(account, "Lab card");
        await gcp.LinkProjectAsync(project, account, CancellationToken.None);
        return gcp;
    }

    private static BudgetService Service(FakeGcp gcp, string installation = "inst-a", List<TimeSpan>? delays = null)
        => new(gcp, gcp, gcp, Installation(installation), (span, _) =>
        {
            delays?.Add(span);
            return Task.CompletedTask;
        });

    [Fact]
    public async Task Saving_creates_one_budget_on_the_projects_billing_account_with_the_standard_thresholds()
    {
        var gcp = await LinkedAsync();

        var saved = await Service(gcp).SaveAsync(Project, 75m, CancellationToken.None);

        saved.AmountUsd.ShouldBe(75m);
        saved.BillingAccountId.ShouldBe(Account);
        saved.DisplayName.ShouldBe("DNA Entropy Graph inst-a my-lab");
        saved.ThresholdPercents.ShouldBe([50, 90, 100]);
        gcp.Budgets.Count.ShouldBe(1);
        gcp.BudgetProjectOf(saved.Name).ShouldBe(Project);
    }

    [Fact]
    public async Task Saving_twice_changes_the_amount_and_never_makes_a_second_budget()
    {
        var gcp = await LinkedAsync();
        var service = Service(gcp);

        var first = await service.SaveAsync(Project, 50m, CancellationToken.None);
        var second = await service.SaveAsync(Project, 120m, CancellationToken.None);

        gcp.Budgets.Count.ShouldBe(1);
        second.Name.ShouldBe(first.Name);
        gcp.Budgets.Single().AmountUsd.ShouldBe(120m);
    }

    [Fact]
    public async Task The_amount_is_rounded_to_cents_before_it_is_sent()
    {
        var gcp = await LinkedAsync();

        var saved = await Service(gcp).SaveAsync(Project, 12.9999999996m, CancellationToken.None);

        saved.AmountUsd.ShouldBe(13m);
    }

    [Fact]
    public async Task Two_installations_on_one_billing_account_get_two_budgets_and_each_sees_only_its_own()
    {
        var gcp = await LinkedAsync();

        await Service(gcp, "inst-a").SaveAsync(Project, 50m, CancellationToken.None);
        await Service(gcp, "inst-b").SaveAsync(Project, 20m, CancellationToken.None);

        gcp.Budgets.Count.ShouldBe(2);
        (await Service(gcp, "inst-a").GetAsync(Project, CancellationToken.None))!.AmountUsd.ShouldBe(50m);
        (await Service(gcp, "inst-b").GetAsync(Project, CancellationToken.None))!.AmountUsd.ShouldBe(20m);
    }

    [Fact]
    public async Task Two_projects_on_one_billing_account_get_their_own_budgets_and_never_see_or_take_each_others()
    {
        var gcp = await LinkedAsync();
        await LinkedAsync("other-lab", Account, gcp);
        var service = Service(gcp);

        await service.SaveAsync(Project, 50m, CancellationToken.None);
        (await service.GetAsync("other-lab", CancellationToken.None)).ShouldBeNull();

        await service.SaveAsync("other-lab", 20m, CancellationToken.None);

        gcp.Budgets.Count.ShouldBe(2);
        (await service.GetAsync(Project, CancellationToken.None))!.AmountUsd.ShouldBe(50m);
        (await service.GetAsync("other-lab", CancellationToken.None))!.AmountUsd.ShouldBe(20m);
        gcp.BudgetProjectOf((await service.GetAsync(Project, CancellationToken.None))!.Name).ShouldBe(Project);

        (await service.RemoveAsync("other-lab", CancellationToken.None)).ShouldBeTrue();
        (await service.GetAsync(Project, CancellationToken.None)).ShouldNotBeNull();
    }

    [Fact]
    public void The_display_name_fits_Googles_limit_for_a_full_length_installation_id_and_project_id()
    {
        var name = BudgetNaming.DisplayName(Guid.NewGuid().ToString("n"), new string('a', 30));

        name.Length.ShouldBeLessThanOrEqualTo(60);
    }

    [Fact]
    public async Task Saving_switches_the_Budgets_API_on_first_when_it_is_off()
    {
        var gcp = (await LinkedAsync()).WithServiceDisabled(Project, RequiredServices.BillingBudgets);

        await Service(gcp).SaveAsync(Project, 50m, CancellationToken.None);

        (await gcp.IsServiceEnabledAsync(Project, RequiredServices.BillingBudgets, CancellationToken.None)).ShouldBeTrue();
        gcp.Budgets.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_service_that_still_reads_as_off_just_after_enabling_is_retried_with_a_backoff_and_then_succeeds()
    {
        var gcp = (await LinkedAsync()).WithBudgetApiNotYetPropagated(2);
        var delays = new List<TimeSpan>();

        await Service(gcp, delays: delays).SaveAsync(Project, 50m, CancellationToken.None);

        gcp.Budgets.Count.ShouldBe(1);
        gcp.BudgetUpsertCalls.ShouldBe(3);
        delays.Count.ShouldBe(2);
        delays[1].ShouldBeGreaterThan(delays[0]);
    }

    [Fact]
    public async Task A_service_that_never_reads_as_on_gives_up_after_a_bounded_number_of_tries_and_says_api_disabled()
    {
        var gcp = (await LinkedAsync()).WithBudgetApiNotYetPropagated(100);
        var delays = new List<TimeSpan>();

        var ex = await Should.ThrowAsync<CloudOperationException>(() => Service(gcp, delays: delays).SaveAsync(Project, 50m, CancellationToken.None));

        ex.Kind.ShouldBe(CloudErrorKind.ApiDisabled);
        gcp.BudgetUpsertCalls.ShouldBe(5);
        delays.ShouldBe([TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(16)]);
        gcp.Budgets.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_permission_error_is_never_retried()
    {
        var gcp = (await LinkedAsync()).WithBudgetPermissionDenied();
        var delays = new List<TimeSpan>();

        await Should.ThrowAsync<CloudOperationException>(() => Service(gcp, delays: delays).SaveAsync(Project, 50m, CancellationToken.None));

        gcp.BudgetUpsertCalls.ShouldBe(1);
        delays.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_project_with_no_billing_account_is_refused_before_anything_is_created()
    {
        var gcp = new FakeGcp().WithBillingOff(Project);

        var ex = await Should.ThrowAsync<CloudOperationException>(() => Service(gcp).SaveAsync(Project, 50m, CancellationToken.None));

        ex.Kind.ShouldBe(CloudErrorKind.Billing);
        ex.Error.Code.ShouldBe(BudgetErrorCodes.NoBilling);
        gcp.BudgetUpsertCalls.ShouldBe(0);
    }

    [Fact]
    public async Task A_linked_account_with_billing_switched_off_reads_as_no_billing()
    {
        var gcp = new FakeGcp().WithBillingOff(Project).WithBillingAccount(Account, "Lab card").WithBillingLinkThatDoesNotEnable();
        await gcp.LinkProjectAsync(Project, Account, CancellationToken.None);

        var ex = await Should.ThrowAsync<CloudOperationException>(() => Service(gcp).SaveAsync(Project, 50m, CancellationToken.None));

        ex.Error.Code.ShouldBe(BudgetErrorCodes.BillingDisabled);
        (await Service(gcp).GetAsync(Project, CancellationToken.None)).ShouldBeNull();
        gcp.BudgetUpsertCalls.ShouldBe(0);
    }

    [Fact]
    public async Task A_user_who_is_not_a_billing_administrator_gets_a_permission_error_and_no_budget()
    {
        var gcp = (await LinkedAsync()).WithBudgetPermissionDenied();

        var ex = await Should.ThrowAsync<CloudOperationException>(() => Service(gcp).SaveAsync(Project, 50m, CancellationToken.None));

        ex.Kind.ShouldBe(CloudErrorKind.Permission);
        gcp.Budgets.ShouldBeEmpty();
    }

    [Fact]
    public async Task Not_being_the_project_owner_surfaces_when_the_API_has_to_be_switched_on()
    {
        var gcp = (await LinkedAsync()).WithServiceDisabled(Project, RequiredServices.BillingBudgets).WithNotProjectOwner(Project);

        var ex = await Should.ThrowAsync<CloudOperationException>(() => Service(gcp).SaveAsync(Project, 50m, CancellationToken.None));

        ex.Kind.ShouldBe(CloudErrorKind.Permission);
        gcp.BudgetUpsertCalls.ShouldBe(0);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("0.4")]
    [InlineData("2000000")]
    public async Task An_unusable_amount_is_refused_without_a_cloud_call(string amount)
    {
        var gcp = await LinkedAsync();

        var ex = await Should.ThrowAsync<CloudOperationException>(() => Service(gcp).SaveAsync(Project, decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture), CancellationToken.None));

        ex.Error.Code.ShouldBe(BudgetErrorCodes.InvalidAmount);
        gcp.BudgetUpsertCalls.ShouldBe(0);
    }

    [Fact]
    public async Task Looking_when_the_Budgets_API_is_off_answers_none_without_asking_Google_for_a_list()
    {
        var gcp = (await LinkedAsync()).WithServiceDisabled(Project, RequiredServices.BillingBudgets);

        (await Service(gcp).GetAsync(Project, CancellationToken.None)).ShouldBeNull();

        gcp.BudgetFindCalls.ShouldBe(0);
    }

    [Fact]
    public async Task Looking_without_a_billing_account_answers_none()
    {
        var gcp = new FakeGcp().WithBillingOff(Project);

        (await Service(gcp).GetAsync(Project, CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task Looking_without_permission_to_list_budgets_says_so()
    {
        var gcp = (await LinkedAsync()).WithBudgetPermissionDenied();

        var ex = await Should.ThrowAsync<CloudOperationException>(() => Service(gcp).GetAsync(Project, CancellationToken.None));

        ex.Kind.ShouldBe(CloudErrorKind.Permission);
    }

    [Fact]
    public async Task Removing_deletes_this_installations_budget_only()
    {
        var gcp = await LinkedAsync();
        await Service(gcp, "inst-a").SaveAsync(Project, 50m, CancellationToken.None);
        await Service(gcp, "inst-b").SaveAsync(Project, 20m, CancellationToken.None);

        (await Service(gcp, "inst-a").RemoveAsync(Project, CancellationToken.None)).ShouldBeTrue();

        gcp.Budgets.Select(b => b.DisplayName).ShouldBe(["DNA Entropy Graph inst-b my-lab"]);
    }

    [Fact]
    public async Task Removing_when_there_is_no_budget_is_not_an_error()
    {
        var gcp = await LinkedAsync();

        (await Service(gcp).RemoveAsync(Project, CancellationToken.None)).ShouldBeFalse();
    }

    [Fact]
    public async Task The_console_link_opens_the_budgets_page_of_the_linked_account_or_the_billing_home()
    {
        var linked = await LinkedAsync();
        (await Service(linked).ConsoleLinkAsync(Project, CancellationToken.None)).ShouldBe("https://console.cloud.google.com/billing/0A1B2C-3D4E5F-6A7B8C/budgets");

        var unlinked = new FakeGcp().WithBillingOff(Project);
        (await Service(unlinked).ConsoleLinkAsync(Project, CancellationToken.None)).ShouldBe("https://console.cloud.google.com/billing");
    }
}
