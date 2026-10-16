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

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(portableBase));
        if (string.Equals(Path.TrimEndingDirectorySeparator(full), root, StringComparison.OrdinalIgnoreCase))
        {
            return Prefix;
        }

        return full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            ? Prefix + full[(root.Length + 1)..]
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
        return relative.Length == 0 ? root : Path.Combine(root, relative);
    }
}
