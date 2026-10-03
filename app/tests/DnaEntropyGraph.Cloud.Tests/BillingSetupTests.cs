using DnaEntropyGraph.Core.Cloud;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Cloud.Tests;

/// <summary>
/// Issue #51: the wizard's billing step as pure policy over <see cref="IBillingGateway"/>, run against FakeGcp:
/// enabled, disabled with one account (linked for the user), disabled with several (the user chooses), disabled with
/// none (a link to Google's billing page, and the same call again is the re-check), and no permission to link.
/// </summary>
public class BillingSetupTests
{
    private const string Project = "my-lab";

    [Fact]
    public async Task A_project_with_billing_on_needs_nothing_and_links_nothing()
    {
        var gcp = new FakeGcp().WithBillingAccount("billingAccounts/AAA", "Lab card");

        var outcome = await new BillingSetup(gcp).EnsureAsync(Project, CancellationToken.None);

        outcome.Kind.ShouldBe(BillingOutcomeKind.AlreadyEnabled);
        gcp.BillingLinkCalls.ShouldBe(0);
    }

    [Fact]
    public async Task Billing_off_with_exactly_one_account_links_it_and_the_status_flips()
    {
        var gcp = new FakeGcp().WithBillingOff(Project).WithBillingAccount("billingAccounts/AAA", "Lab card");

        var outcome = await new BillingSetup(gcp).EnsureAsync(Project, CancellationToken.None);

        outcome.Kind.ShouldBe(BillingOutcomeKind.Linked);
        outcome.AccountId.ShouldBe("billingAccounts/AAA");
        gcp.BillingLinkCalls.ShouldBe(1);
        (await gcp.GetBillingStatusAsync(Project, CancellationToken.None)).Enabled.ShouldBeTrue();
        (await gcp.IsBillingEnabledAsync(Project, CancellationToken.None)).ShouldBeTrue();
    }

    [Fact]
    public async Task Billing_off_with_several_accounts_asks_the_user_to_choose_and_links_nothing()
    {
        var gcp = new FakeGcp().WithBillingOff(Project).WithBillingAccount("billingAccounts/AAA", "Lab card").WithBillingAccount("billingAccounts/BBB", "Department");

        var outcome = await new BillingSetup(gcp).EnsureAsync(Project, CancellationToken.None);

        outcome.Kind.ShouldBe(BillingOutcomeKind.ChooseAccount);
        outcome.Accounts.Select(a => a.AccountId).ShouldBe(["billingAccounts/AAA", "billingAccounts/BBB"]);
        gcp.BillingLinkCalls.ShouldBe(0);

        var linked = await new BillingSetup(gcp).LinkAsync(Project, "billingAccounts/BBB", CancellationToken.None);
        linked.Kind.ShouldBe(BillingOutcomeKind.Linked);
        linked.AccountId.ShouldBe("billingAccounts/BBB");
    }

    [Fact]
    public async Task Billing_off_with_no_account_gives_the_deep_link_and_the_same_call_again_is_the_recheck()
    {
        var gcp = new FakeGcp().WithBillingOff(Project);
        var setup = new BillingSetup(gcp);

        var first = await setup.EnsureAsync(Project, CancellationToken.None);

        first.Kind.ShouldBe(BillingOutcomeKind.NeedsAccount);
        first.DeepLink.ShouldBe(BillingLinks.ForProject(Project));
        first.Code.ShouldBe(SetupErrorCodes.NoBilling);

        // The user adds a payment method on Google's page and comes back.
        gcp.WithBillingAccount("billingAccounts/NEW", "New card");
        var second = await setup.EnsureAsync(Project, CancellationToken.None);

        second.Kind.ShouldBe(BillingOutcomeKind.Linked);
    }

    [Fact]
    public async Task No_permission_to_link_is_BILLING_NO_PERMISSION_with_the_request_text_inputs()
    {
        var gcp = new FakeGcp().WithBillingOff(Project).WithBillingAccount("billingAccounts/AAA", "Lab card").WithBillingLinkPermissionDenied();

        var ex = await Should.ThrowAsync<CloudOperationException>(() => new BillingSetup(gcp).EnsureAsync(Project, CancellationToken.None));

        ex.Error.Code.ShouldBe(SetupErrorCodes.BillingNoPermission);
        ex.Kind.ShouldBe(CloudErrorKind.Permission);
        (await gcp.GetBillingStatusAsync(Project, CancellationToken.None)).Enabled.ShouldBeFalse();
    }

    [Fact]
    public async Task A_link_that_did_not_turn_billing_on_is_reported_as_needing_an_account_not_as_success()
    {
        var gcp = new FakeGcp().WithBillingOff(Project).WithBillingAccount("billingAccounts/AAA", "Lab card").WithBillingLinkThatDoesNotEnable();

        var outcome = await new BillingSetup(gcp).EnsureAsync(Project, CancellationToken.None);

        outcome.Kind.ShouldBe(BillingOutcomeKind.NeedsAccount);
    }
}
