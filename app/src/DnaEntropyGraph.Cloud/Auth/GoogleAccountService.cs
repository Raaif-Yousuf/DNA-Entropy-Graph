using System.ComponentModel;
using System.Security.Cryptography;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Cloud;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Http;
using Google.Apis.Util.Store;

namespace DnaEntropyGraph.Cloud.Auth;

/// <summary>
/// Sign in with Google for a desktop app (issue #48): the authorization-code flow with PKCE (S256) and a loopback
/// redirect, tokens in DPAPI files keyed by the account's <c>sub</c>, a switcher over several accounts.
/// Hard Rule 7: this is the Google-facing implementation of Core's <see cref="IGcpAccount"/>,
/// <see cref="IGcpAccessTokenSource"/> and <see cref="ICloudTokenRefresher"/>. Nothing here logs a token, an email or
/// a code: failures carry an <see cref="AuthErrorCodes"/> code and the English lives in <c>Resources.resw</c>.
/// </summary>
/// <remarks>
/// The flow is <see cref="PkceGoogleAuthorizationCodeFlow"/> from Google.Apis.Auth, not a hand-rolled one: it sends
/// <c>code_challenge</c>/<c>code_challenge_method=S256</c> and the matching <c>code_verifier</c> (MEASURED 2026-10-03:
/// the PKCE test in GoogleAccountServiceTests checks both at the HTTP level). The prompt is <c>select_account consent</c>:
/// <c>select_account</c> lets a second Google account be chosen; <c>consent</c> makes Google return a refresh token every
/// time, because without it a repeat sign-in of an already-consented account returns none (THEORY (unverified):
/// Google's documented behaviour, not yet seen against the owner's client, see docs/ToTest.md).
/// </remarks>
public sealed class GoogleAccountService : IGcpAccount, IGcpAccessTokenSource, ICloudTokenRefresher
{
    private static readonly string[] Scopes = ["openid", "email", "https://www.googleapis.com/auth/cloud-platform"];

    private readonly GoogleAccountOptions _options;
    private readonly DpapiTokenStore _store;
    private readonly AccountRegistry _registry;
    // _gate guards the account list and the token files and is only ever held for short, local work.
    // _signInGate serialises sign-ins and IS held across the wait on the browser, so that wait never blocks a
    // token call for the account that is already signed in (cold review of #48).
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _signInGate = new(1, 1);
    private readonly object _loadLock = new();
    private AccountsFile? _state;

    public GoogleAccountService(GoogleAccountOptions options)
    {
        _options = options;
        _store = new DpapiTokenStore(options.AuthDirectory, options.Protector);
        _registry = new AccountRegistry(options.AuthDirectory);
    }

    public event EventHandler? AccountChanged;

    public bool IsSignedIn => State.Active is { NeedsSignIn: false };

    /// <summary>
    /// <see cref="GoogleAccountOptions.ProjectIdUntilSelectionExists"/> while signed in, null otherwise: the project is
    /// chosen in the setup wizard and stored per account in a later issue (#520). Signed out stays null, so a run
    /// still fails with no_project before anyone signs in, exactly as it did against the fake.
    /// </summary>
    public string? SelectedProjectId => IsSignedIn ? _options.ProjectIdUntilSelectionExists : null;

    public AccountInfo? CurrentAccount => State.Active is { } active ? ToInfo(active) : null;

    public IReadOnlyList<AccountInfo> Accounts => State.Accounts.Select(ToInfo).ToList();

    private AccountsFile State
    {
        get
        {
            lock (_loadLock)
            {
                return _state ??= Reconcile(_registry.Load());
            }
        }
    }

