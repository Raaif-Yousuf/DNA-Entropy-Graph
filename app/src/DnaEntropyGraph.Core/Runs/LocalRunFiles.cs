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

    /// <summary>
    /// The folder carries no marker (issue #934): it may be from before markers existed, or it may be the user's own folder named by a damaged row. Nothing was deleted;
    /// <see cref="ILocalRunFiles.DeleteUnmarkedOutputFolder"/> is the one way on, and only after the user has seen the folder and said yes.
    /// </summary>
    NeedsConfirmation,

    /// <summary>The folder's marker names another job or cannot be trusted (issue #934). Nothing was deleted and no confirmation unlocks it: the user deletes it themselves.</summary>
    OtherRunsFolder,

    /// <summary>The run was saved straight to a drive (<c>D:\</c>), so a delete through the History row is never offered (issue #954). Nothing was deleted.</summary>
    DriveRootRefused,

    /// <summary>
    /// The folder has no marker and is one of the user's own shell folders (their profile, Documents, Desktop, Downloads itself, Pictures, Music, Videos) or sits directly inside one (issue #934).
    /// Nothing was deleted and no confirmation unlocks it: a damaged row naming such a folder must never reach a recursive delete.
    /// </summary>
    ProtectedFolderRefused,
}

/// <summary>What the History must do before it deletes a run's folder (issue #934), decided without deleting anything. The default (0) is "go ahead": the delete itself still checks everything.</summary>
public enum LocalDeletePlan
{
    /// <summary>The folder carries this run's marker (or there is nothing to ask about): the ordinary confirmation, then delete.</summary>
    Proceed = 0,

    /// <summary>No marker: show the user the folder and ask once before <see cref="ILocalRunFiles.DeleteUnmarkedOutputFolder"/>.</summary>
    AskFirst,

    /// <summary>The marker names another job: refuse, and offer to open the folder.</summary>
    OtherRunsFolder,

    /// <summary>The run was saved straight to a drive: refuse, and offer to open the folder.</summary>
    DriveRoot,

    /// <summary>No marker, and the folder is the user's own shell folder or directly inside one: refuse, and offer to open the folder.</summary>
    ProtectedFolder,
}

/// <summary>The user's own folders an unmarked run folder may not be, or sit directly inside (issue #934). Paths are compared without case and without a trailing separator.</summary>
/// <param name="SelfAndChildren">The folder itself and anything directly inside it is protected (the profile, Documents, Desktop, Pictures, Music, Videos).</param>
/// <param name="SelfOnly">Only the folder itself is protected: Downloads holds ordinary run folders directly inside it.</param>
public sealed record ShellFolderSet(IReadOnlyList<string> SelfAndChildren, IReadOnlyList<string> SelfOnly)
{
    /// <summary>The current user's folders as Windows reports them.</summary>
    public static ShellFolderSet FromEnvironment()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string[] both =
        [
            profile,
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
            Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
        ];
        return new(
            both.Where(p => !string.IsNullOrWhiteSpace(p)).ToList(),
            string.IsNullOrWhiteSpace(profile) ? [] : [Path.Combine(profile, "Downloads")]);
    }

    /// <summary>True when <paramref name="folder"/> is one of these folders, or (for <see cref="SelfAndChildren"/>) directly inside one. Lexical; a path that cannot be read counts as protected.</summary>
    public bool Protects(string folder)
    {
        try
        {
            var full = Norm(folder);
            var parent = Path.GetDirectoryName(full);
            return SelfOnly.Any(p => Same(full, p))
                   || SelfAndChildren.Any(p => Same(full, p) || (parent is not null && Same(parent, p)));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return true;
        }
    }

    private static string Norm(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static bool Same(string normalized, string other)
        => string.Equals(normalized, Norm(other), StringComparison.OrdinalIgnoreCase);
}

/// <summary>What a local delete did. The counts are files, not folders, and are set for <see cref="LocalDeleteStatus.Deleted"/> and <see cref="LocalDeleteStatus.Partial"/>.</summary>
public sealed record LocalDeleteResult(LocalDeleteStatus Status, int FilesDeleted = 0, int FilesRemaining = 0);

/// <summary>The run's files on this PC (issue #101). Hard Rule 14: nothing here ever touches the input copy or anything outside the run's own output folder.</summary>
public interface ILocalRunFiles
{
    bool OutputFolderExists(RunRecord run);

    /// <summary>
    /// Deletes the run's own output folder, only if it lies strictly inside the folder the run was told to write under AND carries this run's marker (issue #934).
    /// A folder with no marker is not touched (<see cref="LocalDeleteStatus.NeedsConfirmation"/>); one whose marker names another job is not touched either
    /// (<see cref="LocalDeleteStatus.OtherRunsFolder"/>). Never the input copy.
    /// </summary>
    LocalDeleteResult DeleteOutputFolder(RunRecord run);

