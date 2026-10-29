namespace DnaEntropyGraph.Core.Runs;

/// <summary>
/// Renames a file the user may have changed to a free <c>name (changed time).ext</c> before a download takes its place (Hard Rule 14: a result
/// download never overwrites a file on disk). Shared by the History re-download and the run's own download.
/// </summary>
internal static class ChangedFileAside
{
    private const int MaxTries = 50;

    private static string Candidate(string path, int attempt, TimeProvider time)
    {
        var stamp = time.GetLocalNow().ToString("yyyy-MM-dd HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        var stem = Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path) + " (changed " + stamp);
        return (attempt == 1 ? stem + ")" : $"{stem} {attempt})") + Path.GetExtension(path);
    }

    /// <summary>The most time spent looking for a free name; a clock the caller injects (the same one the name's timestamp uses) keeps this testable.</summary>
    public static readonly TimeSpan MaxTotal = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Moves <paramref name="path"/> to a free changed-name and returns it. A name that is taken (found taken, or taken between the check and the move) is not an error:
    /// the next counter is tried at once. Any other failure (a sharing violation the mover already retried on this same name) is thrown as it is, because another
    /// name would not release the file. The whole search is bounded by <see cref="MaxTotal"/>.
    /// </summary>
    public static string Move(string path, TimeProvider time, IRunFileMover mover)
    {
        var started = time.GetTimestamp();
        for (var attempt = 1; attempt <= MaxTries; attempt++)
        {
            var candidate = Candidate(path, attempt, time);
            if (File.Exists(candidate))
            {
                if (time.GetElapsedTime(started) > MaxTotal)
                {
                    break;
                }

                continue;
            }

            try
            {
                mover.Move(path, candidate);
                return candidate;
            }
            catch (IOException) when (File.Exists(candidate) && attempt < MaxTries && time.GetElapsedTime(started) <= MaxTotal)
            {
                // Taken between the check and the move: the next name.
            }
        }

        throw new IOException("No free name was found to keep the changed file under.");
    }
}