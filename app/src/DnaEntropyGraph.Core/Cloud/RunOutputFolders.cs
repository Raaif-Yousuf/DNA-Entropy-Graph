namespace DnaEntropyGraph.Core.Cloud;

/// <summary>
/// Where a run's results go (Hard Rule 14): a fresh folder inside the folder the
/// user chose (default Downloads), named after the input, never over an earlier run's.
/// </summary>
public static class RunOutputFolders
{
    /// <summary>
    /// The default output folder when the user chose none: the Downloads known folder as reported by
    /// <paramref name="downloadsKnownFolder"/> (a redirected or OneDrive Downloads included), or
    /// <c>Downloads</c> under the user profile only when that lookup returns nothing or throws.
    /// The Windows lookup lives in the App project (<c>KnownFolders</c>), so Core stays testable.
    /// </summary>
    public static string DefaultParent(Func<string?> downloadsKnownFolder)
    {
        ArgumentNullException.ThrowIfNull(downloadsKnownFolder);
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
    /// Creates and returns <c>parent\name</c>, or <c>parent\name_2</c>, <c>_3</c> and so on when
    /// that exists. <paramref name="name"/> is made safe as a single folder name first.
    /// </summary>
    public static string CreateUnique(string parent, string name, Action<string>? removeStaging = null)
    {
        var safe = Sanitize(name);
        Directory.CreateDirectory(parent);
        for (var n = 1; ; n++)
        {
            var candidate = Path.Combine(parent, n == 1 ? safe : $"{safe}_{n}");
            if (TryClaim(parent, candidate, removeStaging ?? Directory.Delete))
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
