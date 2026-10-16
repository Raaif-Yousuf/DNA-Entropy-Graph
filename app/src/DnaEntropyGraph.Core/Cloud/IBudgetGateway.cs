namespace DnaEntropyGraph.Core.Cloud;

/// <summary>
/// The Cloud Billing budget that emails the user at 50, 90 and 100 percent of an amount they choose (issues #148 and #222).
/// The app's own spend figure is an estimate; this is Google's own meter. See IComputeGateway for the Hard Rule 7 boundary.
/// A budget lives on the BILLING ACCOUNT, not the project, and a project owner is often not a billing administrator, so a
/// <see cref="CloudErrorKind.Permission"/> failure is the common case and not a bug. Budgets carry no labels, so the one
/// per installation is found by its display name (<see cref="BudgetNaming"/>), never by a fixed name (Hard Rule 9).
/// </summary>
public interface IBudgetGateway
{
    /// <summary>This installation's budget on the account, or null when there is none. Never returns another installation's budget.</summary>
    Task<BudgetInfo?> FindAsync(string billingAccountId, string installationId, CancellationToken cancellationToken);

    /// <summary>Creates this installation's budget, or changes the amount and project of the one that exists (idempotent: a second call never makes a second budget).</summary>
    Task<BudgetInfo> UpsertAsync(BudgetSpec spec, CancellationToken cancellationToken);

    /// <summary>Deletes a budget by the <see cref="BudgetInfo.Name"/> a find or upsert returned. A budget that is already gone is not an error.</summary>
    Task DeleteAsync(string budgetName, CancellationToken cancellationToken);
}

/// <param name="BillingAccountId">The account's resource name as Google writes it, <c>billingAccounts/XXXXXX-XXXXXX-XXXXXX</c>.</param>
/// <param name="ProjectId">The project the budget watches (spend of other projects on the account is not counted).</param>
/// <param name="InstallationId">The installation id that goes into the display name so two users on one account never share a budget.</param>
/// <param name="AmountUsd">The monthly amount, in the currency of the billing account (US dollars for most).</param>
public sealed record BudgetSpec(string BillingAccountId, string ProjectId, string InstallationId, double AmountUsd);

/// <param name="Name">Google's resource name, <c>billingAccounts/AAA/budgets/GUID</c>.</param>
public sealed record BudgetInfo(string Name, string DisplayName, string BillingAccountId, double AmountUsd, IReadOnlyList<double> ThresholdPercents);

/// <summary>Where a budget is found and how it is warned about: one place, so the gateway, the fake and the tests agree.</summary>
public static class BudgetNaming
{
    /// <summary>The prefix every budget this app makes starts with.</summary>
    public const string DisplayNamePrefix = "DNA Entropy Graph ";

    /// <summary>The default monthly amount offered in Settings.</summary>
    public const double DefaultAmountUsd = 50;

    /// <summary>The largest amount accepted (a typo guard; Google takes far more).</summary>
    public const double MaxAmountUsd = 1_000_000;

    /// <summary>The fractions of the amount that send an email.</summary>
    public static IReadOnlyList<double> ThresholdFractions { get; } = [0.5, 0.9, 1.0];

    /// <summary>The same thresholds as the percentages the user reads.</summary>
    public static IReadOnlyList<double> ThresholdPercents { get; } = [50, 90, 100];

    public static string DisplayName(string installationId) => DisplayNamePrefix + installationId;

    public static bool IsOurs(string? displayName, string installationId)
        => string.Equals(displayName, DisplayName(installationId), StringComparison.Ordinal);

    /// <summary>True for a finite amount of at least 1, up to <see cref="MaxAmountUsd"/>.</summary>
    public static bool IsValidAmount(double amountUsd) => double.IsFinite(amountUsd) && amountUsd >= 1 && amountUsd <= MaxAmountUsd;
}

/// <summary>The Google Cloud console pages a budget problem sends the user to (a link action, Hard Rule 13).</summary>
public static class BudgetLinks
{
    /// <summary>The budgets page of a billing account, or the general billing page when the account is not known.</summary>
    public static string ForBillingAccount(string? billingAccountId)
    {
        var id = billingAccountId?.Replace("billingAccounts/", string.Empty, StringComparison.Ordinal);
        return string.IsNullOrEmpty(id)
            ? "https://console.cloud.google.com/billing"
            : "https://console.cloud.google.com/billing/" + Uri.EscapeDataString(id) + "/budgets";
    }
}

/// <summary>The codes a budget step fails under, in <see cref="CloudError.Code"/> of a <see cref="CloudOperationException"/>.</summary>
public static class BudgetErrorCodes
{
    /// <summary>The project has no billing account, so there is nothing to put a budget on. Action: open Google's billing page.</summary>
    public const string NoBilling = "BUDGET_NO_BILLING";

    /// <summary>The amount is not a usable number. Action: type an amount.</summary>
    public const string InvalidAmount = "BUDGET_INVALID_AMOUNT";
}

/// <summary>
/// What Settings asks for, by project: look, save, remove. Implemented by <see cref="BudgetService"/>; an interface so the
/// ViewModel is tested without any cloud type.
/// </summary>
public interface IBudgetService
{
    /// <summary>The installation's budget on the project's billing account; null when none, or when the Budgets API is off (no budget can exist then).</summary>
    Task<BudgetInfo?> GetAsync(string projectId, CancellationToken cancellationToken);

    /// <summary>Turns the Budgets API on if needed, then creates or updates the budget.</summary>
    Task<BudgetInfo> SaveAsync(string projectId, double amountUsd, CancellationToken cancellationToken);

    /// <summary>Deletes the installation's budget; true when one existed.</summary>
    Task<bool> RemoveAsync(string projectId, CancellationToken cancellationToken);

    /// <summary>The console page to send the user to for this project: its account's budgets, or the billing home.</summary>
    Task<string> ConsoleLinkAsync(string projectId, CancellationToken cancellationToken);
}
