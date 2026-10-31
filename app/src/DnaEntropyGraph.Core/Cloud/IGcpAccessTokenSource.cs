namespace DnaEntropyGraph.Core.Cloud;

/// <summary>
/// The access token the real gateways attach to every Google Cloud call. Implemented next to the OAuth flow in
/// DnaEntropyGraph.Cloud (Hard Rule 7). A refreshed token is persisted before it is returned; a refused one throws
/// <see cref="AccountAuthException"/> with <see cref="AuthErrorCodes.SigninExpired"/>.
/// </summary>
public interface IGcpAccessTokenSource
{
    /// <param name="accountSub">The account to act as (issue #830: token files are keyed by sub); null means the current account, for the calls that are not scoped to a run.</param>
    Task<string> GetAccessTokenAsync(string? accountSub, CancellationToken cancellationToken);
}
