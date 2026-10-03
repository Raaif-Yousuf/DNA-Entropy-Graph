namespace DnaEntropyGraph.Core.Viewers;

/// <summary>Finds an installed desktop viewer's program without asking the user (issue #586).</summary>
public interface IViewerLocator
{
    /// <summary>The full path of the viewer's program, or null when it is not where an installer puts it.</summary>
    string? Find(ExternalViewer viewer);
}

/// <summary>
/// Looks in the folders installers use: <c>IGV-&lt;version&gt;</c> (newest version first; <c>igv.exe</c>, else <c>igv.bat</c>) and
/// <c>Geneious*</c>, under each root (Program Files, Program Files (x86), and the per-user Programs folder).
/// </summary>
public sealed class ViewerLocator : IViewerLocator
{
    private readonly Func<IEnumerable<string>> _roots;

    public ViewerLocator()
        : this(DefaultRoots)
    {
    }

    public ViewerLocator(Func<IEnumerable<string>> roots) => _roots = roots;

    public string? Find(ExternalViewer viewer)
    {
        var (prefix, programs) = viewer switch
        {
            ExternalViewer.Igv => ("IGV", new[] { "igv.exe", "igv.bat" }),
            _ => ("Geneious", new[] { "Geneious Prime.exe", "Geneious.exe" }),
        };

        foreach (var root in _roots().Where(r => !string.IsNullOrEmpty(r) && Directory.Exists(r)))
        {
            // Version folders sort as text ("IGV-2.19.7" after "IGV-2.9.4" is wrong) so compare by version when there is one.
            var folders = Directory.EnumerateDirectories(root, prefix + "*")
                .OrderByDescending(VersionOf)
                .ThenByDescending(p => p, StringComparer.OrdinalIgnoreCase);
            foreach (var folder in folders)
            {
                foreach (var program in programs)
                {
                    var candidate = Path.Combine(folder, program);
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
            }
        }

        return null;
    }

    private static IEnumerable<string> DefaultRoots()
    {
        yield return Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs");
    }

    private static Version VersionOf(string folder)
    {
        var name = Path.GetFileName(folder);
        var dash = name.IndexOf('-', StringComparison.Ordinal);
        return dash >= 0 && Version.TryParse(name[(dash + 1)..], out var v) ? v : new Version(0, 0);
    }
}
