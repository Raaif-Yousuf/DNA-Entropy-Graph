using DnaEntropyGraph.Core.Contract;
using DnaEntropyGraph.Core.Runs;

namespace DnaEntropyGraph.Core.Cloud;

/// <summary>
/// Where a run's results go (Hard Rule 14): a fresh folder inside the folder the
/// user chose (default Downloads), named after the input, never over an earlier run's.
/// </summary>
public static class RunOutputFolders
{
    private const string DefaultTemplate = "{file}";

    /// <summary>
    /// The default output folder when the user chose none: the Downloads known folder as reported by
    /// <paramref name="downloadsKnownFolder"/> (a redirected or OneDrive Downloads included), or
    /// <c>Downloads</c> under the user profile only when that lookup returns nothing or throws.
    /// The Windows lookup lives in the App project (<c>KnownFolders</c>), so Core stays testable.
    /// </summary>
    public static string DefaultParent(Func<string?> downloadsKnownFolder, string? portableOutputs = null)
    {
        ArgumentNullException.ThrowIfNull(downloadsKnownFolder);
        if (!string.IsNullOrWhiteSpace(portableOutputs))
        {
            // Portable mode (issue #144): results stay on the stick, never in the host PC's Downloads.
            return portableOutputs;
        }

        string? found = null;
        try
        {
            found = downloadsKnownFolder();
        }
        catch (Exception)
        {
            // Fall through to the profile path: a failed shell call must not stop a run.
        }

        return string.IsNullOrWhiteSpace(found)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads")
            : found;
    }

    /// <summary>
    /// The folder name a run gets (issue #514): <see cref="RunOptions.NameTemplate"/> expanded by <see cref="RunNamer"/> for this input, model and
    /// time. The cloud runner, the This PC runner and the New run page's preview all call this one function, so what the page shows is what
    /// the runner creates. A name already taken is not handled here: <see cref="CreateUnique"/> adds <c>_2</c> against the real disk.
    /// </summary>
    public static string RunFolderName(RunOptions options, StagedInput input, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(input);

        // An accession has no file name, and its output stem holds no dot, so the stem is what {file} must see; a file goes in by its
        // name so RunNamer drops the extension exactly as StagedInput.OutputStem does.
        var file = input.IsAccession ? input.OutputStem : input.FileName;
        // A blank template is the default, not "run": a user who clears the box gets the file's name, as before the box existed.
        var template = string.IsNullOrWhiteSpace(options.NameTemplate) ? DefaultTemplate : options.NameTemplate;
        return RunNamer.Resolve(template, file, options.ModelId, now, 1, []);
    }

    /// <summary>
    /// Creates and returns <c>parent\name</c>, or <c>parent\name_2</c>, <c>_3</c> and so on when
    /// that exists. <paramref name="name"/> is made safe as a single folder name first.
    /// </summary>
    /// <param name="jobId">The run this folder is for (issue #934): when given, the folder gets its <see cref="RunFolderMarker"/> before it is returned, which a later recursive
    /// delete requires. A marker that cannot be written fails like any other write into the folder (the empty folder is removed again).</param>
    public static string CreateUnique(string parent, string name, Action<string>? removeStaging = null, string? jobId = null)
    {
        var safe = Sanitize(name);
        Directory.CreateDirectory(parent);
        for (var n = 1; ; n++)
        {
            var candidate = Path.Combine(parent, n == 1 ? safe : $"{safe}_{n}");
            if (TryClaim(parent, candidate, removeStaging ?? Directory.Delete))
            {
                if (!string.IsNullOrWhiteSpace(jobId))
                {
                    WriteMarkerOrRemove(candidate, jobId);
                }

                return candidate;
            }
        }
    }

    private static void WriteMarkerOrRemove(string folder, string jobId)
    {
        try
        {
            RunFolderMarker.Write(folder, jobId);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The folder is ours and still empty: leave no trace of a run that could not start (best effort), then fail like any unwritable folder.
            RunFolderMarker.TryRemoveEmptyFolder(folder);
            throw;
        }
    }

