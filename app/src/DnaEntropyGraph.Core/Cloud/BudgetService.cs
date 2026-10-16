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
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    /// <summary>Waits before the n-th retry of a save that Google still answers "service disabled": 2, 4, 8 then 16 seconds, then it gives up.</summary>
    private const int MaxAttempts = 5;

    private static readonly TimeSpan FirstBackoff = TimeSpan.FromSeconds(2);

    public BudgetService(IBudgetGateway budgets, IBillingGateway billing, IServiceEnablementGateway services, ISettingsStore settings, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _delay = delay ?? Task.Delay;
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

        return await _budgets.FindAsync(account, InstallationId.GetOrCreate(_settings), projectId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BudgetInfo> SaveAsync(string projectId, decimal amountUsd, CancellationToken cancellationToken)
    {
        if (!BudgetNaming.IsValidAmount(amountUsd))
        {
            throw new CloudOperationException(new CloudError(BudgetErrorCodes.InvalidAmount, null, "The budget amount is not a usable number."), CloudErrorKind.Other);
        }

        var status = await _billing.GetBillingStatusAsync(projectId, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(status.AccountId))
        {
            throw new CloudOperationException(new CloudError(BudgetErrorCodes.NoBilling, null, "The project has no billing account to put a budget on."), CloudErrorKind.Billing);
        }

        // A different problem with a different action: the account exists but is switched off or suspended.
        if (!status.Enabled)
        {
            throw new CloudOperationException(new CloudError(BudgetErrorCodes.BillingDisabled, null, "Billing is switched off for the project."), CloudErrorKind.Billing);
        }

        var account = status.AccountId;

        if (!await _services.IsServiceEnabledAsync(projectId, RequiredServices.BillingBudgets, cancellationToken).ConfigureAwait(false))
        {
            await _services.EnableServicesAsync(projectId, [RequiredServices.BillingBudgets], cancellationToken).ConfigureAwait(false);
        }

        var spec = new BudgetSpec(account, projectId, InstallationId.GetOrCreate(_settings), BudgetNaming.RoundToCents(amountUsd));
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await _budgets.UpsertAsync(spec, cancellationToken).ConfigureAwait(false);
            }
            catch (CloudOperationException ex) when (ex.Kind == CloudErrorKind.ApiDisabled && attempt < MaxAttempts)
            {
                // Google can still answer "service disabled" for a short while after the enable returned. Only that kind is retried, and
                // UpsertAsync looks the budget up first, so a replay never creates a second one.
                await _delay(FirstBackoff * Math.Pow(2, attempt - 1), cancellationToken).ConfigureAwait(false);
            }
        }
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
        // An account that is linked but switched off cannot bill, so it reads as no billing (the same action: open Google's billing page).
        return !status.Enabled || string.IsNullOrEmpty(status.AccountId) ? null : status.AccountId;
    }
}
