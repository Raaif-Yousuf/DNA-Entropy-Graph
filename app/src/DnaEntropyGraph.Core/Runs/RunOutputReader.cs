using DnaEntropyGraph.Core.Abstractions;

namespace DnaEntropyGraph.Core.Runs;

/// <summary>
/// Reads a finished run's own output folder for the Results page (issue #102). Read-only (Hard Rule 14): it lists and reads,
/// never writes. A symlink or junction that leads outside the folder is never listed or entered; a OneDrive "online-only"
/// placeholder (a reparse point carrying the offline or recall attributes) is the user's own file and is listed.
/// </summary>
public sealed class RunOutputReader : IRunOutputReader
{
    // Win32 FILE_ATTRIBUTE_RECALL_ON_OPEN and FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS: .NET has no enum members for them.
    public const FileAttributes RecallOnOpen = (FileAttributes)0x00040000;
    public const FileAttributes RecallOnDataAccess = (FileAttributes)0x00400000;
    private const FileAttributes CloudPlaceholder = FileAttributes.Offline | RecallOnDataAccess | RecallOnOpen;

    private readonly Func<string, FileAttributes> _attributesOf;
    private readonly Func<string, string?> _linkTarget;

    public RunOutputReader()
        : this(File.GetAttributes, ResolveLinkTarget)
    {
    }

    /// <summary>The two disk questions about links, injectable because a real cloud placeholder cannot be made in a test.</summary>
    public RunOutputReader(Func<string, FileAttributes> attributesOf, Func<string, string?> linkTarget)
    {
        _attributesOf = attributesOf;
        _linkTarget = linkTarget;
    }

    public RunOutputSnapshot? Read(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            return null;
        }

        var root = Path.GetFullPath(folder);
        var files = new List<RunOutputFile>();
        Walk(root, root, files);
        files.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.RelativePath, b.RelativePath));

        var summaries = files.Where(f => IsSummary(f.RelativePath)).Select(ReadSummary).ToList();
        return new RunOutputSnapshot(files, summaries);
    }

    private void Walk(string root, string directory, List<RunOutputFile> files)
    {
        IEnumerable<string> entries;
        try
        {
            entries = Directory.EnumerateFileSystemEntries(directory).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        foreach (var entry in entries)
        {
            FileAttributes attributes;
            try
            {
                attributes = _attributesOf(entry);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            var isLink = attributes.HasFlag(FileAttributes.ReparsePoint) && (attributes & CloudPlaceholder) == 0;
            if (attributes.HasFlag(FileAttributes.Directory))
            {
                // A linked folder could lead anywhere; a cloud placeholder folder is a real folder of this run.
                if (!isLink)
                {
                    Walk(root, entry, files);
                }

                continue;
            }

            if (isLink && !IsInside(_linkTarget(entry), root))
            {
                continue;
            }

            files.Add(new RunOutputFile(Path.GetRelativePath(root, entry).Replace('\\', '/'), entry, SizeOf(entry)));
        }
    }

    private static bool IsInside(string? target, string root) => target is not null && RunOutputRoot.IsStrictlyInside(target, root);

    private static long SizeOf(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static string? ResolveLinkTarget(string path)
    {
        try
        {
            return new FileInfo(path).ResolveLinkTarget(returnFinalTarget: true)?.FullName;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
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
