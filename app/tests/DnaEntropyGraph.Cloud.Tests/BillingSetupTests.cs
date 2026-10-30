using DnaEntropyGraph.Core.Cloud;
using NSubstitute;
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
    public async Task A_link_that_did_not_turn_billing_on_with_no_other_account_says_to_fix_that_account_not_to_pick_another()
    {
        var gcp = new FakeGcp().WithBillingOff(Project).WithBillingAccount("billingAccounts/AAA", "Lab card").WithBillingLinkThatDoesNotEnable();

        var outcome = await new BillingSetup(gcp).EnsureAsync(Project, CancellationToken.None);

        outcome.Kind.ShouldBe(BillingOutcomeKind.FixLinkedAccount);
        outcome.Code.ShouldBe(SetupErrorCodes.BillingAccountOff);
        outcome.Code.ShouldNotBe(SetupErrorCodes.NoBilling);
        outcome.Code.ShouldNotBe(SetupErrorCodes.BillingStillOff);
        outcome.AccountId.ShouldBe("billingAccounts/AAA");
        outcome.DeepLink.ShouldBe(BillingLinks.ForProject(Project));
    }

    [Fact]
    public async Task A_link_that_did_not_turn_billing_on_with_other_accounts_offers_exactly_those_others()
    {
        var gcp = new FakeGcp().WithBillingOff(Project).WithBillingAccount("billingAccounts/AAA", "Lab card").WithBillingAccount("billingAccounts/BBB", "Department").WithBillingLinkThatDoesNotEnable();

        var outcome = await new BillingSetup(gcp).LinkAsync(Project, "billingAccounts/AAA", CancellationToken.None);

        outcome.Kind.ShouldBe(BillingOutcomeKind.LinkedButStillOff);
        outcome.Code.ShouldBe(SetupErrorCodes.BillingStillOff);
        outcome.Accounts.Select(a => a.AccountId).ShouldBe(["billingAccounts/BBB"]);
    }

    [Fact]
    public async Task Calling_again_with_one_suspended_account_never_links_it_again_and_keeps_naming_the_fix()
    {
        var gcp = new FakeGcp().WithBillingOff(Project).WithBillingAccount("billingAccounts/AAA", "Lab card").WithBillingLinkThatDoesNotEnable();
        var setup = new BillingSetup(gcp);

        var first = await setup.EnsureAsync(Project, CancellationToken.None);
        var second = await setup.EnsureAsync(Project, CancellationToken.None);
        var third = await setup.EnsureAsync(Project, CancellationToken.None);

        gcp.BillingLinkCalls.ShouldBe(1, "the same suspended account was linked again by every re-check");
        new[] { first, second, third }.ShouldAllBe(o => o.Kind == BillingOutcomeKind.FixLinkedAccount && o.Code == SetupErrorCodes.BillingAccountOff && o.DeepLink == BillingLinks.ForProject(Project));
    }

    [Fact]
    public async Task A_project_whose_linked_account_is_off_asks_before_replacing_it_when_other_accounts_exist()
    {
        var gcp = new FakeGcp().WithBillingOff(Project).WithBillingAccount("billingAccounts/AAA", "Lab card").WithBillingAccount("billingAccounts/BBB", "Department").WithBillingLinkThatDoesNotEnable();
        var setup = new BillingSetup(gcp);
        await setup.LinkAsync(Project, "billingAccounts/AAA", CancellationToken.None);

        var outcome = await setup.EnsureAsync(Project, CancellationToken.None);

        outcome.Kind.ShouldBe(BillingOutcomeKind.ChooseAccount);
        outcome.Accounts.Select(a => a.AccountId).ShouldBe(["billingAccounts/BBB"]);
        outcome.AccountId.ShouldBe("billingAccounts/AAA");
        gcp.BillingLinkCalls.ShouldBe(1, "the user's existing link was replaced without asking");
    }

    [Fact]
    public async Task A_linked_but_off_account_that_is_no_longer_in_the_open_list_is_still_not_silently_replaced_by_the_only_open_one()
    {
        // The project is linked to a closed account (absent from the open list); one open account exists. Ask, do not link.
        var gcp = new FakeGcp().WithBillingOff(Project).WithBillingAccount("billingAccounts/OPEN", "Open one").WithBillingLinkThatDoesNotEnable();
        await gcp.LinkProjectAsync(Project, "billingAccounts/CLOSED", CancellationToken.None);

        var outcome = await new BillingSetup(gcp).EnsureAsync(Project, CancellationToken.None);

        outcome.Kind.ShouldBe(BillingOutcomeKind.ChooseAccount);
        outcome.Accounts.Select(a => a.AccountId).ShouldBe(["billingAccounts/OPEN"]);
        gcp.BillingLinkCalls.ShouldBe(1);
    }

    [Fact]
    public async Task A_linked_but_off_project_with_other_accounts_carries_the_BILLING_STILL_OFF_code_but_a_fresh_choice_carries_none()
    {
        var linkedOff = new FakeGcp().WithBillingOff(Project).WithBillingAccount("billingAccounts/AAA", "Lab card").WithBillingAccount("billingAccounts/BBB", "Department").WithBillingLinkThatDoesNotEnable();
        var setup = new BillingSetup(linkedOff);
        await setup.LinkAsync(Project, "billingAccounts/AAA", CancellationToken.None);

        var again = await setup.EnsureAsync(Project, CancellationToken.None);

        again.Kind.ShouldBe(BillingOutcomeKind.ChooseAccount);
        again.Code.ShouldBe(SetupErrorCodes.BillingStillOff);

        var fresh = new FakeGcp().WithBillingOff(Project).WithBillingAccount("billingAccounts/AAA", "Lab card").WithBillingAccount("billingAccounts/BBB", "Department");
        var choose = await new BillingSetup(fresh).EnsureAsync(Project, CancellationToken.None);

        choose.Kind.ShouldBe(BillingOutcomeKind.ChooseAccount);
        choose.Code.ShouldBeNull("nothing is wrong when the user simply has several accounts to pick from");
    }

    [Fact]
    public async Task A_failure_listing_accounts_after_a_link_that_worked_returns_FixLinkedAccount_instead_of_throwing()
    {
        var gateway = Substitute.For<IBillingGateway>();
        gateway.GetBillingStatusAsync(Project, Arg.Any<CancellationToken>()).Returns(new BillingStatus(false, "billingAccounts/AAA"));
        gateway.ListOpenBillingAccountsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IReadOnlyList<BillingAccountSummary>>(new CloudOperationException(new CloudError("NETWORK", 0, "offline"), CloudErrorKind.Network)));

        var outcome = await new BillingSetup(gateway).LinkAsync(Project, "billingAccounts/AAA", CancellationToken.None);

        await gateway.Received(1).LinkProjectAsync(Project, "billingAccounts/AAA", Arg.Any<CancellationToken>());
        outcome.Kind.ShouldBe(BillingOutcomeKind.FixLinkedAccount);
        outcome.Code.ShouldBe(SetupErrorCodes.BillingAccountOff);
        outcome.Accounts.ShouldBeEmpty();
        outcome.DeepLink.ShouldBe(BillingLinks.ForProject(Project));
    }

    [Fact]
    public async Task Cancelling_during_the_after_link_listing_still_cancels()
    {
        var gateway = Substitute.For<IBillingGateway>();
        gateway.GetBillingStatusAsync(Project, Arg.Any<CancellationToken>()).Returns(new BillingStatus(false, "billingAccounts/AAA"));
        gateway.ListOpenBillingAccountsAsync(Arg.Any<CancellationToken>()).Returns(Task.FromException<IReadOnlyList<BillingAccountSummary>>(new OperationCanceledException()));

        await Should.ThrowAsync<OperationCanceledException>(() => new BillingSetup(gateway).LinkAsync(Project, "billingAccounts/AAA", CancellationToken.None));
    }

    [Theory]
    [InlineData(CloudErrorKind.Permission, SetupErrorCodes.BillingNoPermission)]
    [InlineData(CloudErrorKind.Billing, "BILLING")]
    [InlineData(CloudErrorKind.ApiDisabled, "API_DISABLED")]
    [InlineData(CloudErrorKind.OrgPolicy, "ORG_POLICY_BLOCK")]
    public async Task An_abort_class_failure_listing_accounts_after_a_link_that_worked_propagates_and_is_not_read_as_a_suspended_account(CloudErrorKind kind, string code)
    {
        var gateway = Substitute.For<IBillingGateway>();
        gateway.GetBillingStatusAsync(Project, Arg.Any<CancellationToken>()).Returns(new BillingStatus(false, "billingAccounts/AAA"));
        gateway.ListOpenBillingAccountsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IReadOnlyList<BillingAccountSummary>>(new CloudOperationException(new CloudError(code, 403, "denied"), kind)));

        var ex = await Should.ThrowAsync<CloudOperationException>(() => new BillingSetup(gateway).LinkAsync(Project, "billingAccounts/AAA", CancellationToken.None));

        ex.Kind.ShouldBe(kind);
        ex.Error.Code.ShouldBe(code);
    }

    [Theory]
    [InlineData(CloudErrorKind.Network)]
    [InlineData(CloudErrorKind.Other)]
    public async Task A_transient_or_unknown_failure_listing_accounts_after_a_link_that_worked_still_returns_FixLinkedAccount(CloudErrorKind kind)
    {
        var gateway = Substitute.For<IBillingGateway>();
        gateway.GetBillingStatusAsync(Project, Arg.Any<CancellationToken>()).Returns(new BillingStatus(false, "billingAccounts/AAA"));
        gateway.ListOpenBillingAccountsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IReadOnlyList<BillingAccountSummary>>(new CloudOperationException(new CloudError("X", 0, "x"), kind)));

        var outcome = await new BillingSetup(gateway).LinkAsync(Project, "billingAccounts/AAA", CancellationToken.None);

        outcome.Kind.ShouldBe(BillingOutcomeKind.FixLinkedAccount);
    }
}