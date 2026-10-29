namespace DnaEntropyGraph.Core;

/// <summary>
/// Stores a path that lies inside the portable folder relative to it (<c>portable:outputs\run</c>) and expands it again on read, so a stick
/// that gets another drive letter still finds its results (issue #144). A path outside the portable folder (an output folder the user
/// chose) is stored as given, and with no portable folder nothing is changed.
/// </summary>
public sealed class DataPathMapper(string? portableBase)
{
    public const string Prefix = "portable:";

    public string? ToStored(string? path)
    {
        if (portableBase is null || string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }

        var root = Runs.RunOutputRoot.WithTrailingSeparator(portableBase);
        if (string.Equals(Runs.RunOutputRoot.WithTrailingSeparator(full), root, StringComparison.OrdinalIgnoreCase))
        {
            return Prefix;
        }

        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase)
            ? Prefix + full[root.Length..]
            : path;
    }

    public string? FromStored(string? stored)
    {
        if (portableBase is null || stored is null || !stored.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return stored;
        }

        var relative = stored[Prefix.Length..];
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(portableBase));
        if (relative.Length == 0)
        {
            return root;
        }

        // The stored text comes from the history database. A rooted or climbing remainder ("portable:..\..\x", "portable:C:\x")
        // must not expand to a place outside the portable folder (Hard Rule 14). It reads back as null, "no output folder". MEASURED 2026-10-29 by grep: the consumers of RunRecord.OutputDir
        // (RunCloudResults, LocalRunFiles, JobReconciler, RunCompletionActions, LocalJobRunner) test for null or empty, whereas
        // the raw text would be a cwd-relative path with a colon in it.
        try
        {
            var expanded = Path.Combine(root, relative);
            return Runs.RunOutputRoot.IsStrictlyInside(expanded, root) ? expanded : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }
}
