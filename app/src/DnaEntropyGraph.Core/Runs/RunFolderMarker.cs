using System.Text;

namespace DnaEntropyGraph.Core.Runs;

/// <summary>What a run folder's marker says about the run that is asking (issue #934).</summary>
public enum RunFolderMarkerState
{
    /// <summary>No marker: a folder from before markers existed, or one the user made. Never proof the app made it.</summary>
    Absent,

    /// <summary>The marker names this run's job id: the app made this folder for this run.</summary>
    Matches,

    /// <summary>The marker names another job, or cannot be trusted (a folder, a link, unreadable): not this run's folder as far as anyone can prove.</summary>
    OtherRun,
}

/// <summary>
/// The small file (<see cref="FileName"/>, holding the job id) every run output folder the app creates carries (issue #934, Hard Rule 14). A history row names its own
/// output folder and the folder it must lie inside, so a damaged row can pass a containment test and still point at the user's own folder; the marker is the one thing
/// on the disk that only the app wrote for that job. A recursive delete of a run's folder requires it.
/// </summary>
public static class RunFolderMarker
{
    public const string FileName = ".dna-entropy-run";

    // A job id is a short plain name; anything longer than this is not one, and is never read in full.
    private const int MaxMarkerBytes = 512;

    /// <summary>Writes the marker into <paramref name="folder"/> (UTF-8, no byte-order mark, LF). Throws <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/> like any file write.</summary>
    public static void Write(string folder, string jobId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        File.WriteAllText(Path.Combine(folder, FileName), jobId + "\n", new UTF8Encoding(false));
    }

    /// <summary>
    /// Whether <paramref name="folder"/> carries the marker of job <paramref name="jobId"/>. A marker that is not a plain file (a folder, a junction or symlink), cannot be read, or
    /// names anything else (compared exactly) is <see cref="RunFolderMarkerState.OtherRun"/>, never "absent": only a missing file may be confirmed by the user.
    /// </summary>
    public static RunFolderMarkerState Check(string folder, string jobId)
    {
        var path = Path.Combine(folder, FileName);
        try
        {
            if (!Path.Exists(path))
            {
                return RunFolderMarkerState.Absent;
            }

            var info = new FileInfo(path);
            if (!info.Exists || info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.Length > MaxMarkerBytes)
            {
                return RunFolderMarkerState.OtherRun;
            }

            var text = File.ReadAllText(path, Encoding.UTF8).Trim();
            return !string.IsNullOrWhiteSpace(jobId) && string.Equals(text, jobId, StringComparison.Ordinal)
                ? RunFolderMarkerState.Matches
                : RunFolderMarkerState.OtherRun;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return RunFolderMarkerState.OtherRun;
        }
    }

    /// <summary>True when <paramref name="path"/> is the marker file of <paramref name="folder"/> itself (not a same-named file deeper down).</summary>
    public static bool IsMarkerOf(string folder, string path)
        => string.Equals(
            Path.GetFullPath(path),
            Path.GetFullPath(Path.Combine(folder, FileName)),
            StringComparison.OrdinalIgnoreCase);

    /// <summary>True when nothing is in <paramref name="folder"/> except (at most) its marker: the folder of a run that produced nothing.</summary>
    public static bool IsEmptyApartFromMarker(string folder)
        => Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).All(entry => IsMarkerOf(folder, entry));

    /// <summary>
    /// Removes <paramref name="folder"/> only when <see cref="IsEmptyApartFromMarker"/> and the marker is this job's or absent: the marker (a file the app wrote) and then the empty
    /// folder, never anything recursive. A folder holding another run's marker (or one that cannot be trusted) is never removed. Best effort and never throws; false when the folder
    /// was not removed (it holds something, is gone, or is refused), and then its marker is left in place.
    /// </summary>
    public static bool TryRemoveEmptyFolder(string folder, string jobId)
    {
        try
        {
            if (Check(folder, jobId) == RunFolderMarkerState.OtherRun || !IsEmptyApartFromMarker(folder))
            {
                return false;
            }

            var marker = Path.Combine(folder, FileName);
            if (File.Exists(marker))
            {
                File.Delete(marker);
            }

            Directory.Delete(folder, recursive: false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
