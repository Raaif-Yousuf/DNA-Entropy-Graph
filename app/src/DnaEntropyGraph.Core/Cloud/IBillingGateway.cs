namespace DnaEntropyGraph.Core.Cloud;

/// <summary>
/// First-run wizard step 4 (issue #51): is the project paying for itself, and if not, link a billing account. No
/// billing means no VM ever starts, and in the prototype that looked exactly like a stockout, so this step blocks the
/// Run button. See IComputeGateway for the Hard Rule 7 boundary. <see cref="BillingSetup"/> is the policy over it.
/// A refusal to link is a <see cref="CloudOperationException"/> carrying <see cref="SetupErrorCodes.BillingNoPermission"/>.
/// </summary>
public interface IBillingGateway
{
    Task<BillingStatus> GetBillingStatusAsync(string projectId, CancellationToken cancellationToken);

    /// <summary>The billing accounts the signed-in user can see that are open (a closed account cannot be linked).</summary>
    Task<IReadOnlyList<BillingAccountSummary>> ListOpenBillingAccountsAsync(CancellationToken cancellationToken);

    /// <param name="billingAccountId">The account's resource name as Google writes it, <c>billingAccounts/XXXXXX-XXXXXX-XXXXXX</c>.</param>
    Task LinkProjectAsync(string projectId, string billingAccountId, CancellationToken cancellationToken);
}

/// <summary>A project's billing state: <paramref name="Enabled"/> is true only when an account is linked AND switched on.</summary>
public sealed record BillingStatus(bool Enabled, string? AccountId);

/// <summary>An open billing account as the wizard lists it.</summary>
public sealed record BillingAccountSummary(string AccountId, string DisplayName);
