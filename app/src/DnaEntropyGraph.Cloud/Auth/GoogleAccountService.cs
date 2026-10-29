using System.ComponentModel;
using System.Security.Cryptography;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Cloud;
using DnaEntropyGraph.Core.Net;
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
    private const string RequiredScope = "https://www.googleapis.com/auth/cloud-platform";
    private static readonly string[] Scopes = ["openid", "email", RequiredScope];

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
    private bool _accountsFileSetAside;
    private bool _accountsFileLocked;
    private bool _accountsFileFolderProblem;
    private DateTimeOffset _retryLoadAfter;
    private static readonly TimeSpan LockedRetryWindow = TimeSpan.FromSeconds(2);

    public GoogleAccountService(GoogleAccountOptions options)
    {
        _options = options;
        _store = new DpapiTokenStore(options.AuthDirectory, options.Protector);
        _registry = new AccountRegistry(options.AuthDirectory, name => new Mutex(false, name), options.SaveLockWait);
    }

    public event EventHandler? AccountChanged;

    /// <summary>Tests only: the registry under this service (to play a failed write).</summary>
    internal AccountRegistry Registry => _registry;

    /// <summary>Tests only: runs between the token file's write-time stamp and the list update of an expiry mark, to land another copy's sign-in in that window.</summary>
    internal Func<Task>? AfterExpiryStampForTest { get; set; }

    /// <summary>Tests only: runs between a sign-out's stamp (and token read) and its list change.</summary>
    internal Func<Task>? AfterSignOutStampForTest { get; set; }

    /// <summary>Tests only: runs after a sign-out's list change, before its revoke.</summary>
    internal Func<Task>? AfterSignOutListUpdateForTest { get; set; }

    public bool IsSignedIn => State.Active is { NeedsSignIn: false };

    /// <summary>
    /// The project the current account chose (<see cref="SelectProjectAsync"/>, stored in its <c>accounts.json</c> record), or, while
    /// signed in with none chosen, <see cref="GoogleAccountOptions.ProjectIdUntilSelectionExists"/> (null in a build with the real
    /// gateways). Signed out is null, so a run fails with no_project before anyone signs in, exactly as it did against the fake.
    /// </summary>
    public string? SelectedProjectId => State.Active is { NeedsSignIn: false } active ? active.ProjectId ?? _options.ProjectIdUntilSelectionExists : null;

    public AccountInfo? CurrentAccount => State.Active is { } active ? ToInfo(active) : null;

    public IReadOnlyList<AccountInfo> Accounts => State.Accounts.Select(ToInfo).ToList();

    private AccountsFile State
    {
        get
        {
            var state = LoadState(force: false, out var becameReadable);
            if (becameReadable)
            {
                // Outside the lock: a handler reads this service again.
                RaiseChanged();
            }

            return state;
        }
    }

    /// <summary>
    /// True when the saved account list exists but could not be read (issue #624): <see cref="IsSignedIn"/> is false and <see cref="Accounts"/> empty only
    /// because it is unread. A damaged file the folder would not let the app move aside is a folder problem, not a hold, so it is not this.
    /// </summary>
    public bool IsAccountsFileLocked
    {
        get
        {
            _ = State;
            lock (_loadLock)
            {
                return _accountsFileLocked && !_accountsFileFolderProblem;
            }
        }
    }

    /// <summary>
    /// Try again (issue #624): reads <c>accounts.json</c> now, whatever the window since the last failed read, and raises <see cref="AccountChanged"/> if it
    /// had been locked and can be read. Chosen over a timer: a timer would read the file on a pool thread unasked, and the user is looking at a
    /// message that says what to do.
    /// </summary>
    public void RecheckAccountsFile()
    {
        _ = LoadState(force: true, out var becameReadable);
        if (becameReadable)
        {
            RaiseChanged();
        }
    }

    private AccountsFile LoadState(bool force, out bool becameReadable)
    {
        becameReadable = false;
        lock (_loadLock)
        {
            if (_state is not null)
            {
                return _state;
            }

            var now = _options.TimeProvider.GetUtcNow();
            if (!force && _accountsFileLocked && now < _retryLoadAfter)
            {
                // The file was just found held: do not touch it (and wait on the mutex) again on every read, which may be the UI thread.
                return AccountsFile.Empty;
            }

            var wasLocked = _accountsFileLocked;
            var outcome = _registry.LoadWithOutcome();
            var loaded = Reconcile(outcome.File);
            // |=, not =: a notice from a set-aside that no change has yet reported (a failed write after it, below) must survive the next load.
            _accountsFileSetAside |= outcome.QuarantinedTo is not null;
            // An unreadable file (locked) gives an empty list that is not the truth: do not keep it as the state, read the file again after the window.
            _accountsFileLocked = outcome.Unreadable;
            _accountsFileFolderProblem = outcome.QuarantineFailed;
            if (_accountsFileLocked)
            {
                _retryLoadAfter = now + LockedRetryWindow;
            }
            else
            {
                _state = loaded;
                becameReadable = wasLocked;
            }

            return loaded;
        }
    }

    /// <summary>
    /// Anything that would act on the account list while it could not be read (a token request, a sign-out) fails with
    /// <see cref="AuthErrorCodes.AccountsFileLocked"/> instead of acting on the empty stand-in, which would read as "signed out" or "sign in again".
    /// </summary>
    private void ThrowIfAccountsFileLocked()
    {
        _ = State;
        lock (_loadLock)
        {
            if (_accountsFileLocked)
            {
                // A damaged file the folder would not let us move aside is a folder problem (Try again never fixes it); anything else is a hold that can clear.
                throw _accountsFileFolderProblem
                    ? new AccountAuthException(AuthErrorCodes.StorageFailed, "accounts.json is damaged and could not be set aside (the folder is not writable)")
                    : new AccountAuthException(AuthErrorCodes.AccountsFileLocked, "accounts.json exists but could not be read (held open elsewhere)");
            }
        }
    }

    /// <summary>
    /// Issue #616: when <c>accounts.json</c> could not be parsed, <see cref="AccountRegistry"/> set it aside as <c>accounts.json.bad</c> and the list starts empty.
    /// The first change to the list (sign-in, project choice, switch) says so once, with <see cref="AuthErrorCodes.AccountsFileUnreadable"/>, before anything is written
    /// or any browser opened; the user then signs in again against the empty list. Reads never throw it.
    /// </summary>
    private void ThrowIfAccountsFileWasSetAside()
    {
        ThrowIfAccountsFileLocked();
        lock (_loadLock)
        {
            if (!_accountsFileSetAside)
            {
                return;
            }

            _accountsFileSetAside = false;
        }

        throw new AccountAuthException(AuthErrorCodes.AccountsFileUnreadable, "accounts.json could not be parsed and was set aside as accounts.json.bad");
    }

    public async Task SignInAsync(CancellationToken cancellationToken)
    {
        await _signInGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfAccountsFileWasSetAside();
            var client = _options.ClientLoader.Load();
            var token = await AuthorizeInBrowserAsync(client, cancellationToken).ConfigureAwait(false);

            var claims = IdTokenClaims.Read(token.IdToken);
            if (claims is not { } identity || !DpapiTokenStore.IsSafeKey(identity.Sub))
            {
                throw new AccountAuthException(AuthErrorCodes.SigninFailed, "the id token carried no usable subject");
            }

            // Granular consent (issue #259): a ticked-off box still signs the user in, so the answer is read, not assumed. Nothing is stored.
            if (!GrantsRequiredScope(token.Scope, missingMeansGranted: false))
            {
                throw new AccountAuthException(AuthErrorCodes.ConsentIncomplete, "the granted scopes lack cloud-platform");
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
                    var email = string.IsNullOrWhiteSpace(identity.Email) ? identity.Sub : identity.Email;
                    await UpdateAsync(file =>
                    {
                        // An account that signs in again (its token expired) keeps the project it chose, as the file has it now (another copy may have chosen one since this copy loaded); a signed-out one was dropped with its record.
                        var record = new AccountRecord(identity.Sub, email, NeedsSignIn: false, file.Accounts.FirstOrDefault(a => a.Sub == identity.Sub)?.ProjectId);
                        return new AccountsFile(identity.Sub, [.. file.Accounts.Where(a => a.Sub != identity.Sub), record]);
                    }).ConfigureAwait(false);
                    // The user has just signed in against the list as it now is: a set-aside this change found is not reported on the next one ("sign in again" straight after signing in).
                    lock (_loadLock)
                    {
                        _accountsFileSetAside = false;
                    }
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
            ThrowIfAccountsFileLocked();
            var active = State.Active;
            if (active is null)
            {
                return true;
            }

            // The token as this copy found it, with its stamp, before anything changes: the list change below happens only if the file is still that token (checked under the
            // registry's lock), and the revoke is of exactly that token, never of one another copy signed in meanwhile.
            var stamp = TokenStamp(active.Sub);
            var token = await _store.GetAsync<TokenResponse>(active.Sub).ConfigureAwait(false);
            if (AfterSignOutStampForTest is { } stampHook)
            {
                await stampHook().ConfigureAwait(false);
            }

            // The list goes first, the token file with it (under the same lock, after the list was saved), then the revoke of the token read above, whatever Google says:
            // the user asked to be signed out. If the list cannot be saved (a lock, a full disk) the account stays listed, signed in, with its token and still valid at Google,
            // rather than listed signed-in with no token.
            var unchanged = true;
            Exception? deleteFailure = null;
            await UpdateAsync(
                file =>
                {
                    if (TokenStamp(active.Sub) != stamp)
                    {
                        // Another copy signed this account in again since: it stays, and nothing of it is revoked or deleted.
                        unchanged = false;
                        return file;
                    }

                    var remaining = file.Accounts.Where(a => a.Sub != active.Sub).ToList();
                    // Another copy may have made a different account current since this one loaded: that choice stands. Only when the current one is going does the next take over.
                    var currentStays = file.ActiveSub is { } current && current != active.Sub && remaining.Any(a => a.Sub == current);
                    var next = remaining.FirstOrDefault(a => !a.NeedsSignIn) ?? remaining.FirstOrDefault();
                    return new AccountsFile(currentStays ? file.ActiveSub : next?.Sub, remaining);
                },
                () => deleteFailure = unchanged ? TryDeleteTokenFile(active.Sub) : null).ConfigureAwait(false);
            if (!unchanged)
            {
                revoked = true;
            }
            else
            {
                if (AfterSignOutListUpdateForTest is { } updateHook)
                {
                    await updateHook().ConfigureAwait(false);
                }

                revoked = await RevokeAsync(active, token, cancellationToken).ConfigureAwait(false);
                if (deleteFailure is not null)
                {
                    throw new TokenStorageException(deleteFailure);
                }
            }
        }        catch (TokenStorageException ex)
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

    public async Task SelectProjectAsync(string projectId, CancellationToken cancellationToken)
    {
        if (!ProjectIdGenerator.IsValid(projectId))
        {
            throw new AccountAuthException(AuthErrorCodes.ProjectInvalid, "the project id is not a legal Google project id");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfAccountsFileWasSetAside();
            var current = State;
            if (current.Active is not { NeedsSignIn: false } active)
            {
                throw new AccountAuthException(AuthErrorCodes.SigninExpired, "no signed-in account");
            }

            // An account the file no longer lists is not put back: it may be one another copy is signing out right now (list entry gone, revoke pending, token file not yet deleted).
            await UpdateAsync(file => file.Accounts.Any(a => a.Sub == active.Sub && !a.NeedsSignIn)
                ? file with { Accounts = [.. file.Accounts.Select(a => a.Sub == active.Sub ? a with { ProjectId = projectId } : a)] }
                : throw new AccountAuthException(AuthErrorCodes.SigninExpired, "the account is no longer signed in (signed out in another copy of the app, or the list lost it)")).ConfigureAwait(false);
        }
        catch (TokenStorageException ex)
        {
            // Not StorageFailure: its code says "your sign-in could not be saved" and offers Sign in again, wrong for a project choice. A lock is still a lock.
            throw new AccountAuthException(ex is AccountsFileLockedException ? AuthErrorCodes.AccountsFileLocked : ex is AccountsFileFolderException ? AuthErrorCodes.StorageFailed : AuthErrorCodes.ProjectSaveFailed, ex.Message, ex);
        }
        finally
        {
            _gate.Release();
        }

        RaiseChanged();
    }

    public async Task SwitchAccountAsync(string sub, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfAccountsFileWasSetAside();
            var current = State;
            if (current.Accounts.All(a => a.Sub != sub))
            {
                throw new AccountAuthException(AuthErrorCodes.AccountNotFound);
            }

            await UpdateAsync(file => file.Accounts.Any(a => a.Sub == sub)
                ? file with { ActiveSub = sub }
                : throw new AccountAuthException(AuthErrorCodes.AccountNotFound)).ConfigureAwait(false);
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
            ThrowIfAccountsFileLocked();
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
                    await ThrowIfRefreshDroppedScopeAsync(active, credential.Token).ConfigureAwait(false);
                    return credential.Token.AccessToken;
                }

                var accessToken = await credential.GetAccessTokenForRequestAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
                await ThrowIfRefreshDroppedScopeAsync(active, credential.Token).ConfigureAwait(false);
                return accessToken;
            }
            catch (AccountAuthException)
            {
                expired = true; // ThrowIfRefreshDroppedScopeAsync marked the account first
                throw;
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
                throw NetworkFailure("the token endpoint could not be reached", ex);
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
            throw NetworkFailure("Google could not be reached", ex);
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

    private async Task<bool> RevokeAsync(AccountRecord account, TokenResponse? token, CancellationToken cancellationToken)
    {
        if (token?.RefreshToken is null)
        {
            return true; // nothing at Google to revoke: the token was already refused or never readable
        }

        try
        {
            // A discarding store: the flow would delete the stored token itself on success, and that may by now be a newer one (SignOutAsync deletes the old one, guarded).
            using var flow = NewFlow(_options.ClientLoader.Load(), new DiscardingDataStore());
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
            HttpClientFactory = _options.HttpHandler is { } handler ? new HandlerFactory(handler) : new Net.ProxyHttpClientFactory(_options.Proxy ?? DnaEntropyGraph.Core.Net.ProxyPolicy.SystemDefault),
        });

    /// <summary>
    /// Google reports the scopes a token grants in the <c>scope</c> field (MEASURED 2026-10-16, developers.google.com/identity/protocols/oauth2/native-app:
    /// "The scopes of access granted by the access_token", and a user "may not grant your app access to all of them"). A sign-in answer must carry it;
    /// a refresh answer that omits it keeps the stored grant (the library keeps the earlier value), one that names scopes without cloud-platform does not.
    /// </summary>
    private static bool GrantsRequiredScope(string? scope, bool missingMeansGranted)
        => string.IsNullOrWhiteSpace(scope) ? missingMeansGranted : scope.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(RequiredScope, StringComparer.Ordinal);

    private async Task ThrowIfRefreshDroppedScopeAsync(AccountRecord account, TokenResponse token)
    {
        if (GrantsRequiredScope(token.Scope, missingMeansGranted: true))
        {
            return;
        }

        await MarkExpiredAsync(account).ConfigureAwait(false);
        throw new AccountAuthException(AuthErrorCodes.ConsentIncomplete, "a refreshed token no longer grants cloud-platform");
    }

    private async Task MarkExpiredAsync(AccountRecord account)
    {
        // The list and the token go together, under the registry's lock, and only if the token is still the one this copy found dead: another copy may have signed this
        // account in again meanwhile (it stores the new token before it lists it), and marking or deleting that would sign it out. The stamp is a hash of the file's bytes.
        var stamp = TokenStamp(account.Sub);
        if (AfterExpiryStampForTest is { } hook)
        {
            await hook().ConfigureAwait(false);
        }

        var unchanged = true;
        Exception? deleteFailure = null;
        await UpdateAsync(
            file =>
            {
                if (TokenStamp(account.Sub) != stamp)
                {
                    unchanged = false;
                    return file;
                }

                return file with { Accounts = [.. file.Accounts.Select(a => a.Sub == account.Sub ? a with { NeedsSignIn = true } : a)] };
            },
            () => deleteFailure = unchanged ? TryDeleteTokenFile(account.Sub) : null).ConfigureAwait(false);
        if (deleteFailure is not null)
        {
            throw new TokenStorageException(deleteFailure);
        }
    }

    /// <summary>
    /// A hash of the token file's bytes (empty when there is none): the files are tiny, and unlike a write time it cannot be equal for two different tokens written in the
    /// same tick. Two reads of the same file give the same stamp; a sign-in of the account in between changes it (a new refresh token is new bytes).
    /// </summary>
    private string TokenStamp(string sub)
    {
        try
        {
            return Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(_store.PathFor(sub))));
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return string.Empty;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "unreadable";
        }
    }

    /// <summary>Deletes the token file; the failure, if any, is handed back (it runs under the registry's lock after the list was saved, so it must not throw there).</summary>
    private Exception? TryDeleteTokenFile(string sub)
    {
        try
        {
            File.Delete(_store.PathFor(sub));
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ex;
        }
    }
    /// <summary>
    /// Issue #620: every change to the account list goes through here and nowhere else. <paramref name="change"/> is applied to the file as it is under
    /// the registry's lock, not to this copy's cached list (which another copy's change can have made stale), and only to the fields it means to change,
    /// so two copies changing different things both persist; where both change one field of one account, the later writer wins that field.
    /// The save runs on a pool thread: it can wait up to 10 s for another copy's lock, and the caller may be a dispatcher.
    /// </summary>
    private async Task UpdateAsync(Func<AccountsFile, AccountsFile> change, Action? afterWrite = null)
    {
        // Filled under the registry's lock, on the pool thread; only read after the task is done. It must not take _loadLock there (a load holds that and waits on the registry's lock).
        string? setAside = null;
        AccountsFile written;
        try
        {
            written = await Task.Run(() => _registry.Update(file => change(Reconcile(file)), where => setAside = where, afterWrite)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (setAside is not null || ex is AccountsFileFolderException)
            {
                // This update found the file damaged (set it aside, issue #616, or could not): the list this copy cached is gone, and the user is told now if the change gave up.
                // One lock for the cache and the flag: a load between them would see no state and no notice.
                var told = setAside is not null && ex is AccountAuthException;
                lock (_loadLock)
                {
                    _state = null;
                    _accountsFileSetAside |= setAside is not null && !told;
                }

                if (told)
                {
                    throw new AccountAuthException(AuthErrorCodes.AccountsFileUnreadable, "accounts.json could not be parsed and was set aside as accounts.json.bad", ex);
                }
            }

            throw;
        }

        lock (_loadLock)
        {
            _state = written;
            _accountsFileSetAside |= setAside is not null;
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

    /// <summary>A failed request to Google: a proxy that refused it or a certificate this PC does not trust (issue #239) is named as such; anything else is the plain network code.</summary>
    private static AccountAuthException NetworkFailure(string message, Exception ex)
        => new(ProxyFailure.Detect(ex) switch
        {
            ProxyFailureKind.ProxyRefused => AuthErrorCodes.SigninProxy,
            ProxyFailureKind.CertificateUntrusted => AuthErrorCodes.SigninCertificate,
            _ => AuthErrorCodes.NetworkUnavailable,
        }, message, ex);

    /// <summary>Only the exception type is kept: its message can carry a path with the user's name in it.</summary>
    private static AccountAuthException StorageFailure(TokenStorageException ex)
        => new(ex is AccountsFileLockedException ? AuthErrorCodes.AccountsFileLocked : AuthErrorCodes.StorageFailed, ex.Message, ex);

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
