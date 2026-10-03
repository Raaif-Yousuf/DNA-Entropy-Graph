using DnaEntropyGraph.Core.Abstractions;

namespace DnaEntropyGraph.Core.Runs;

/// <summary>
/// Reads a finished run's own output folder for the Results page (issue #102). Read-only (Hard Rule 14): it lists and reads,
/// never writes. Containment never depends on attribute bits (a user can set Offline on a symlink with <c>attrib +O</c>):
/// every reparse point is asked for its link target. One that has a target is a symlink or junction, so a linked folder is
/// never entered and a linked file is listed only when its target is strictly inside the folder. Only a reparse point with
/// no link target (a OneDrive "online-only" placeholder) is the user's own file or folder and is listed.
/// </summary>
public sealed class RunOutputReader : IRunOutputReader
{
    // Win32 FILE_ATTRIBUTE_RECALL_ON_OPEN and FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS: .NET has no enum members for them.
    // Kept public for the tests that build placeholder attribute sets; the reader itself no longer trusts them.
    public const FileAttributes RecallOnOpen = (FileAttributes)0x00040000;
    public const FileAttributes RecallOnDataAccess = (FileAttributes)0x00400000;

    /// <summary>No run writes folders nested anywhere near this deep; the cap is a backstop against any loop the link checks missed.</summary>
    public const int MaxDepth = 32;

    // A link whose target could not be worked out is still a link: it is returned as a target that is inside nothing.
    private const string UnresolvableLink = "?";

    private readonly Func<string, FileAttributes> _attributesOf;
    private readonly Func<string, string?> _linkTarget;
    private readonly Func<string, long?> _sizeOf;

    public RunOutputReader()
        : this(File.GetAttributes, ResolveLinkTarget)
    {
    }

    /// <summary>The disk questions, injectable because a real cloud placeholder or an unreadable file cannot be made in a test.</summary>
    public RunOutputReader(Func<string, FileAttributes> attributesOf, Func<string, string?> linkTarget, Func<string, long?>? sizeOf = null)
    {
        _attributesOf = attributesOf;
        _linkTarget = linkTarget;
        _sizeOf = sizeOf ?? SizeOf;
    }

    public RunOutputSnapshot? Read(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            return null;
        }

        var root = Path.GetFullPath(folder);
        var files = new List<RunOutputFile>();
        Walk(root, root, 0, files);
        files.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.RelativePath, b.RelativePath));

        var summaries = files.Where(f => IsSummary(f.RelativePath)).Select(ReadSummary).ToList();
        return new RunOutputSnapshot(files, summaries);
    }

    private void Walk(string root, string directory, int depth, List<RunOutputFile> files)
    {
        if (depth > MaxDepth)
        {
            return;
        }

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

            var target = attributes.HasFlag(FileAttributes.ReparsePoint) ? _linkTarget(entry) : null;
            var isLink = target is not null;
            if (attributes.HasFlag(FileAttributes.Directory))
            {
                // A linked folder could lead anywhere, or back to a parent; a placeholder folder is a real folder of this run.
                if (!isLink)
                {
                    Walk(root, entry, depth + 1, files);
                }

                continue;
            }

            if (isLink && !IsInside(target, root))
            {
                continue;
            }

            files.Add(new RunOutputFile(Path.GetRelativePath(root, entry).Replace('\\', '/'), entry, _sizeOf(entry)));
        }
    }

    private static bool IsInside(string? target, string root) => !string.IsNullOrEmpty(target) && target != UnresolvableLink && RunOutputRoot.IsStrictlyInside(target, root);

    /// <summary>Null when the size cannot be read, so the page says "size unknown" rather than claiming an empty file.</summary>
    private static long? SizeOf(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
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
            return UnresolvableLink;
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
