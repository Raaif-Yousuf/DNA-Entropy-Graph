using DnaEntropyGraph.Core.Abstractions;

namespace DnaEntropyGraph.Core.Runs;

public enum LocalDeleteStatus
{
    Deleted,
    NothingToDelete,
    Refused,

    /// <summary>Nothing could be deleted because a file is open in another program.</summary>
    InUse,

    /// <summary>Nothing could be deleted because Windows denied access (a read-only file, or a folder the user may not change).</summary>
    AccessDenied,

    /// <summary>Some files were deleted and some could not be; <see cref="LocalDeleteResult"/> says how many of each.</summary>
    Partial,

    /// <summary>
    /// The run's folder, or a folder between the output folder and it, is a junction or symbolic link. Nothing was deleted and
    /// the link was not removed: following it would reach files somewhere else (Hard Rule 14).
    /// </summary>
    LinkRefused,
}

/// <summary>What a local delete did. The counts are files, not folders, and are set for <see cref="LocalDeleteStatus.Deleted"/> and <see cref="LocalDeleteStatus.Partial"/>.</summary>
public sealed record LocalDeleteResult(LocalDeleteStatus Status, int FilesDeleted = 0, int FilesRemaining = 0);

/// <summary>The run's files on this PC (issue #101). Hard Rule 14: nothing here ever touches the input copy or anything outside the run's own output folder.</summary>
public interface ILocalRunFiles
{
    bool OutputFolderExists(RunRecord run);

    /// <summary>Deletes the run's own output folder, only if it lies strictly inside the folder the run was told to write under. Never the input copy.</summary>
    LocalDeleteResult DeleteOutputFolder(RunRecord run);

    /// <summary>The input a re-run should use: the app's own copy, else the original file, else null.</summary>
    string? FindRerunInput(RunRecord run, RunOptions options);
}

public sealed class LocalRunFiles : ILocalRunFiles
{
    private readonly IRunInputStore _inputs;
    private readonly Func<string> _defaultOutputParent;
    private readonly IReadOnlyList<string> _protectedPaths;

    /// <param name="protectedPaths">Folders that must never be deleted from or through (the app data folder holding the input copies).</param>
    public LocalRunFiles(IRunInputStore inputs, Func<string> defaultOutputParent, IReadOnlyList<string> protectedPaths)
    {
        _inputs = inputs;
        _defaultOutputParent = defaultOutputParent;
        _protectedPaths = protectedPaths;
    }

    public bool OutputFolderExists(RunRecord run)
        => !string.IsNullOrWhiteSpace(run.OutputDir) && Directory.Exists(run.OutputDir);

    public LocalDeleteResult DeleteOutputFolder(RunRecord run)
    {
        if (string.IsNullOrWhiteSpace(run.OutputDir))
        {
            return new(LocalDeleteStatus.NothingToDelete);
        }

        string folder;
        string root;
        try
        {
            root = RunOutputRoot.Resolve(run, _defaultOutputParent);
            folder = Path.GetFullPath(run.OutputDir);
            if (!RunOutputRoot.IsStrictlyInside(folder, root) || _protectedPaths.Any(p => RunOutputRoot.Overlaps(folder, p)))
            {
                return new(LocalDeleteStatus.Refused);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new(LocalDeleteStatus.Refused);
        }

        if (!Directory.Exists(folder))
        {
            return new(LocalDeleteStatus.NothingToDelete);
        }

        // Hard Rule 14: IsStrictlyInside is lexical, so a junction or symlink at the folder or above it (below the output folder)
        // would still carry the delete into its target. Refuse rather than remove the link: the user did not ask to unlink anything.
        if (HasLinkBetween(folder, root))
        {
            return new(LocalDeleteStatus.LinkRefused);
        }

        return DeleteFilesThenFolders(folder);
    }

    /// <summary>True when <paramref name="folder"/>, or any folder above it up to (not including) <paramref name="root"/>, is a reparse point.</summary>
    private static bool HasLinkBetween(string folder, string root)
    {
        var stop = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        try
        {
            for (var current = new DirectoryInfo(Path.TrimEndingDirectorySeparator(folder));
                 current is not null && !string.Equals(current.FullName, stop, StringComparison.OrdinalIgnoreCase);
                 current = current.Parent)
            {
                if (current.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    return true;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Cannot prove the path is link-free: the safe answer is not to delete through it.
            return true;
        }

        return false;
    }

    /// <summary>
    /// Deletes file by file so a file that cannot go (open elsewhere, read-only) does not hide how much did:
    /// the caller reports the counts. Folders are removed deepest first, only once empty.
    /// </summary>
    private static LocalDeleteResult DeleteFilesThenFolders(string folder)
    {
        var files = new List<string>();
        var links = new List<(string Path, bool IsDirectory)>();
        var directories = new List<string>();
        try
        {
            Walk(new DirectoryInfo(folder), files, directories, links);
        }
        catch (UnauthorizedAccessException)
        {
            return new(LocalDeleteStatus.AccessDenied);
        }
        catch (IOException)
        {
            return new(LocalDeleteStatus.InUse);
        }

        int deleted = 0, inUse = 0, denied = 0;
        foreach (var file in files)
        {
            try
            {
                File.Delete(file);
                deleted++;
            }
            catch (UnauthorizedAccessException)
            {
                denied++;
            }
            catch (IOException)
            {
                inUse++;
            }
        }

        // A junction or symlink is removed as the link it is: deleting through it would reach files outside the run's folder.
        foreach (var (path, isDirectory) in links)
        {
            try
            {
                if (isDirectory)
                {
                    Directory.Delete(path, recursive: false);
                }
                else
                {
                    File.Delete(path);
                }

                deleted++;
            }
            catch (UnauthorizedAccessException)
            {
                denied++;
            }
            catch (IOException)
            {
                inUse++;
            }
        }

        foreach (var directory in directories.OrderByDescending(d => d.Length))
        {
            TryDeleteEmptyFolder(directory);
        }

        var remaining = inUse + denied;
        if (remaining == 0)
        {
            // Every file went; the folder itself failing to go (something opened it meanwhile) is still "in use".
            return TryDeleteEmptyFolder(folder) || !Directory.Exists(folder)
                ? new(LocalDeleteStatus.Deleted, deleted)
                : new(LocalDeleteStatus.InUse);
        }

        return deleted > 0
            ? new(LocalDeleteStatus.Partial, deleted, remaining)
            : new(inUse > 0 ? LocalDeleteStatus.InUse : LocalDeleteStatus.AccessDenied, 0, remaining);
    }

    /// <summary>Lists what lies under <paramref name="root"/> without ever stepping into a reparse point (junction, symlink): those are collected as links.</summary>
    private static void Walk(DirectoryInfo root, List<string> files, List<string> directories, List<(string Path, bool IsDirectory)> links)
    {
        foreach (var entry in root.EnumerateFileSystemInfos())
        {
            if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                links.Add((entry.FullName, entry is DirectoryInfo));
            }
            else if (entry is DirectoryInfo directory)
            {
                directories.Add(directory.FullName);
                Walk(directory, files, directories, links);
            }
            else
            {
                files.Add(entry.FullName);
            }
        }
    }

    private static bool TryDeleteEmptyFolder(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public string? FindRerunInput(RunRecord run, RunOptions options)
    {
        var staged = _inputs.TryFindStagedInput(run.JobId);
        if (!string.IsNullOrWhiteSpace(staged) && File.Exists(staged))
        {
            return staged;
        }

        return !string.IsNullOrWhiteSpace(options.InputPath) && File.Exists(options.InputPath) ? options.InputPath : null;
    }
}
