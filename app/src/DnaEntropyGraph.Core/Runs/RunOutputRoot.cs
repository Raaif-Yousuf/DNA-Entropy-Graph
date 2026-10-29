using DnaEntropyGraph.Core.Abstractions;

namespace DnaEntropyGraph.Core.Runs;

/// <summary>Where a run's own output folder may live, and the one containment test every delete or write uses.</summary>
public static class RunOutputRoot
{
    /// <summary>The folder a run's output folder was created under: the run's own choice, else the default parent.</summary>
    public static string Resolve(RunRecord run, Func<string> defaultParent)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(defaultParent);
        var chosen = RunOptionsJson.TryDeserialize(run.OptionsJson)?.OutputFolder;
        return string.IsNullOrWhiteSpace(chosen) ? defaultParent() : chosen;
    }

    /// <summary>The fully resolved path with exactly one trailing separator, whether or not it was a drive root (<c>D:\</c> already ends with one).</summary>
    public static string WithTrailingSeparator(string path)
    {
        var trimmed = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        return trimmed.EndsWith(Path.DirectorySeparatorChar) ? trimmed : trimmed + Path.DirectorySeparatorChar;
    }

    /// <summary>
    /// True when <paramref name="path"/> is strictly below <paramref name="root"/> once both are fully resolved (so <c>..</c> cannot climb out and a lookalike sibling name does not match).
    /// The test is lexical: a junction or symlink inside the root is not followed (see <c>LocalRunFiles</c> for the reparse-point check).
    /// </summary>
    public static bool IsStrictlyInside(string path, string root)
    {
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var prefix = WithTrailingSeparator(root);
        return fullPath.Length > prefix.Length && fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True when <paramref name="path"/> is a drive root (<c>C:\</c>) or a UNC share root (<c>\\server\share</c>), once resolved.</summary>
    public static bool IsVolumeRoot(string path)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var volume = Path.TrimEndingDirectorySeparator(Path.GetPathRoot(full) ?? string.Empty);
        return string.Equals(full, volume, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// <see cref="IsStrictlyInside"/> for anything that writes or deletes: a volume root is never accepted as the root, so a row
    /// that names <c>C:\Windows\Temp\x</c> is not "inside" an output folder of <c>C:\</c> (Hard Rule 14). DECISION (agent-made, reversible):
    /// a user who picked a whole drive as the output folder can still run, but cannot delete or re-download through the History row.
    /// </summary>
    public static bool IsSafelyInside(string path, string root) => !IsVolumeRoot(root) && IsStrictlyInside(path, root);

    /// <summary>True when the two resolved paths are the same or either contains the other.</summary>
    public static bool Overlaps(string a, string b)
    {
        var fullA = Path.TrimEndingDirectorySeparator(Path.GetFullPath(a));
        var fullB = Path.TrimEndingDirectorySeparator(Path.GetFullPath(b));
        return string.Equals(fullA, fullB, StringComparison.OrdinalIgnoreCase)
               || IsStrictlyInside(fullA, fullB)
               || IsStrictlyInside(fullB, fullA);
    }
}