    /// <summary>
    /// The same delete for a folder with no marker, called only after the user was shown the folder and confirmed (issue #934). Every other check still applies, and a folder
    /// whose marker names another job, or a drive root, is still refused: a confirmation unlocks the missing marker and nothing else.
    /// </summary>
    LocalDeleteResult DeleteUnmarkedOutputFolder(RunRecord run);

    /// <summary>What to do before a delete (issue #934, #954): nothing deleted, the disk only read.</summary>
    LocalDeletePlan PlanDelete(RunRecord run);

    /// <summary>True when the run was saved straight to a drive (issue #954). Lexical, no disk access, so the History can ask it for every row.</summary>
    bool OutputRootIsDriveRoot(RunRecord run);

    /// <summary>The input a re-run should use: the app's own copy, else the original file, else null.</summary>
    string? FindRerunInput(RunRecord run, RunOptions options);

    /// <summary>The GFF3 a re-run of a paired run (issue #686) should use: the app's own copy, else the original, else null. Null too when the run had no GFF3.</summary>
    string? FindRerunAnnotation(RunRecord run, RunOptions options);
}

public sealed class LocalRunFiles : ILocalRunFiles
{
    private readonly IRunInputStore _inputs;
    private readonly Func<string> _defaultOutputParent;
    private readonly IReadOnlyList<string> _protectedPaths;
    private readonly Func<ShellFolderSet> _shellFolders;
    private readonly Action<string>? _beforeFirstDelete;

    /// <param name="protectedPaths">Folders that must never be deleted from or through (the app data folder holding the input copies).</param>
    /// <param name="shellFolders">The user's own folders an unmarked folder may not be or sit directly inside; null reads them from Windows. A test seam.</param>
    /// <param name="beforeFirstDelete">Called with the folder after it was listed and before the first file is deleted, so a test can change the disk in the gap the re-check closes. A test seam.</param>
    public LocalRunFiles(
        IRunInputStore inputs,
        Func<string> defaultOutputParent,
        IReadOnlyList<string> protectedPaths,
        Func<ShellFolderSet>? shellFolders = null,
        Action<string>? beforeFirstDelete = null)
    {
        _inputs = inputs;
        _defaultOutputParent = defaultOutputParent;
        _protectedPaths = protectedPaths;
        _shellFolders = shellFolders ?? ShellFolderSet.FromEnvironment;
        _beforeFirstDelete = beforeFirstDelete;
    }

    public bool OutputFolderExists(RunRecord run)
        => !string.IsNullOrWhiteSpace(run.OutputDir) && Directory.Exists(run.OutputDir);

    public LocalDeleteResult DeleteOutputFolder(RunRecord run) => DeleteCore(run, allowUnmarked: false);

    // DECISION (agent-made, reversible; issue #934): a folder with no marker (made before markers existed, or the user's own folder named by a damaged row) is deleted only
    // after the user has been shown its full path and confirmed. The alternative was to refuse every older run for good; the owner question in NEXT_SESSION.md recommended the dialog.
    public LocalDeleteResult DeleteUnmarkedOutputFolder(RunRecord run) => DeleteCore(run, allowUnmarked: true);

    public bool OutputRootIsDriveRoot(RunRecord run) => RunOutputRoot.IsDriveRootOutput(run, _defaultOutputParent);

    public LocalDeletePlan PlanDelete(RunRecord run)
    {
        var inspection = Inspect(run);
        if (inspection.Stop == LocalDeleteStatus.DriveRootRefused)
        {
            return LocalDeletePlan.DriveRoot;
        }

        if (inspection.Stop is not null)
        {
            // Not a marker question: the delete itself reports it (nothing there, a link, outside the output folder).
            return LocalDeletePlan.Proceed;
        }

        return RunFolderMarker.Check(inspection.Folder!, run.JobId) switch
        {
            RunFolderMarkerState.Matches => LocalDeletePlan.Proceed,
            RunFolderMarkerState.Absent => _shellFolders().Protects(inspection.Folder!) ? LocalDeletePlan.ProtectedFolder : LocalDeletePlan.AskFirst,
            _ => LocalDeletePlan.OtherRunsFolder,
        };
    }

    private readonly record struct Inspection(LocalDeleteStatus? Stop, string? Folder, string? Root = null);

