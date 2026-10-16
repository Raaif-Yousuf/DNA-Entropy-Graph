using System.Text.Json;
using DnaEntropyGraph.Core.Cloud;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Cloud.Tests.Rest;

/// <summary>
/// Issues #148 and #222: the real Cloud Billing Budgets v1 gateway over a scripted HTTP handler. Payloads are Google's documented
/// REST shapes (billingAccounts.budgets list, create, patch, delete, google.rpc.Status errors), not captured from a live account:
/// there is no GCP access in this build (docs/ToTest.md carries the real-account row). The request is the decisive observable.
/// </summary>
public class GoogleBudgetGatewayTests
{
    private const string Account = "billingAccounts/0A1B2C-3D4E5F-6A7B8C";
    private const string ListPath = "/v1/billingAccounts/0A1B2C-3D4E5F-6A7B8C/budgets";
    private const string BudgetPath = "/v1/billingAccounts/0A1B2C-3D4E5F-6A7B8C/budgets/b-1";

    private static readonly HttpMethod Get = HttpMethod.Get;
    private static readonly HttpMethod Post = HttpMethod.Post;
    private static readonly HttpMethod Patch = HttpMethod.Patch;
    private static readonly HttpMethod Delete = HttpMethod.Delete;

    private static string RpcError(int http, string status, string message)
        => "{\"error\":{\"code\":" + http + ",\"message\":" + JsonSerializer.Serialize(message) + ",\"status\":\"" + status + "\"}}";

    private static string BudgetJson(string name, string displayName, string units, string projects = "projects/555")
        => "{\"name\":\"" + name + "\",\"displayName\":\"" + displayName + "\",\"budgetFilter\":{\"projects\":[\"" + projects + "\"]},"
           + "\"amount\":{\"specifiedAmount\":{\"currencyCode\":\"USD\",\"units\":\"" + units + "\"}},"
           + "\"thresholdRules\":[{\"thresholdPercent\":0.5,\"spendBasis\":\"CURRENT_SPEND\"},{\"thresholdPercent\":0.9,\"spendBasis\":\"CURRENT_SPEND\"},{\"thresholdPercent\":1,\"spendBasis\":\"CURRENT_SPEND\"}]}";

    private static string ListJson(string budgets, string? next = null)
        => "{\"budgets\":[" + budgets + "]" + (next is null ? string.Empty : ",\"nextPageToken\":\"" + next + "\"") + "}";

    private static void ProjectNumber(GoogleGatewayHarness rig)
        => rig.Handler.Returns(Get, "/v3/projects/my-lab", 200, """{"name":"projects/555","projectId":"my-lab","state":"ACTIVE","displayName":"My Lab"}""");

    [Fact]
    public async Task Finding_reads_the_accounts_budgets_and_returns_only_this_installations()
    {
        var rig = new GoogleGatewayHarness();
        rig.Handler.Returns(Get, ListPath, 200, ListJson(
            BudgetJson(Account + "/budgets/other", "DNA Entropy Graph inst-b", "20") + "," + BudgetJson(Account + "/budgets/b-1", "DNA Entropy Graph inst-a", "50")));

        var found = await rig.Gateways.Budget.FindAsync(Account, "inst-a", CancellationToken.None);

        found.ShouldNotBeNull();
        found.Name.ShouldBe(Account + "/budgets/b-1");
        found.AmountUsd.ShouldBe(50);
        found.ThresholdPercents.ShouldBe([50, 90, 100]);
        var call = rig.Handler.To(Get, ListPath).Single();
        call.Uri.Host.ShouldBe("billingbudgets.googleapis.com");
        call.Authorization.ShouldBe("Bearer " + rig.Tokens.CurrentToken);
        // Without it Google bills the call to the app's own project, where the API is not on (the user's project is the quota project).
        call.UserProject.ShouldBe("my-lab");
    }

    [Fact]
    public async Task Finding_follows_the_page_token_and_answers_none_when_no_budget_is_ours()
    {
        var rig = new GoogleGatewayHarness();
        rig.Handler
            .Returns(Get, ListPath, 200, ListJson(BudgetJson(Account + "/budgets/other", "Someone else's budget", "20"), "p2"))
            .Returns(Get, ListPath, 200, ListJson(BudgetJson(Account + "/budgets/b-1", "DNA Entropy Graph inst-a", "50")));

        (await rig.Gateways.Budget.FindAsync(Account, "inst-a", CancellationToken.None)).ShouldNotBeNull();

        var calls = rig.Handler.To(Get, ListPath);
        calls.Count.ShouldBe(2);
        calls[1].Uri.Query.ShouldContain("pageToken=p2");
    }

