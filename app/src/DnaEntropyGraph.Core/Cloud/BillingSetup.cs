namespace DnaEntropyGraph.Core.Cloud;

/// <summary>What the billing step found, and what the wizard does next.</summary>
public enum BillingOutcomeKind
{
    /// <summary>Billing was already on; nothing was linked.</summary>
    AlreadyEnabled,

    /// <summary>Exactly one open account existed, or the user chose one, and linking it turned billing on.</summary>
    Linked,

    /// <summary>Several open accounts: the user picks one (<see cref="BillingOutcome.Accounts"/>), then <see cref="BillingSetup.LinkAsync"/>.</summary>
    ChooseAccount,

    /// <summary>No usable account: open <see cref="BillingOutcome.DeepLink"/>, then call <see cref="BillingSetup.EnsureAsync"/> again (that is the re-check).</summary>
    NeedsAccount,

    /// <summary>The link was accepted but billing is still off: <see cref="BillingOutcome.DeepLink"/> opens the project's billing page, or the user picks another account.</summary>
    LinkedButStillOff,
}

public sealed record BillingOutcome(
    BillingOutcomeKind Kind,
    string? AccountId = null,
    IReadOnlyList<BillingAccountSummary>? Accounts = null,
    string? DeepLink = null)
{
    public IReadOnlyList<BillingAccountSummary> Accounts { get; init; } = Accounts ?? [];

    /// <summary>The <see cref="SetupErrorCodes"/> code whose message the wizard shows for this outcome, or null when nothing is wrong.</summary>
    public string? Code => Kind switch
    {
        BillingOutcomeKind.NeedsAccount => SetupErrorCodes.NoBilling,
        BillingOutcomeKind.LinkedButStillOff => SetupErrorCodes.BillingStillOff,
        _ => null,
    };
}

/// <summary>
/// Issue #51's policy: billing on, link the one account there is, ask when there are several, send the user to
/// Google's page when there are none. It carries no state, so calling <see cref="EnsureAsync"/> again after the user
/// has added a payment method is the "re-check", and a later preflight sees the flipped status without a restart.
/// </summary>
public sealed class BillingSetup
{
    private readonly IBillingGateway _gateway;

    public BillingSetup(IBillingGateway gateway)
    {
        _gateway = gateway;
    }

    public async Task<BillingOutcome> EnsureAsync(string projectId, CancellationToken cancellationToken)
    {
        var status = await _gateway.GetBillingStatusAsync(projectId, cancellationToken).ConfigureAwait(false);
        if (status.Enabled)
        {
            return new BillingOutcome(BillingOutcomeKind.AlreadyEnabled, status.AccountId);
        }

        var accounts = await _gateway.ListOpenBillingAccountsAsync(cancellationToken).ConfigureAwait(false);
        return accounts.Count switch
        {
            0 => new BillingOutcome(BillingOutcomeKind.NeedsAccount, DeepLink: BillingLinks.ForProject(projectId)),
            1 => await LinkAsync(projectId, accounts[0].AccountId, cancellationToken).ConfigureAwait(false),
            _ => new BillingOutcome(BillingOutcomeKind.ChooseAccount, Accounts: accounts),
        };
    }

    /// <summary>Links the account the user chose, then reads the status back: a link Google accepted that did not turn billing on is not success.</summary>
    public async Task<BillingOutcome> LinkAsync(string projectId, string billingAccountId, CancellationToken cancellationToken)
    {
        await _gateway.LinkProjectAsync(projectId, billingAccountId, cancellationToken).ConfigureAwait(false);
        var status = await _gateway.GetBillingStatusAsync(projectId, cancellationToken).ConfigureAwait(false);
        return status.Enabled
            ? new BillingOutcome(BillingOutcomeKind.Linked, billingAccountId)
            : new BillingOutcome(BillingOutcomeKind.LinkedButStillOff, DeepLink: BillingLinks.ForProject(projectId));
    }
}

/// <summary>The Google Cloud console pages the billing step sends the user to (a link action, Hard Rule 13).</summary>
public static class BillingLinks
{
    public const string CreateAccount = "https://console.cloud.google.com/billing/create";

    public static string ForProject(string projectId)
        => "https://console.cloud.google.com/billing/linkedaccount?project=" + Uri.EscapeDataString(projectId);
}

/// <summary>
/// The copyable text a user without billing rights sends to their billing administrator. The English is the
/// <c>SetupBillingRequestText</c> resource (Hard Rule 13); this only fills in the two placeholders.
/// </summary>
public static class BillingRequestText
{
    public static string Fill(string template, string projectId, string account)
    {
        ArgumentNullException.ThrowIfNull(template);
        return template.Replace("{project}", projectId, StringComparison.Ordinal).Replace("{account}", account, StringComparison.Ordinal);
    }
}