    public async Task SignInAsync(CancellationToken cancellationToken)
    {
        await _signInGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var client = _options.ClientLoader.Load();
            var token = await AuthorizeInBrowserAsync(client, cancellationToken).ConfigureAwait(false);

            var claims = IdTokenClaims.Read(token.IdToken);
            if (claims is not { } identity || !DpapiTokenStore.IsSafeKey(identity.Sub))
            {
                throw new AccountAuthException(AuthErrorCodes.SigninFailed, "the id token carried no usable subject");
            }

            if (string.IsNullOrEmpty(token.RefreshToken))
            {
                throw new AccountAuthException(AuthErrorCodes.SigninFailed, "Google returned no refresh token");
            }

            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                try
                {
                    await _store.StoreAsync(identity.Sub, token).ConfigureAwait(false);
                    var record = new AccountRecord(identity.Sub, string.IsNullOrWhiteSpace(identity.Email) ? identity.Sub : identity.Email, NeedsSignIn: false);
                    var current = State;
                    Commit(new AccountsFile(identity.Sub, [.. current.Accounts.Where(a => a.Sub != identity.Sub), record]));
                }
                catch (TokenStorageException ex)
                {
                    // The account list could not be saved: do not leave a token file nothing lists.
                    TryDeleteToken(identity.Sub);
                    throw StorageFailure(ex);
                }
            }
            finally
            {
                _gate.Release();
            }
        }
        finally
        {
            _signInGate.Release();
        }

        RaiseChanged();
    }

    public async Task<bool> SignOutAsync(CancellationToken cancellationToken)
    {
        bool revoked;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var active = State.Active;
            if (active is null)
            {
                return true;
            }

            revoked = await RevokeAsync(active, cancellationToken).ConfigureAwait(false);

            // Local deletion happens whatever Google said: the user asked to be signed out.
            await _store.DeleteAsync<TokenResponse>(active.Sub).ConfigureAwait(false);
            var remaining = State.Accounts.Where(a => a.Sub != active.Sub).ToList();
            var next = remaining.FirstOrDefault(a => !a.NeedsSignIn) ?? remaining.FirstOrDefault();
            Commit(new AccountsFile(next?.Sub, remaining));
        }
        catch (TokenStorageException ex)
        {
            throw StorageFailure(ex);
        }
        finally
        {
            _gate.Release();
        }

        RaiseChanged();
        return revoked;
    }

    public async Task SwitchAccountAsync(string sub, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = State;
            if (current.Accounts.All(a => a.Sub != sub))
            {
                throw new AccountAuthException(AuthErrorCodes.AccountNotFound);
            }

            Commit(current with { ActiveSub = sub });
        }
        catch (TokenStorageException ex)
        {
            throw StorageFailure(ex);
        }
        finally
        {
            _gate.Release();
        }

        RaiseChanged();
    }

    public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken) => AccessTokenAsync(forceRefresh: false, cancellationToken);

    /// <summary>The resilience pipeline calls this once after a 401: the access token Google just refused is replaced even if it looks fresh.</summary>
    public async Task RefreshAsync(CancellationToken cancellationToken) => _ = await AccessTokenAsync(forceRefresh: true, cancellationToken).ConfigureAwait(false);

    private async Task<string> AccessTokenAsync(bool forceRefresh, CancellationToken cancellationToken)
    {
        var expired = false;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var active = State.Active;
            if (active is null || active.NeedsSignIn)
            {
                throw new AccountAuthException(AuthErrorCodes.SigninExpired, "no signed-in account");
            }

            var token = await _store.GetAsync<TokenResponse>(active.Sub).ConfigureAwait(false);
            if (token is null || string.IsNullOrEmpty(token.RefreshToken))
            {
                await MarkExpiredAsync(active).ConfigureAwait(false);
                expired = true;
                throw new AccountAuthException(AuthErrorCodes.SigninExpired, "the stored token is missing or unreadable");
            }

            var client = _options.ClientLoader.Load();
            using var flow = NewFlow(client, _store);
            var credential = new UserCredential(flow, active.Sub, token);
            try
            {
                if (forceRefresh)
                {
                    await credential.RefreshTokenAsync(cancellationToken).ConfigureAwait(false);
                    return credential.Token.AccessToken;
                }

                return await credential.GetAccessTokenForRequestAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (TokenResponseException ex) when (ex.Error?.Error == "invalid_grant")
            {
                await MarkExpiredAsync(active).ConfigureAwait(false);
                expired = true;
                throw new AccountAuthException(AuthErrorCodes.SigninExpired, "Google refused the refresh token (invalid_grant)", ex);
            }
            catch (TokenResponseException ex)
            {
                throw new AccountAuthException(AuthErrorCodes.SigninFailed, $"token endpoint answered {ex.Error?.Error}", ex);
            }
            catch (Exception ex) when (ex is HttpRequestException || IsRefreshManagerGiveUp(ex) || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
            {
                // A request timeout reaches us as a TaskCanceledException the caller did not ask for, or, after
                // Google's refresh manager has retried it three times, as an InvalidOperationException
                // ("could not be refreshed. Errors: timeout, ..."); a refused token is invalid_grant, handled above.
                throw new AccountAuthException(AuthErrorCodes.NetworkUnavailable, "the token endpoint could not be reached", ex);
            }
        }
        catch (TokenStorageException ex)
        {
            throw StorageFailure(ex);
        }
        finally
        {
            _gate.Release();
            if (expired)
            {
                RaiseChanged();
            }
        }
    }

    private async Task<TokenResponse> AuthorizeInBrowserAsync(OAuthClient client, CancellationToken cancellationToken)
    {
        using var receiver = new LoopbackCodeReceiver(_options.Browser, _options.Pages, _options.SignInTimeout);

        // Nothing is stored under a guessed key: the account's sub is only known from the id token this returns.
        using var flow = NewFlow(client, new DiscardingDataStore());
        try
        {
            var credential = await new AuthorizationCodeInstalledApp(flow, receiver).AuthorizeAsync("signin", cancellationToken).ConfigureAwait(false);
            return credential.Token;
        }
        catch (TokenResponseException ex) when (ex.Error?.Error == "access_denied")
        {
            throw new AccountAuthException(AuthErrorCodes.SigninCancelled, "the user declined", ex);
        }
        catch (TokenResponseException ex)
        {
            throw new AccountAuthException(AuthErrorCodes.SigninFailed, $"token endpoint answered {ex.Error?.Error}", ex);
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            throw new AccountAuthException(AuthErrorCodes.NetworkUnavailable, "Google could not be reached", ex);
        }
        catch (Win32Exception ex)
        {
            throw new AccountAuthException(AuthErrorCodes.BrowserUnavailable, "the browser could not be started", ex);
        }
        catch (TimeoutException ex)
        {
            throw new AccountAuthException(AuthErrorCodes.SigninTimeout, "the sign-in page was not completed", ex);
        }
    }

    private async Task<bool> RevokeAsync(AccountRecord account, CancellationToken cancellationToken)
    {
        var token = await _store.GetAsync<TokenResponse>(account.Sub).ConfigureAwait(false);
        if (token?.RefreshToken is null)
        {
            return true; // nothing at Google to revoke: the token was already refused or never readable
        }

        try
        {
            using var flow = NewFlow(_options.ClientLoader.Load(), _store);
            await flow.RevokeTokenAsync(account.Sub, token.RefreshToken, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (TokenResponseException ex) when (ex.Error?.Error == "invalid_token")
        {
            return true; // Google no longer knows the token: that is revoked
        }
        catch (Exception ex) when (ex is TokenResponseException or HttpRequestException or AccountAuthException
            || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return false;
        }
    }

    private GoogleAuthorizationCodeFlow NewFlow(OAuthClient client, IDataStore store)
        => new PkceGoogleAuthorizationCodeFlow(new GoogleAuthorizationCodeFlow.Initializer
        {
            ClientSecrets = new ClientSecrets { ClientId = client.ClientId, ClientSecret = client.ClientSecret },
            Scopes = Scopes,
            Prompt = "select_account consent",
            DataStore = store,
            HttpClientFactory = _options.HttpHandler is { } handler ? new HandlerFactory(handler) : null,
        });

    private async Task MarkExpiredAsync(AccountRecord account)
    {
        await _store.DeleteAsync<TokenResponse>(account.Sub).ConfigureAwait(false);
        var current = State;
        Commit(current with { Accounts = [.. current.Accounts.Select(a => a.Sub == account.Sub ? a with { NeedsSignIn = true } : a)] });
    }

    private void Commit(AccountsFile file)
    {
        _registry.Save(file);
        lock (_loadLock)
        {
            _state = file;
        }
    }

    /// <summary>An account whose token file is gone (deleted by hand, restored from a backup on another PC) is "sign in again", not signed in.</summary>
    private AccountsFile Reconcile(AccountsFile file)
        => file with
        {
            Accounts = [.. file.Accounts.Select(a => !a.NeedsSignIn && DpapiTokenStore.IsSafeKey(a.Sub) && File.Exists(_store.PathFor(a.Sub)) ? a : a with { NeedsSignIn = true })],
        };

    /// <summary>Google's TokenRefreshManager gives up after three transient failures with this exact message; any other InvalidOperationException is a bug and must surface.</summary>
    private static bool IsRefreshManagerGiveUp(Exception ex) => ex is InvalidOperationException && ex.Message.Contains("could not be refreshed", StringComparison.Ordinal);

    private void TryDeleteToken(string sub)
    {
        try
        {
            File.Delete(_store.PathFor(sub));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: the original failure is the one the user needs to hear about.
        }
    }

    /// <summary>Only the exception type is kept: its message can carry a path with the user's name in it.</summary>
    private static AccountAuthException StorageFailure(TokenStorageException ex) => new(AuthErrorCodes.StorageFailed, ex.Message, ex);

    private static AccountInfo ToInfo(AccountRecord record) => new(record.Sub, record.Email, record.NeedsSignIn);

    private void RaiseChanged() => AccountChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>Hands Google's HTTP stack a shared handler it must not dispose (a flow disposes its client when it is).</summary>
    private sealed class HandlerFactory(HttpMessageHandler handler) : HttpClientFactory
    {
        protected override HttpMessageHandler CreateHandler(CreateHttpClientArgs args) => new NonDisposingHandler(handler);
    }

    private sealed class NonDisposingHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override void Dispose(bool disposing)
        {
        }
    }
}
