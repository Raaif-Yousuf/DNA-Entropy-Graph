namespace DnaEntropyGraph.Core.Runs;

/// <summary>Deletes download temp files (<c>*.part</c>) older than a day from a run's output folder: a crash or an abandoned attempt leaves one, and it never carries a final name.</summary>
internal static class StalePartSweeper
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(1);

    public static void Sweep(string folder, TimeProvider time)
    {
        try
        {
            if (!Directory.Exists(folder))
            {
                return;
            }

            var cutoff = time.GetUtcNow().UtcDateTime - MaxAge;
            foreach (var path in Directory.EnumerateFiles(folder, "*.part", SearchOption.AllDirectories))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(path) < cutoff)
                    {
                        File.Delete(path);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // In use or not ours to delete: leave it.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A sweep is housekeeping; it never fails a download.
        }
    }
}
