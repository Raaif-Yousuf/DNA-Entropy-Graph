namespace DnaEntropyGraph.Core.Cloud;

/// <summary>
/// The codes a sign-in, sign-out or token call fails under (issue #48). A failure carries a code; the English
/// lives in <c>Resources.resw</c> under <see cref="ResourceKey"/> and names one action (Hard Rule 13). The raw
/// exception text is never shown and never carries a token or an email.
/// </summary>
public static class AuthErrorCodes
{
    /// <summary>Google refused the stored refresh token (<c>invalid_grant</c>: revoked, password changed, or unused for six months). Action: Sign in again.</summary>
    public const string SigninExpired = "SIGNIN_EXPIRED";

    /// <summary>The user declined on Google's page, or closed it.</summary>
    public const string SigninCancelled = "SIGNIN_CANCELLED";

    /// <summary>The browser page was never completed within the time allowed.</summary>
    public const string SigninTimeout = "SIGNIN_TIMEOUT";

    /// <summary>Google answered, but not with a usable account (no id token, no refresh token, a malformed answer).</summary>
    public const string SigninFailed = "SIGNIN_FAILED";

    /// <summary>Google could not be reached. The stored sign-in is kept: this is not an expiry.</summary>
    public const string NetworkUnavailable = "SIGNIN_NETWORK";

    /// <summary>This build has no <c>oauth_client.local.json</c> (Hard Rule 12: it is injected at build time, never committed).</summary>
    public const string OAuthClientMissing = "OAUTH_CLIENT_MISSING";

    /// <summary>The OAuth client file exists but is not a Google Desktop-app client download.</summary>
    public const string OAuthClientInvalid = "OAUTH_CLIENT_INVALID";

    /// <summary>The system browser could not be opened (no default browser registered, or Windows refused).</summary>
    public const string BrowserUnavailable = "SIGNIN_BROWSER";

    /// <summary>The app could not listen on this computer for Google's reply (a security program or a policy blocked it).</summary>
    public const string LoopbackUnavailable = "SIGNIN_LOOPBACK";

    /// <summary>The sign-in could not be saved to, or read from, the app's folder (disk full, read-only, no access).</summary>
    public const string StorageFailed = "SIGNIN_STORAGE";

    /// <summary>A switch to an account this PC has no sign-in for.</summary>
    public const string AccountNotFound = "ACCOUNT_NOT_FOUND";

    /// <summary>The project id the wizard offered is not a legal Google project id (empty, wrong characters or length). Nothing was stored.</summary>
    public const string ProjectInvalid = "PROJECT_INVALID";

    /// <summary>The chosen project could not be saved to the app's folder (disk full, read-only, no access). Nothing was stored. Action: Try again.</summary>
    public const string ProjectSaveFailed = "PROJECT_SAVE_FAILED";

    /// <summary>Every code a sign-in can fail under.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        SigninExpired, SigninCancelled, SigninTimeout, SigninFailed, NetworkUnavailable,
        OAuthClientMissing, OAuthClientInvalid, AccountNotFound, BrowserUnavailable, LoopbackUnavailable, StorageFailed, ProjectInvalid, ProjectSaveFailed,
    ];

    /// <summary>The <c>Resources.resw</c> key for a code's message (plain, non-dotted, literal so the orphan-resource scan sees it).</summary>
    public static string ResourceKey(string? code) => code switch
    {
        SigninExpired => "AuthError_SIGNIN_EXPIRED",
        SigninCancelled => "AuthError_SIGNIN_CANCELLED",
        SigninTimeout => "AuthError_SIGNIN_TIMEOUT",
        SigninFailed => "AuthError_SIGNIN_FAILED",
        NetworkUnavailable => "AuthError_SIGNIN_NETWORK",
        OAuthClientMissing => "AuthError_OAUTH_CLIENT_MISSING",
        OAuthClientInvalid => "AuthError_OAUTH_CLIENT_INVALID",
        AccountNotFound => "AuthError_ACCOUNT_NOT_FOUND",
        BrowserUnavailable => "AuthError_SIGNIN_BROWSER",
        LoopbackUnavailable => "AuthError_SIGNIN_LOOPBACK",
        StorageFailed => "AuthError_SIGNIN_STORAGE",
        ProjectInvalid => "AuthError_PROJECT_INVALID",
        ProjectSaveFailed => "AuthError_PROJECT_SAVE_FAILED",
        _ => "AuthError_SIGNIN_FAILED",
    };

    /// <summary>
    /// The <c>Resources.resw</c> key for the label of the button that carries the one action, or null when the
    /// action is not a button (the message itself says it).
    /// </summary>
    public static string? ActionResourceKey(string? code) => code switch
    {
        SigninExpired or SigninCancelled or SigninTimeout or SigninFailed or AccountNotFound or BrowserUnavailable or LoopbackUnavailable or StorageFailed => "AuthAction_SignInAgain",
        NetworkUnavailable or ProjectSaveFailed => "AuthAction_TryAgain",
        _ => null,
    };
}
