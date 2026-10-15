using DnaEntropyGraph.Core.Cloud;

namespace DnaEntropyGraph.Core.Abstractions;

/// <summary>
/// The signed-in Google account and its selected project, as Presentation
/// is allowed to see it. Implemented in DnaEntropyGraph.Cloud (which is
/// allowed to reference Google.*); Presentation never references Cloud
/// directly (docs/architecture.md section 2's dependency-arrow diagram).
/// Several accounts can be signed in on one PC; one is current (issue #48).
/// </summary>
public interface IGcpAccount
{
    /// <summary>True when the current account holds a token Google has not refused.</summary>
    bool IsSignedIn { get; }

    /// <summary>The project the current account chose in the setup wizard (stored with that account, so switching accounts switches it); null when signed out or none chosen yet.</summary>
    string? SelectedProjectId { get; }

    /// <summary>The account calls are made as; null when none is signed in.</summary>
    AccountInfo? CurrentAccount { get; }

    /// <summary>Every account signed in on this PC, the current one included.</summary>
    IReadOnlyList<AccountInfo> Accounts { get; }

    /// <summary>Raised after the current account, the account list or the sign-in state changed. Not guaranteed to be on the UI thread.</summary>
    event EventHandler? AccountChanged;

    /// <summary>Opens the system browser on Google's page and waits. Throws <see cref="AccountAuthException"/> for every failure the user can act on.</summary>
    Task SignInAsync(CancellationToken cancellationToken);

    /// <summary>Revokes the current account's token at Google and deletes it here. Returns false when Google could not be reached to revoke (the local sign-in is deleted either way).</summary>
    Task<bool> SignOutAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Stores <paramref name="projectId"/> as the current account's project. Throws <see cref="AccountAuthException"/>:
    /// <see cref="AuthErrorCodes.ProjectInvalid"/> for an id that is not a legal Google project id (nothing changes),
    /// <see cref="AuthErrorCodes.SigninExpired"/> when no account is signed in, <see cref="AuthErrorCodes.StorageFailed"/> when it could not be saved.
    /// </summary>
    Task SelectProjectAsync(string projectId, CancellationToken cancellationToken);

    /// <summary>Makes <paramref name="sub"/> the current account. Throws <see cref="AccountAuthException"/> with <see cref="AuthErrorCodes.AccountNotFound"/> for an unknown one.</summary>
    Task SwitchAccountAsync(string sub, CancellationToken cancellationToken);
}
