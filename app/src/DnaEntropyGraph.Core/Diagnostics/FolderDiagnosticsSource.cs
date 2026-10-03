namespace DnaEntropyGraph.Core.Diagnostics;

/// <summary>
/// The real app data folder. It only ever looks at <c>settings.json</c> and the <c>logs</c> and <c>runs</c> trees: the
/// token folder (<c>auth</c>), the copies of the user's inputs (<c>inputs</c>, <c>pasted</c>) and the database are not
/// even listed, so a mistake in the builder's path rules still cannot reach them.
/// </summary>
public sealed class FolderDiagnosticsSource(string root) : IDiagnosticsSource
{
    private static readonly string[] Trees = ["logs", "runs"];

    private static readonly EnumerationOptions Options = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System,
    };

    public IReadOnlyList<string> ListFiles()
    {
        var files = new List<string>();
        if (File.Exists(Path.Combine(root, "settings.json")))
        {
            files.Add("settings.json");
        }

        foreach (var tree in Trees)
        {
            var directory = Path.Combine(root, tree);
            if (!Directory.Exists(directory))
            {
                continue;
            }

            files.AddRange(Directory.EnumerateFiles(directory, "*", Options)
                .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/')));
        }

        return files;
    }

    public DiagnosticsFile? TryRead(string relativePath, long maxBytes)
    {
        var full = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!full.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            // ReadWrite sharing: a log the app is still appending to must not make the whole bundle fail.
            using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var length = stream.Length;
            var truncated = length > maxBytes;
            var count = (int)Math.Min(length, maxBytes);
            if (truncated)
            {
                stream.Seek(length - count, SeekOrigin.Begin);
            }

            var buffer = new byte[count];
            stream.ReadExactly(buffer);
            return new DiagnosticsFile(buffer, truncated);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
