using DnaEntropyGraph.Core.Cloud;
using Google;
using Google.Apis.CloudBillingBudget.v1;
using Google.Apis.CloudResourceManager.v3;
using BudgetData = Google.Apis.CloudBillingBudget.v1.Data;

namespace DnaEntropyGraph.Cloud.Rest;

/// <summary>
/// The real <see cref="IBudgetGateway"/> over Cloud Billing Budget v1 REST (issues #148 and #222): <c>billingAccounts.budgets.list</c>,
/// <c>create</c>, <c>patch</c> and <c>delete</c>. Each HTTP call goes through <see cref="CloudCallPipeline"/> on its own (so the
/// gateway is never wrapped: a whole-method retry around list-then-create could make two budgets), and the create is attempted once.
/// Every request carries <c>x-goog-user-project</c>, the user's own project: with a user's token Google bills the call to a quota project,
/// and without the header that is this app's own Google project, where the Budget API is not on. The budget watches the one project (the
/// filter takes the project NUMBER, read from Resource Manager), has no <c>notificationsRule</c> (so the billing account's administrators and
/// users are emailed by default) and no <c>currencyCode</c> (so the account's own currency is used; a mismatched one is refused).
/// THEORY (unverified, no live billing account): that an unset currency and an absent notificationsRule behave as documented, and that the
/// filter accepts <c>projects/{number}</c>; docs/ToTest.md carries the real-account row.
/// </summary>
internal sealed class GoogleBudgetGateway : IBudgetGateway
{
    private const int PageSize = 100;
    private const string UpdateMask = "amount,budgetFilter,thresholdRules";
    private const string SpendBasis = "CURRENT_SPEND";
    private const decimal NanosPerUnit = 1_000_000_000m;

    private readonly CloudBillingBudgetService _service;
    private readonly CloudResourceManagerService _resourceManager;
    private readonly CloudCallPipeline _pipeline;
    private readonly Func<string?> _selectedProjectId;

    public GoogleBudgetGateway(CloudBillingBudgetService service, CloudResourceManagerService resourceManager, CloudCallPipeline pipeline, Func<string?> selectedProjectId)
    {
        _service = service;
        _resourceManager = resourceManager;
        _pipeline = pipeline;
        _selectedProjectId = selectedProjectId;
    }

    public async Task<BudgetInfo?> FindAsync(string billingAccountId, string installationId, string projectId, CancellationToken cancellationToken)
    {
        var quotaProject = _selectedProjectId();
        string? pageToken = null;
        do
        {
            var requestedToken = pageToken;
            var page = await CallAsync(
                "Budget.List",
                token =>
                {
                    var request = _service.BillingAccounts.Budgets.List(billingAccountId);
                    request.PageSize = PageSize;
                    request.PageToken = requestedToken;
                    UseQuotaProject(request, quotaProject);
                    return request.ExecuteAsync(token);
                },
                cancellationToken,
                replayable: true).ConfigureAwait(false);

            if ((page.Budgets ?? []).FirstOrDefault(b => BudgetNaming.IsOurs(b.DisplayName, installationId, projectId)) is { } ours)
            {
                return ToInfo(ours, billingAccountId);
            }

            var next = string.IsNullOrEmpty(page.NextPageToken) ? null : page.NextPageToken;
            if (next is not null && next == pageToken)
            {
                throw new CloudOperationException(
                    new CloudError("LIST_PAGINATION_STUCK", null, "The Budget API returned the same page token twice, so the budget list may be incomplete."),
                    CloudErrorKind.Network);
            }

            pageToken = next;
        }
        while (pageToken is not null);

        return null;
    }

    public async Task<BudgetInfo> UpsertAsync(BudgetSpec spec, CancellationToken cancellationToken)
    {
        // The existing budget is looked up first: a budget made by an earlier attempt (or whose create response was lost) is changed, never doubled.
        var existing = await FindAsync(spec.BillingAccountId, spec.InstallationId, spec.ProjectId, cancellationToken).ConfigureAwait(false);
        var projectNumber = await ProjectNumberAsync(spec.ProjectId, cancellationToken).ConfigureAwait(false);
        var body = BuildBody(spec, projectNumber);

        BudgetData.GoogleCloudBillingBudgetsV1Budget saved;
        if (existing is null)
        {
            saved = await CallAsync(
                "Budget.Create",
                token =>
                {
                    var request = _service.BillingAccounts.Budgets.Create(body, spec.BillingAccountId);
                    UseQuotaProject(request, spec.ProjectId);
                    return request.ExecuteAsync(token);
                },
                cancellationToken,
                replayable: false).ConfigureAwait(false);
        }
        else
        {
            saved = await CallAsync(
                "Budget.Patch",
                token =>
                {
                    var request = _service.BillingAccounts.Budgets.Patch(body, existing.Name);
                    request.UpdateMask = UpdateMask;
                    UseQuotaProject(request, spec.ProjectId);
                    return request.ExecuteAsync(token);
                },
                cancellationToken,
                replayable: true).ConfigureAwait(false);
        }

        return ToInfo(saved, spec.BillingAccountId);
    }

