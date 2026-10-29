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

    /// <summary>True when <paramref name="path"/> is strictly below <paramref name="root"/> once both are fully resolved (so <c>..</c> cannot climb out and a lookalike sibling name does not match).</summary>
    public static bool IsStrictlyInside(string path, string root)
    {
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        return fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
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

    /// <summary>The one containment test for a recorded run folder: strictly inside <paramref name="root"/> AND no junction or symlink on any component below it.</summary>
    public static bool IsContained(string path, string root) => IsStrictlyInside(path, root) && NoReparseBetween(root, path);

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
