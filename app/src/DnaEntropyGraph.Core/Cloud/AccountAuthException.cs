namespace DnaEntropyGraph.Core.Cloud;

/// <summary>
/// A sign-in, token or account call failed in a way the user can act on. <see cref="Code"/> is one of
/// <see cref="AuthErrorCodes"/>; the message is for logs only and never carries a token or an email.
/// </summary>
public sealed class AccountAuthException : Exception
{
    public AccountAuthException(string code, string? detail = null, Exception? inner = null, string? accountSub = null)
        : base(detail is null ? code : $"{code}: {detail}", inner)
    {
        Code = code;
        AccountSub = accountSub;
    }

    public string Code { get; }

    /// <summary>Issue #830: the account (its stable sub, never its email) the failure is about, when the call was made as a specific one; null otherwise. A run surface looks the email up to say "Sign in as ...".</summary>
    public string? AccountSub { get; }
}
