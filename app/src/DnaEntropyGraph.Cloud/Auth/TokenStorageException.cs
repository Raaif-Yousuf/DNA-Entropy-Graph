namespace DnaEntropyGraph.Cloud.Auth;

/// <summary>The app's own folder could not be read or written (disk full, read-only, no access, DPAPI refused). Thrown only by <see cref="DpapiTokenStore"/> and <see cref="AccountRegistry"/>, so the service maps exactly these to SIGNIN_STORAGE and nothing else.</summary>
public class TokenStorageException : Exception
{
    public TokenStorageException(Exception inner)
        : base(inner.GetType().Name, inner)
    {
    }
}

/// <summary>
/// Not a disk problem: another program or copy of the app holds <c>accounts.json</c>'s lock (or the system refused to open it), so nothing was written
/// and trying again later can work. Kept apart from <see cref="TokenStorageException"/> because the advice differs ("free disk space" versus "close the other copy").
/// </summary>
public sealed class AccountsFileLockedException : TokenStorageException
{
    public AccountsFileLockedException(Exception inner)
        : base(inner)
    {
    }
}
