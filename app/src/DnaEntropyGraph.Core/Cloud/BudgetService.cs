using DnaEntropyGraph.Core.Abstractions;

namespace DnaEntropyGraph.Core.Cloud;

/// <summary>
/// Issues #148 and #222's policy over <see cref="IBudgetGateway"/>: which billing account (the project's linked one), whether the
/// Budgets API is on (switched on at the first save, never in the wizard), and which budget is this installation's (found by the
/// installation id in its name, Hard Rule 9). A failure is a <see cref="CloudOperationException"/>: <see cref="CloudErrorKind.Permission"/>
/// for a user who is not a billing administrator, <see cref="BudgetErrorCodes.NoBilling"/> for a project with no billing account.
/// </summary>
public sealed class BudgetService : IBudgetService
{
    private readonly IBudgetGateway _budgets;
    private readonly IBillingGateway _billing;
    private readonly IServiceEnablementGateway _services;
    private readonly ISettingsStore _settings;

    public BudgetService(IBudgetGateway budgets, IBillingGateway billing, IServiceEnablementGateway services, ISettingsStore settings)
    {
        _budgets = budgets;
        _billing = billing;
        _services = services;
        _settings = settings;
    }

    public async Task<BudgetInfo?> GetAsync(string projectId, CancellationToken cancellationToken)
    {
        if (await LinkedAccountAsync(projectId, cancellationToken).ConfigureAwait(false) is not { } account)
        {
            return null;
        }

        // A budget cannot exist while the API is off, and listing would fail with a permission-shaped error that names the wrong problem.
        if (!await _services.IsServiceEnabledAsync(projectId, RequiredServices.BillingBudgets, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return await _budgets.FindAsync(account, InstallationId.GetOrCreate(_settings), cancellationToken).ConfigureAwait(false);
    }

    public async Task<BudgetInfo> SaveAsync(string projectId, double amountUsd, CancellationToken cancellationToken)
    {
        if (!BudgetNaming.IsValidAmount(amountUsd))
        {
            throw new CloudOperationException(new CloudError(BudgetErrorCodes.InvalidAmount, null, "The budget amount is not a usable number."), CloudErrorKind.Other);
        }

        var account = await LinkedAccountAsync(projectId, cancellationToken).ConfigureAwait(false)
            ?? throw new CloudOperationException(new CloudError(BudgetErrorCodes.NoBilling, null, "The project has no billing account to put a budget on."), CloudErrorKind.Billing);

        if (!await _services.IsServiceEnabledAsync(projectId, RequiredServices.BillingBudgets, cancellationToken).ConfigureAwait(false))
        {
            await _services.EnableServicesAsync(projectId, [RequiredServices.BillingBudgets], cancellationToken).ConfigureAwait(false);
        }

        return await _budgets.UpsertAsync(new BudgetSpec(account, projectId, InstallationId.GetOrCreate(_settings), amountUsd), cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> RemoveAsync(string projectId, CancellationToken cancellationToken)
    {
        if (await GetAsync(projectId, cancellationToken).ConfigureAwait(false) is not { } existing)
        {
            return false;
        }

        await _budgets.DeleteAsync(existing.Name, cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<string> ConsoleLinkAsync(string projectId, CancellationToken cancellationToken)
    {
        try
        {
            return BudgetLinks.ForBillingAccount(await LinkedAccountAsync(projectId, cancellationToken).ConfigureAwait(false));
        }
        catch (CloudOperationException)
        {
            // A link is the fallback for a failure: it must not fail in turn, so the general billing page stands in.
            return BudgetLinks.ForBillingAccount(null);
        }
    }

    private async Task<string?> LinkedAccountAsync(string projectId, CancellationToken cancellationToken)
    {
        var status = await _billing.GetBillingStatusAsync(projectId, cancellationToken).ConfigureAwait(false);
        return string.IsNullOrEmpty(status.AccountId) ? null : status.AccountId;
    }
}
