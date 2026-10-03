namespace DnaEntropyGraph.Core.Abstractions;

/// <summary>
/// The settings file (or the lock that guards it) stayed unavailable after the
/// store's bounded retry. Nothing was changed on disk. Callers that run on the
/// UI thread or on close must catch this; a settings read is never worth a
/// crash (#558).
/// </summary>
public class SettingsUnavailableException : IOException
{
    public SettingsUnavailableException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}

/// <summary>
/// The <c>installation_id</c> file exists but does not hold a valid id. We never
/// mint a replacement (a new id orphans every labelled cloud resource, Hard
/// Rules 9 and 10); the bad file is kept aside and this is raised so the
/// problem is surfaced instead (recovery UX: DECISION #404).
/// </summary>
public sealed class InstallationIdUnusableException : IOException
{
    public InstallationIdUnusableException(string message)
        : base(message)
    {
    }
}