    /// <summary>Every check that does not need the marker, in the order a delete makes them. <see cref="Inspection.Stop"/> is the answer when one stops the delete.</summary>
    private Inspection Inspect(RunRecord run)
    {
        // Hard Rule 14: an imported prototype run's folder is the user's own (the app never wrote it). It has no options, so the output
        // root falls back to Downloads and the folder would pass the "strictly inside" test below: refuse by origin, not by path.
        if (run.Imported)
        {
            return new(LocalDeleteStatus.Refused, null);
        }

        if (string.IsNullOrWhiteSpace(run.OutputDir))
        {
            return new(LocalDeleteStatus.NothingToDelete, null);
        }

        string folder;
        string root;
        try
        {
            root = RunOutputRoot.Resolve(run, _defaultOutputParent);
            folder = Path.GetFullPath(run.OutputDir);
            if (!RunOutputRoot.IsStrictlyInside(folder, root) || _protectedPaths.Any(p => RunOutputRoot.Overlaps(folder, p)))
            {
                return new(LocalDeleteStatus.Refused, null);
            }

            // Issue #954: a whole drive as the output folder is refused (a row naming C:\Windows\Temp\x must not count as inside C:\), but for its own reason.
            if (RunOutputRoot.IsDriveRoot(root))
            {
                return new(LocalDeleteStatus.DriveRootRefused, null);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new(LocalDeleteStatus.Refused, null);
        }

        if (!Directory.Exists(folder))
        {
            return new(LocalDeleteStatus.NothingToDelete, null);
        }

        // Hard Rule 14: IsStrictlyInside is lexical, so a junction or symlink at the folder or above it (below the output folder)
        // would still carry the delete into its target. Refuse rather than remove the link: the user did not ask to unlink anything.
        return HasLinkBetween(folder, root) ? new(LocalDeleteStatus.LinkRefused, null) : new(null, folder, root);
    }

    private LocalDeleteResult DeleteCore(RunRecord run, bool allowUnmarked)
    {
        var inspection = Inspect(run);
        if (inspection.Stop is { } stop)
        {
            return new(stop);
        }

        var folder = inspection.Folder!;

        // Issue #934: containment above is read from the same history row as the folder, so a damaged row can name both. The marker is the proof the app made this folder for this run.
        var planned = RunFolderMarker.Check(folder, run.JobId);
        switch (planned)
        {
            case RunFolderMarkerState.OtherRun:
                return new(LocalDeleteStatus.OtherRunsFolder);
            case RunFolderMarkerState.Absent when !allowUnmarked:
                return new(LocalDeleteStatus.NeedsConfirmation);
            case RunFolderMarkerState.Absent when _shellFolders().Protects(folder):
                // A confirmation unlocks a missing marker, never the user's own folders.
                return new(LocalDeleteStatus.ProtectedFolderRefused);
        }

        return DeleteFilesThenFolders(folder, inspection.Root!, run.JobId, planned);
    }

    /// <summary>The disk now, against what the delete was planned on: a link swapped in, or a marker added, removed or replaced since the checks above. Null when nothing changed.</summary>
    private static LocalDeleteResult? ChangedSincePlanned(string folder, string root, string jobId, RunFolderMarkerState planned)
    {
        if (HasLinkBetween(folder, root))
        {
            return new(LocalDeleteStatus.LinkRefused);
        }

        var now = RunFolderMarker.Check(folder, jobId);
        if (now == planned)
        {
            return null;
        }

        return new(now == RunFolderMarkerState.OtherRun ? LocalDeleteStatus.OtherRunsFolder : LocalDeleteStatus.NeedsConfirmation);
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
    private LocalDeleteResult DeleteFilesThenFolders(string folder, string root, string jobId, RunFolderMarkerState planned)
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

        // The marker goes last, and only once everything else has: a delete that stopped partway keeps it, so pressing the button again does not ask the user a second time.
        var marker = files.FirstOrDefault(f => RunFolderMarker.IsMarkerOf(folder, f));
        if (marker is not null)
        {
            files.Remove(marker);
        }

        // Issue #934: the checks above ran before the walk; re-run the two that decide whether this is ours, immediately before the first delete.
        _beforeFirstDelete?.Invoke(folder);
        if (ChangedSincePlanned(folder, root, jobId, planned) is { } changed)
        {
            return changed;
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
            if (marker is not null)
            {
                try
                {
                    File.Delete(marker);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // A marker that will not go keeps the folder from going: the folder is "in use" below, and the next press finds the marker still in place.
                }
            }

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

    public string? FindRerunAnnotation(RunRecord run, RunOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.AnnotationPath))
        {
            return null;
        }

        // The store answers from the disk without waiting on anything; callers run this off the UI thread.
        var staged = _inputs.FindAnnotationAsync(run.JobId, CancellationToken.None).GetAwaiter().GetResult()?.LocalPath;
        if (!string.IsNullOrWhiteSpace(staged) && File.Exists(staged))
        {
            return staged;
        }

        return File.Exists(options.AnnotationPath) ? options.AnnotationPath : null;
    }
}