    [Fact]
    public async Task Creating_sends_the_name_amount_project_and_the_three_thresholds_and_no_notification_rule()
    {
        var rig = new GoogleGatewayHarness();
        ProjectNumber(rig);
        rig.Handler
            .Returns(Get, ListPath, 200, ListJson(string.Empty))
            .Returns(Post, ListPath, 200, BudgetJson(Account + "/budgets/b-1", "DNA Entropy Graph inst-a", "50"));

        var saved = await rig.Gateways.Budget.UpsertAsync(new BudgetSpec(Account, "my-lab", "inst-a", 50), CancellationToken.None);

        saved.Name.ShouldBe(Account + "/budgets/b-1");
        var request = rig.Handler.To(Post, ListPath).Single();
        request.UserProject.ShouldBe("my-lab");
        using var body = JsonDocument.Parse(request.Body);
        var root = body.RootElement;
        root.GetProperty("displayName").GetString().ShouldBe("DNA Entropy Graph inst-a");
        root.GetProperty("budgetFilter").GetProperty("projects").EnumerateArray().Select(e => e.GetString()).ShouldBe(["projects/555"]);
        var amount = root.GetProperty("amount").GetProperty("specifiedAmount");
        amount.GetProperty("units").GetInt64().ShouldBe(50);
        // Left unset: Google then uses the billing account's own currency; a wrong one is refused.
        amount.TryGetProperty("currencyCode", out _).ShouldBeFalse();
        root.GetProperty("thresholdRules").EnumerateArray().Select(r => r.GetProperty("thresholdPercent").GetDouble()).ShouldBe([0.5, 0.9, 1.0]);
        root.GetProperty("thresholdRules").EnumerateArray().Select(r => r.GetProperty("spendBasis").GetString()).ShouldAllBe(s => s == "CURRENT_SPEND");
        // No notificationsRule: the billing administrators and users are then emailed by default.
        root.TryGetProperty("notificationsRule", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task A_fractional_amount_is_sent_as_units_and_nanos()
    {
        var rig = new GoogleGatewayHarness();
        ProjectNumber(rig);
        rig.Handler
            .Returns(Get, ListPath, 200, ListJson(string.Empty))
            .Returns(Post, ListPath, 200, BudgetJson(Account + "/budgets/b-1", "DNA Entropy Graph inst-a", "12"));

        await rig.Gateways.Budget.UpsertAsync(new BudgetSpec(Account, "my-lab", "inst-a", 12.5), CancellationToken.None);

        using var body = JsonDocument.Parse(rig.Handler.To(Post, ListPath).Single().Body);
        var amount = body.RootElement.GetProperty("amount").GetProperty("specifiedAmount");
        amount.GetProperty("units").GetInt64().ShouldBe(12);
        amount.GetProperty("nanos").GetInt32().ShouldBe(500_000_000);
    }

    [Fact]
    public async Task Upserting_over_an_existing_budget_patches_it_and_never_posts_a_second()
    {
        var rig = new GoogleGatewayHarness();
        ProjectNumber(rig);
        rig.Handler
            .Returns(Get, ListPath, 200, ListJson(BudgetJson(Account + "/budgets/b-1", "DNA Entropy Graph inst-a", "50")))
            .Returns(Patch, BudgetPath, 200, BudgetJson(Account + "/budgets/b-1", "DNA Entropy Graph inst-a", "120"));

        var saved = await rig.Gateways.Budget.UpsertAsync(new BudgetSpec(Account, "my-lab", "inst-a", 120), CancellationToken.None);

        saved.AmountUsd.ShouldBe(120);
        rig.Handler.To(Post, ListPath).ShouldBeEmpty();
        var patch = rig.Handler.To(Patch, BudgetPath).Single();
        Uri.UnescapeDataString(patch.Uri.Query).ShouldContain("updateMask=amount,budgetFilter,thresholdRules");
        using var body = JsonDocument.Parse(patch.Body);
        body.RootElement.GetProperty("amount").GetProperty("specifiedAmount").GetProperty("units").GetInt64().ShouldBe(120);
    }

    [Fact]
    public async Task A_user_who_is_not_a_billing_administrator_gets_a_permission_error()
    {
        var rig = new GoogleGatewayHarness();
        ProjectNumber(rig);
        rig.Handler
            .Returns(Get, ListPath, 403, RpcError(403, "PERMISSION_DENIED", "The caller does not have permission"));

        var ex = await Should.ThrowAsync<CloudOperationException>(() => rig.Gateways.Budget.UpsertAsync(new BudgetSpec(Account, "my-lab", "inst-a", 50), CancellationToken.None));

        ex.Kind.ShouldBe(CloudErrorKind.Permission);
        rig.Handler.To(Post, ListPath).ShouldBeEmpty();
    }

    [Fact]
    public async Task The_Budgets_API_being_off_is_classified_as_api_disabled()
    {
        var rig = new GoogleGatewayHarness();
        rig.Handler.Returns(Get, ListPath, 403, """
            {"error":{"code":403,"message":"Cloud Billing Budget API has not been used in project 1 before or it is disabled.","status":"PERMISSION_DENIED",
            "details":[{"@type":"type.googleapis.com/google.rpc.ErrorInfo","reason":"SERVICE_DISABLED","domain":"googleapis.com"}]}}
            """);

        var ex = await Should.ThrowAsync<CloudOperationException>(() => rig.Gateways.Budget.FindAsync(Account, "inst-a", CancellationToken.None));

        ex.Kind.ShouldBe(CloudErrorKind.ApiDisabled);
    }

    [Fact]
    public async Task Deleting_sends_a_delete_and_a_budget_that_is_already_gone_is_not_an_error()
    {
        var rig = new GoogleGatewayHarness();
        rig.Handler
            .Returns(Delete, BudgetPath, 200, "{}")
            .Returns(Delete, BudgetPath, 404, RpcError(404, "NOT_FOUND", "Budget not found"));

        await rig.Gateways.Budget.DeleteAsync(Account + "/budgets/b-1", CancellationToken.None);
        await rig.Gateways.Budget.DeleteAsync(Account + "/budgets/b-1", CancellationToken.None);

        rig.Handler.To(Delete, BudgetPath).Count.ShouldBe(2);
    }
}