    /// <summary>
    /// The one folder for the exports (pictures) of a run whose own folder belongs to the user: <c>parent\name exports</c>, the same path every time
    /// for the same run. Only names it; the caller creates it when it has something to write.
    /// </summary>
    public static string ExportsFolder(string parent, string name, string jobId)
    {
        // A short code from the job id keeps two runs of the same name apart, and keeps the app out of a plain "<name> exports" folder the
        // user may have made themselves.
        var code = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(jobId)))[..6].ToLowerInvariant();
        return Path.Combine(parent, Sanitize($"{name} exports {code}"));
    }

    /// <summary>
    /// Creates <paramref name="folder"/> and any missing parents and returns exactly the folders it made, outermost first (empty when it was
    /// already there), so a caller that then has nothing to write can <see cref="RemoveCreated"/> them and leave no trace.
    /// </summary>
    /// <param name="folder">The folder to make.</param>
    /// <param name="requiredRoot">When given, a folder that must already exist (a Downloads the user deleted is not brought back):
    /// <see cref="DirectoryNotFoundException"/> when it is not there, and nothing is made.</param>
    public static IReadOnlyList<string> CreateFolderChain(string folder, string? requiredRoot = null) => CreateFolderChain(folder, requiredRoot, File.GetAttributes);

    /// <summary>The same with the attribute read supplied, so a test can refuse it for a folder that is really there.</summary>
    internal static IReadOnlyList<string> CreateFolderChain(string folder, string? requiredRoot, Func<string, FileAttributes> getAttributes)
        => CreateFolderChain(folder, requiredRoot, p => FolderExists(p, getAttributes), d => Directory.CreateDirectory(d));

    /// <summary>
    /// Whether a folder is at <paramref name="path"/>. Unlike <c>Directory.Exists</c> (false on an access error, which would put a folder that was
    /// already there in the "made" list and let <see cref="RemoveCreated"/> take it away), only "not found" answers false; a folder that cannot be
    /// probed counts as there.
    /// </summary>
    internal static bool FolderExists(string path, Func<string, FileAttributes> getAttributes)
    {
        try
        {
            // A file at the path is not a folder (CreateDirectory would refuse it); anything else that answers is.
            return getAttributes(path).HasFlag(FileAttributes.Directory);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return true;
        }
    }

    /// <summary>The same with the probe and the one-folder maker supplied, so a test can fail the maker part-way.</summary>
    internal static IReadOnlyList<string> CreateFolderChain(string folder, string? requiredRoot, Func<string, bool> exists, Action<string> create)
    {
        // A trailing separator would name the same folder twice (with and without it).
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        if (requiredRoot is not null && !exists(Path.TrimEndingDirectorySeparator(Path.GetFullPath(requiredRoot))))
        {
            throw new DirectoryNotFoundException("The folder to put this in is not there.");
        }

        var missing = new List<string>();
        for (var dir = full; !string.IsNullOrEmpty(dir) && !exists(dir); dir = Path.GetDirectoryName(dir) ?? string.Empty)
        {
            missing.Add(dir);
        }

        missing.Reverse();
        var made = new List<string>();
        try
        {
            foreach (var dir in missing)
            {
                create(dir);
                made.Add(dir);
            }
        }
        catch
        {
            // Part-way failure: what this call made is not left behind (RemoveCreated only takes empty folders).
            RemoveCreated(made);
            throw;
        }

        return made;
    }

    /// <summary>
    /// Removes folders from <see cref="CreateFolderChain"/>, innermost first, only while each is empty; never recursive. Best effort: a folder that
    /// cannot be removed (not empty, already gone, refused) is skipped and the rest are still tried, and nothing is thrown, so this is safe in a
    /// failure path.
    /// DECISION (agent-made, reversible): only an EMPTY folder is ever deleted, so a folder another process made between the probe and the create
    /// (and listed as made here) is removed only while still empty. That worst case (an empty folder somebody just made goes away, and they can
    /// make it again) is accepted over a lock or a second probe that could not close the race anyway; it never loses a file.
    /// </summary>
    public static void RemoveCreated(IReadOnlyList<string> created)
    {
        for (var i = created.Count - 1; i >= 0; i--)
        {
            try
            {
                Directory.Delete(created[i]);
            }
            catch (Exception)
            {
                // Skipped: see the summary.
            }
        }
    }

    /// <summary>
    /// A path in <paramref name="folder"/> named <c>baseName.ext</c>, or <c>baseName (2).ext</c>, <c>(3)</c> and so on when anything (a file or a
    /// folder) already has that name: a file the user may care about is never silently overwritten (Hard Rule 14).
    /// </summary>
    public static string UniqueFilePath(string folder, string baseName, string extension)
    {
        for (var n = 1; ; n++)
        {
            var candidate = Path.Combine(folder, n == 1 ? baseName + extension : $"{baseName} ({n}){extension}");
            if (!Path.Exists(candidate))
            {
                return candidate;
            }
        }
    }

    /// <summary>
    /// Takes <paramref name="candidate"/> only if nothing exists there. <c>Directory.CreateDirectory</c>
    /// (the staging folder lives in <paramref name="parent"/> because an atomic move needs the same volume)
    /// succeeds on an existing folder, so it cannot tell "mine" from "someone else's". Instead a fresh
    /// private folder (staged in <paramref name="parent"/>, since an atomic move needs the same volume) is moved onto the name: the move fails if the name appeared in the meantime (an
    /// atomic rename on Windows, so it also holds against another process), and the next name is tried.
    /// </summary>
    private static bool TryClaim(string parent, string candidate, Action<string> removeStaging)
    {
        // Any entry (a file with no extension named like the input counts) makes the name taken.
        if (Path.Exists(candidate))
        {
            return false;
        }

        var staging = Path.Combine(parent, ".deg-claim-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            Directory.Move(staging, candidate);
            return true;
        }
        catch (IOException) when (Path.Exists(candidate))
        {
            TryRemoveStaging(staging, removeStaging);
            return false;
        }
        catch
        {
            TryRemoveStaging(staging, removeStaging);
            throw;
        }
    }

    private static void TryRemoveStaging(string staging, Action<string> removeStaging)
    {
        try
        {
            removeStaging(staging);
        }
        catch (Exception)
        {
            // Best effort: an empty private staging folder we created ourselves; leaving it is harmless.
        }
    }

    private static string Sanitize(string? name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string((name ?? string.Empty).Select(c => Array.IndexOf(invalid, c) >= 0 ? '_' : c).ToArray()).Trim().TrimEnd('.');
        return cleaned.Length == 0 ? "run" : cleaned;
    }
}
