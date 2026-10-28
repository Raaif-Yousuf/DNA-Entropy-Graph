namespace DnaEntropyGraph.Persistence;

/// <summary>
/// The run history database was written by a newer version of the app than the one now running
/// (issue #842: a downgrade). The file is valid, so it is never quarantined or deleted; the start-up
/// path shows a message that tells the user to install the latest version.
/// </summary>
public sealed class DatabaseNewerThanAppException(long databaseVersion, long appVersion)
    : InvalidOperationException(
        $"The run history database is at schema version {databaseVersion}, newer than this version of the app understands ({appVersion}). " +
        "It was written by a newer DNA Entropy Graph. Install the latest version; do not delete the database.")
{
    public long DatabaseVersion { get; } = databaseVersion;

    public long AppVersion { get; } = appVersion;
}
