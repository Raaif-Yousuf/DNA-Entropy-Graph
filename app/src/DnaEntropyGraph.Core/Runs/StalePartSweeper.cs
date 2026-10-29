using System.Text.RegularExpressions;

namespace DnaEntropyGraph.Core.Runs;

/// <summary>
/// Deletes the app's OWN download temp files older than a day: a crash or an abandoned attempt leaves one, and it never carries a final name.
/// Hard Rule 14 (the user's files are read-only to us) sets the limits: only names the downloader generates
/// (<see cref="Cloud.StreamedDownload.TempPathFor"/>: <c>&lt;file&gt;.&lt;12 lowercase hex&gt;.part</c>), only in the run folder and in the folders a result file
/// lives in (never a recursive walk), and never through a junction or symlink.
/// </summary>
internal static partial class StalePartSweeper
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(1);

    [GeneratedRegex(@"\.[0-9a-f]{12}\.part$", RegexOptions.CultureInvariant)]
    private static partial Regex OwnName();

    /// <summary>True when <paramref name="fileName"/> has the shape the downloader generates, so a user's own <c>x.part</c> is never matched.</summary>
    internal static bool IsOwnTempName(string fileName) => fileName.Length > ".0123456789ab.part".Length && OwnName().IsMatch(fileName);

    /// <summary>
    /// Sweeps <paramref name="folder"/> and each of <paramref name="subDirectories"/> (relative to it, the folders a result file lives in), without descending further.
    /// File times are real file-system times, so the cutoff is measured against the real clock; <paramref name="nowUtc"/> is a test seam for that same clock.
    /// </summary>
    public static void Sweep(string folder, IEnumerable<string> subDirectories, DateTime? nowUtc = null)
    {
        try
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
            if (!Directory.Exists(root))
            {
                return;
            }

            var cutoff = (nowUtc ?? DateTime.UtcNow) - MaxAge;
            SweepOne(root, cutoff);
            foreach (var sub in subDirectories.Where(s => !string.IsNullOrEmpty(s)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(root, sub)));
                if (full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && NoReparseBetween(root, full))
                {
                    SweepOne(full, cutoff);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // A sweep is housekeeping; it never fails a download.
        }
    }

    /// <summary>True when <paramref name="path"/> exists and neither it nor any folder between <paramref name="root"/> and it is a junction or symlink.</summary>
    internal static bool NoReparseBetween(string root, string path)
    {
        var current = path;
        while (current.Length > root.Length)
        {
            var info = new DirectoryInfo(current);
            if (!info.Exists || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return false;
            }

            current = Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(current) ?? root);
        }

        return true;
    }

    private static void SweepOne(string directory, DateTime cutoff)
    {
        foreach (var path in Directory.EnumerateFiles(directory, "*.part", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var info = new FileInfo(path);
                if (IsOwnTempName(info.Name) && !info.Attributes.HasFlag(FileAttributes.ReparsePoint) && info.LastWriteTimeUtc < cutoff)
                {
                    info.Delete();
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // In use or not ours to delete: leave it.
            }
        }
    }
}
