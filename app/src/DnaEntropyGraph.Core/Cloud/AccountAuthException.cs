namespace DnaEntropyGraph.Core.Cloud;

/// <summary>
/// A sign-in, token or account call failed in a way the user can act on. <see cref="Code"/> is one of
/// <see cref="AuthErrorCodes"/>; the message is for logs only and never carries a token or an email.
/// </summary>
public sealed class AccountAuthException : Exception
{
    public AccountAuthException(string code, string? detail = null, Exception? inner = null)
        : base(detail is null ? code : $"{code}: {detail}", inner)
    {
        Code = code;
    }

    public string Code { get; }
}
