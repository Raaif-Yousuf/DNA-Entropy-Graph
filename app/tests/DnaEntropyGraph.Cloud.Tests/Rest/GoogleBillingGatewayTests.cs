using System.Text.Json;
using DnaEntropyGraph.Core.Cloud;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Cloud.Tests.Rest;

/// <summary>
/// Issue #51: the real Cloud Billing v1 gateway over a scripted HTTP handler. Payloads are Google's documented REST
/// shapes (projects.getBillingInfo, billingAccounts.list, projects.updateBillingInfo, google.rpc.Status errors), not
/// captured from a live account: there is no GCP access in this build. The request is the decisive observable.
/// </summary>
public class GoogleBillingGatewayTests
{
    private static readonly HttpMethod Get = HttpMethod.Get;
    private static readonly HttpMethod Put = HttpMethod.Put;

    private static string RpcError(int http, string status, string message)
        => "{\"error\":{\"code\":" + http + ",\"message\":" + JsonSerializer.Serialize(message) + ",\"status\":\"" + status + "\"}}";

    [Fact]
    public async Task Billing_status_reads_the_projects_billing_info_with_the_bearer_token()
    {
        var rig = new GoogleGatewayHarness();
        rig.Handler.Returns(Get, "/v1/projects/my-lab/billingInfo", 200, """{"name":"projects/my-lab/billingInfo","projectId":"my-lab","billingAccountName":"billingAccounts/0A1B2C-3D4E5F-6A7B8C","billingEnabled":true}""");

        var status = await rig.Gateways.Billing.GetBillingStatusAsync("my-lab", CancellationToken.None);

        status.Enabled.ShouldBeTrue();
        status.AccountId.ShouldBe("billingAccounts/0A1B2C-3D4E5F-6A7B8C");
        var call = rig.Handler.To(Get, "/v1/projects/my-lab/billingInfo").Single();
        call.Uri.Host.ShouldBe("cloudbilling.googleapis.com");
        call.Authorization.ShouldBe("Bearer " + rig.Tokens.CurrentToken);
    }

    [Fact]
    public async Task A_project_with_no_billing_account_reads_as_disabled_because_Google_omits_the_false()
    {
        var rig = new GoogleGatewayHarness();
        rig.Handler.Returns(Get, "/v1/projects/my-lab/billingInfo", 200, """{"name":"projects/my-lab/billingInfo","projectId":"my-lab"}""");

        var status = await rig.Gateways.Billing.GetBillingStatusAsync("my-lab", CancellationToken.None);

        status.Enabled.ShouldBeFalse();
        status.AccountId.ShouldBeNull();
    }

    [Fact]
    public async Task A_linked_account_that_is_switched_off_reads_as_disabled()
    {
        var rig = new GoogleGatewayHarness();
        rig.Handler.Returns(Get, "/v1/projects/my-lab/billingInfo", 200, """{"name":"projects/my-lab/billingInfo","projectId":"my-lab","billingAccountName":"billingAccounts/AAA","billingEnabled":false}""");

        (await rig.Gateways.Billing.GetBillingStatusAsync("my-lab", CancellationToken.None)).Enabled.ShouldBeFalse();
    }

    [Fact]
    public async Task Listing_asks_for_open_accounts_only_and_follows_the_page_token()
    {
        var rig = new GoogleGatewayHarness();
        rig.Handler
            .Returns(Get, "/v1/billingAccounts", 200, """{"billingAccounts":[{"name":"billingAccounts/AAA","open":true,"displayName":"Lab card"}],"nextPageToken":"p2"}""")
            .Returns(Get, "/v1/billingAccounts", 200, """{"billingAccounts":[{"name":"billingAccounts/BBB","open":true,"displayName":"Department"}]}""");

        var accounts = await rig.Gateways.Billing.ListOpenBillingAccountsAsync(CancellationToken.None);

        var calls = rig.Handler.To(Get, "/v1/billingAccounts");
        calls.Count.ShouldBe(2);
        Uri.UnescapeDataString(calls[0].Uri.Query).ShouldContain("filter=open=true");
        calls[1].Uri.Query.ShouldContain("pageToken=p2");
        calls.ShouldAllBe(c => c.Authorization == "Bearer " + rig.Tokens.CurrentToken);
        accounts.Select(a => (a.AccountId, a.DisplayName)).ShouldBe([("billingAccounts/AAA", "Lab card"), ("billingAccounts/BBB", "Department")]);
    }

