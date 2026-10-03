using DnaEntropyGraph.Core.Abstractions;

namespace DnaEntropyGraph.Core.Runs;

/// <summary>
/// Reads a finished run's own output folder for the Results page (issue #102). Read-only (Hard Rule 14): it lists and reads,
/// never writes, never follows a link out of the folder.
/// </summary>
public sealed class RunOutputReader : IRunOutputReader
{
    private static readonly EnumerationOptions Recursive = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,

        // A link could lead outside the run's folder; the page lists only what is really inside it.
        AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System,
    };

    public RunOutputSnapshot? Read(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            return null;
        }

        var root = Path.GetFullPath(folder);
        var files = Directory.EnumerateFiles(root, "*", Recursive)
            .Select(full => new RunOutputFile(Path.GetRelativePath(root, full).Replace('\\', '/'), full, new FileInfo(full).Length))
            .OrderBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var summaries = files.Where(f => IsSummary(f.RelativePath)).Select(ReadSummary).ToList();
        return new RunOutputSnapshot(files, summaries);
    }

    private static bool IsSummary(string relativePath)
    {
        var name = relativePath[(relativePath.LastIndexOf('/') + 1)..];
        return name.Equals("stats.txt", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".summary.txt", StringComparison.OrdinalIgnoreCase);
    }

    private static RunSummaryFile ReadSummary(RunOutputFile file)
    {
        try
        {
            return new RunSummaryFile(file.RelativePath, RunSummaryReader.Parse(File.ReadAllText(file.FullPath)));
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return new RunSummaryFile(file.RelativePath, null);
        }
    }
}
