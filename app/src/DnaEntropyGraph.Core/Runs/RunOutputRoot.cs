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

    /// <summary>
    /// True when no folder from <paramref name="path"/> up to (not including) <paramref name="root"/> is a junction or symlink, so a write below it cannot land
    /// somewhere else (Hard Rule 14). A component that does not exist yet is fine unless <paramref name="requireExists"/> (the app creates it, as a real folder).
    /// </summary>
    public static bool NoReparseBetween(string root, string path, bool requireExists = false)
    {
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var current = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        while (current.Length > fullRoot.Length && current.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            var info = new DirectoryInfo(current);
            if (info.Exists ? info.Attributes.HasFlag(FileAttributes.ReparsePoint) : requireExists)
            {
                return false;
            }

            current = Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(current) ?? fullRoot);
        }

        return true;
    }

    /// <summary>
    /// The containment test for REUSING a recorded run folder on resume: strictly inside <paramref name="root"/> AND no junction or symlink below it. Unlike <see cref="IsContained"/>
    /// it accepts a drive root as the output folder, because reuse only writes the run's own result files there; a user who picked a whole drive keeps resume.
    /// </summary>
    public static bool IsUnderRootWithoutLinks(string path, string root) => IsStrictlyInside(path, root) && NoReparseBetween(root, path);

    /// <summary>The containment test for a History row that is deleted or re-downloaded through: strictly inside <paramref name="root"/> (a drive root is never accepted as the root, see <see cref="IsSafelyInside"/>) AND no junction or symlink on any component below it.</summary>
    public static bool IsContained(string path, string root) => IsSafelyInside(path, root) && NoReparseBetween(root, path);

    /// <summary>True when <paramref name="path"/> is a drive root such as <c>C:\</c>, once resolved. A UNC share root (<c>\\server\share</c>) is not: a lab NAS share is a normal output folder.</summary>
    public static bool IsDriveRoot(string path)
    {
        var full = Path.GetFullPath(path);

        // A device-path spelling of a drive (\\?\C:\ or \\.\C:\) is the drive, not a UNC share; \\?\UNC\server\share keeps its UNC root.
        if (full.Length >= 6 && (full.StartsWith(@"\\?\", StringComparison.Ordinal) || full.StartsWith(@"\\.\", StringComparison.Ordinal)) && char.IsAsciiLetter(full[4]) && full[5] == ':')
        {
            full = full[4..];
        }

        full = Path.TrimEndingDirectorySeparator(full);
        var root = Path.GetPathRoot(full) ?? string.Empty;
        return !root.StartsWith(@"\\", StringComparison.Ordinal)
               && string.Equals(full, Path.TrimEndingDirectorySeparator(root), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// <see cref="IsStrictlyInside"/> for anything that deletes or re-downloads through a History row: a drive root is never accepted as the root,
    /// so a row naming <c>C:\Windows\Temp\x</c> is not "inside" an output folder of <c>C:\</c> (Hard Rule 14). DECISION (agent-made, reversible):
    /// a user who picked a whole drive as the output folder can still run, but cannot delete or re-download through the History row.
    /// A UNC share root stays a normal root.
    /// </summary>
    public static bool IsSafelyInside(string path, string root) => !IsDriveRoot(root) && IsStrictlyInside(path, root);

    /// <summary>
    /// True when the run's output folder lies inside an output root that is a drive root (issue #954): the one case where <see cref="IsSafelyInside"/> refuses a folder that is
    /// otherwise where it should be, so the History can say why. Purely lexical (no disk access) and never throws: a path that cannot be resolved is false.
    /// </summary>
    public static bool IsDriveRootOutput(RunRecord run, Func<string> defaultParent)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (run.Imported || string.IsNullOrWhiteSpace(run.OutputDir))
        {
            return false;
        }

        try
        {
            var root = Resolve(run, defaultParent);
            return !string.IsNullOrWhiteSpace(root) && IsDriveRoot(root) && IsStrictlyInside(run.OutputDir, root);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>
    /// True when <paramref name="jobId"/> is one plain folder name: not empty, not <c>.</c> or <c>..</c>, and with no separator, colon, NUL or
    /// <c>..</c>. <c>Path.Combine(root, "x\..\y")</c> stays inside <c>root</c> but names another job's folder, so containment alone is not enough.
    /// </summary>
    public static bool IsPlainJobId(string? jobId)
        => !string.IsNullOrWhiteSpace(jobId)
           && jobId is not ("." or "..")
           && jobId.IndexOfAny(['/', '\\', ':', '\0']) < 0
           && !jobId.Contains("..", StringComparison.Ordinal);

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