    public async Task DeleteAsync(string budgetName, CancellationToken cancellationToken)
    {
        var quotaProject = _selectedProjectId();
        try
        {
            await CallAsync(
                "Budget.Delete",
                token =>
                {
                    var request = _service.BillingAccounts.Budgets.Delete(budgetName);
                    UseQuotaProject(request, quotaProject);
                    return request.ExecuteAsync(token);
                },
                cancellationToken,
                replayable: true).ConfigureAwait(false);
        }
        catch (CloudOperationException ex) when (ex.Error.HttpStatus == 404)
        {
            // Already gone: the user's goal (no budget) holds.
        }
    }

    private async Task<string> ProjectNumberAsync(string projectId, CancellationToken cancellationToken)
    {
        var project = await CallAsync(
            "Budget.ProjectNumber",
            token => _resourceManager.Projects.Get("projects/" + projectId).ExecuteAsync(token),
            cancellationToken,
            replayable: true).ConfigureAwait(false);

        // Resource Manager names a project "projects/<number>"; the budget filter takes that same form.
        return string.IsNullOrEmpty(project.Name)
            ? throw new CloudOperationException(new CloudError("PROJECT_NUMBER_MISSING", null, "Resource Manager did not return the project's number."), CloudErrorKind.Other)
            : project.Name;
    }

    private static BudgetData.GoogleCloudBillingBudgetsV1Budget BuildBody(BudgetSpec spec, string projectName)
    {
        // Whole cents first, in decimal: a double or a fraction like 12.9999999996 would otherwise round to 1,000,000,000 nanos, which Google refuses.
        var rounded = BudgetNaming.RoundToCents(spec.AmountUsd);
        var units = Math.Floor(rounded);
        var nanos = (int)((rounded - units) * NanosPerUnit);
        return new BudgetData.GoogleCloudBillingBudgetsV1Budget
        {
            DisplayName = BudgetNaming.DisplayName(spec.InstallationId, spec.ProjectId),
            BudgetFilter = new BudgetData.GoogleCloudBillingBudgetsV1Filter { Projects = [projectName] },
            Amount = new BudgetData.GoogleCloudBillingBudgetsV1BudgetAmount
            {
                SpecifiedAmount = new BudgetData.GoogleTypeMoney { Units = (long)units, Nanos = nanos == 0 ? null : nanos },
            },
            ThresholdRules = BudgetNaming.ThresholdFractions
                .Select(f => new BudgetData.GoogleCloudBillingBudgetsV1ThresholdRule { ThresholdPercent = f, SpendBasis = SpendBasis })
                .ToList(),
        };
    }

    private static BudgetInfo ToInfo(BudgetData.GoogleCloudBillingBudgetsV1Budget budget, string billingAccountId)
    {
        var money = budget.Amount?.SpecifiedAmount;
        var amount = (money?.Units ?? 0) + ((money?.Nanos ?? 0) / NanosPerUnit);
        var percents = (budget.ThresholdRules ?? [])
            .Where(r => r.ThresholdPercent is not null)
            .Select(r => Math.Round(r.ThresholdPercent!.Value * 100, 2))
            .ToList();
        return new BudgetInfo(
            budget.Name ?? string.Empty,
            budget.DisplayName ?? string.Empty,
            billingAccountId,
            amount,
            percents);
    }

    private static void UseQuotaProject<T>(Google.Apis.Requests.ClientServiceRequest<T> request, string? projectId)
    {
        if (!string.IsNullOrEmpty(projectId))
        {
            request.ModifyRequest += message => message.Headers.TryAddWithoutValidation("x-goog-user-project", projectId);
        }
    }

    private Task<T> CallAsync<T>(string operation, Func<CancellationToken, Task<T>> request, CancellationToken cancellationToken, bool replayable)
        => _pipeline.ExecuteAsync(
            operation,
            async token =>
            {
                try
                {
                    return await request(token).ConfigureAwait(false);
                }
                catch (GoogleApiException ex)
                {
                    throw GoogleApiErrors.ToException(GoogleApiErrors.FromApiException(ex));
                }
            },
            cancellationToken,
            replayable: replayable,
            singleRequest: true);
}