    [Fact]
    public async Task An_account_that_is_not_open_is_never_offered_even_if_Google_returns_it()
    {
        var rig = new GoogleGatewayHarness();
        rig.Handler.Returns(Get, "/v1/billingAccounts", 200, """{"billingAccounts":[{"name":"billingAccounts/OLD","open":false,"displayName":"Closed"},{"name":"billingAccounts/NEW","open":true,"displayName":"Open"}]}""");

        var accounts = await rig.Gateways.Billing.ListOpenBillingAccountsAsync(CancellationToken.None);

        accounts.Select(a => a.AccountId).ShouldBe(["billingAccounts/NEW"]);
    }

    [Fact]
    public async Task A_user_with_no_billing_accounts_gets_an_empty_list()
    {
        var rig = new GoogleGatewayHarness();
        rig.Handler.Returns(Get, "/v1/billingAccounts", 200, "{}");

        (await rig.Gateways.Billing.ListOpenBillingAccountsAsync(CancellationToken.None)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Linking_puts_the_account_name_on_the_projects_billing_info()
    {
        var rig = new GoogleGatewayHarness();
        rig.Handler.Returns(Put, "/v1/projects/my-lab/billingInfo", 200, """{"name":"projects/my-lab/billingInfo","projectId":"my-lab","billingAccountName":"billingAccounts/AAA","billingEnabled":true}""");

        await rig.Gateways.Billing.LinkProjectAsync("my-lab", "billingAccounts/AAA", CancellationToken.None);

        var call = rig.Handler.To(Put, "/v1/projects/my-lab/billingInfo").Single();
        call.Authorization.ShouldBe("Bearer " + rig.Tokens.CurrentToken);
        using var body = JsonDocument.Parse(call.Body);
        body.RootElement.GetProperty("billingAccountName").GetString().ShouldBe("billingAccounts/AAA");
    }

    [Fact]
    public async Task Linking_with_a_403_is_BILLING_NO_PERMISSION_and_is_not_retried()
    {
        var rig = new GoogleGatewayHarness();
        rig.Handler.Returns(Put, "/v1/projects/my-lab/billingInfo", 403, RpcError(403, "PERMISSION_DENIED", "The caller does not have permission billing.resourceAssociations.create"));

        var ex = await Should.ThrowAsync<CloudOperationException>(() => rig.Gateways.Billing.LinkProjectAsync("my-lab", "billingAccounts/AAA", CancellationToken.None));

        ex.Error.Code.ShouldBe(SetupErrorCodes.BillingNoPermission);
        ex.Kind.ShouldBe(CloudErrorKind.Permission);
        rig.Handler.To(Put, "/v1/projects/my-lab/billingInfo").Count.ShouldBe(1);
    }

    [Fact]
    public async Task Reading_billing_with_a_403_stays_a_plain_permission_error()
    {
        var rig = new GoogleGatewayHarness();
        rig.Handler.Returns(Get, "/v1/projects/my-lab/billingInfo", 403, RpcError(403, "PERMISSION_DENIED", "The caller does not have permission"));

        var ex = await Should.ThrowAsync<CloudOperationException>(() => rig.Gateways.Billing.GetBillingStatusAsync("my-lab", CancellationToken.None));

        ex.Error.Code.ShouldBe("PERMISSION_DENIED");
        ex.Kind.ShouldBe(CloudErrorKind.Permission);
    }

    [Fact]
    public async Task A_401_refreshes_the_token_and_replays_the_billing_call()
    {
        var rig = new GoogleGatewayHarness();
        rig.Handler
            .Returns(Get, "/v1/projects/my-lab/billingInfo", 401, RpcError(401, "UNAUTHENTICATED", "Request had invalid authentication credentials."))
            .Returns(Get, "/v1/projects/my-lab/billingInfo", 200, """{"projectId":"my-lab","billingAccountName":"billingAccounts/AAA","billingEnabled":true}""");

        (await rig.Gateways.Billing.GetBillingStatusAsync("my-lab", CancellationToken.None)).Enabled.ShouldBeTrue();

        rig.Tokens.Refreshes.ShouldBe(1);
        rig.Handler.To(Get, "/v1/projects/my-lab/billingInfo").Select(c => c.Authorization).ShouldBe(["Bearer ya29.test-token-1", "Bearer ya29.test-token-2"]);
    }
}
