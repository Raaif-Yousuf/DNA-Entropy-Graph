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
