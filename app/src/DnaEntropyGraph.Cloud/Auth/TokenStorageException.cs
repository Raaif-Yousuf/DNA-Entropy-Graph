namespace DnaEntropyGraph.Cloud.Auth;

/// <summary>The app's own folder could not be read or written (disk full, read-only, no access, DPAPI refused). Thrown only by <see cref="DpapiTokenStore"/> and <see cref="AccountRegistry"/>, so the service maps exactly these to SIGNIN_STORAGE and nothing else.</summary>
public sealed class TokenStorageException : Exception
{
    public TokenStorageException(Exception inner)
        : base(inner.GetType().Name, inner)
    {
    }
}
