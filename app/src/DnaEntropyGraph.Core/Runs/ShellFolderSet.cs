using System.Runtime.InteropServices;

namespace DnaEntropyGraph.Core.Runs;

/// <summary>
/// The user's own folders an unmarked run folder may not be (issue #934): their profile and its direct children, and the shell folders themselves (Desktop, Documents, Pictures,
/// Music, Videos, Downloads). Paths are compared without case, without a trailing separator, without a <c>\\?\</c> prefix and with 8.3 short names expanded, so another spelling
/// of the same folder does not slip past.
/// </summary>
/// <param name="SelfAndChildren">The folder itself and anything directly inside it is protected (the profile).</param>
/// <param name="SelfOnly">Only the folder itself is protected. DECISION (agent-made, reversible; issue #934): the children of Documents, Desktop and the like are not, because older
/// runs live there when the user chose such a folder; they get the one confirmation.</param>
/// <param name="FailClosed">Windows could not name the profile: every folder is protected, so no unmarked folder is deleted.</param>
public sealed record ShellFolderSet(IReadOnlyList<string> SelfAndChildren, IReadOnlyList<string> SelfOnly, bool FailClosed = false)
{
    private static readonly Guid DownloadsId = new("374DE290-123F-4565-9164-39C4925E467B");

    /// <summary>The current user's folders as Windows reports them.</summary>
    public static ShellFolderSet FromEnvironment() => Build(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
        Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
        Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
        KnownDownloads());

    /// <summary>
    /// Builds the set from what Windows reported. A shell folder reported as blank falls back to its usual place in the profile, the usual Downloads is protected beside a redirected one,
    /// and a blank profile fails closed (<see cref="FailClosed"/>): not knowing where the user's folders are is never a reason to allow a delete.
    /// </summary>
    internal static ShellFolderSet Build(string? profile, string? desktop, string? documents, string? pictures, string? music, string? videos, string? downloads)
    {
        if (string.IsNullOrWhiteSpace(profile))
        {
            return new([], [], FailClosed: true);
        }

        string Or(string? reported, string usual) => string.IsNullOrWhiteSpace(reported) ? Path.Combine(profile, usual) : reported;
        return new(
            [profile],
            new[]
            {
                Or(desktop, "Desktop"),
                Or(documents, "Documents"),
                Or(pictures, "Pictures"),
                Or(music, "Music"),
                Or(videos, "Videos"),
                Or(downloads, "Downloads"),
                Path.Combine(profile, "Downloads"),
            }.Distinct(StringComparer.OrdinalIgnoreCase).ToList());
    }

    /// <summary>True when <paramref name="folder"/> is one of these folders, or (for <see cref="SelfAndChildren"/>) directly inside one. A path that cannot be read counts as protected.</summary>
    public bool Protects(string folder)
    {
        if (FailClosed)
        {
            return true;
        }

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

    /// <summary>Expands the short names of a path that exists, or answers null; null reads Windows (GetLongPathNameW). A test seam, because a volume may have short names turned off.</summary>
    internal Func<string, string?>? LongPathResolver { get; init; }

    private bool Same(string normalized, string other)
        => string.Equals(normalized, Norm(other), StringComparison.OrdinalIgnoreCase);

    private string Norm(string path)
        => Path.TrimEndingDirectorySeparator(ExpandShortNames(StripVerbatimPrefix(Path.GetFullPath(path))));

    private static string StripVerbatimPrefix(string full)
    {
        const string verbatimUnc = @"\\?\UNC\";
        const string verbatim = @"\\?\";
        if (full.StartsWith(verbatimUnc, StringComparison.OrdinalIgnoreCase))
        {
            return @"\\" + full[verbatimUnc.Length..];
        }

        return full.StartsWith(verbatim, StringComparison.Ordinal) ? full[verbatim.Length..] : full;
    }

    /// <summary>
    /// Expands 8.3 names (BOB~1) in the part of the path that exists, keeping the rest as written; any failure answers the path unchanged. The comparison stays safe either way:
    /// a spelling that cannot be expanded is only compared as written.
    /// </summary>
    private string ExpandShortNames(string full)
    {
        var tail = new Stack<string>();
        var current = full;
        while (true)
        {
            var expanded = (LongPathResolver ?? LongPath)(current);
            if (expanded is not null)
            {
                return tail.Aggregate(expanded, Path.Combine);
            }

            var parent = Path.GetDirectoryName(current);
            if (parent is null || string.Equals(parent, current, StringComparison.Ordinal))
            {
                return full;
            }

            tail.Push(Path.GetFileName(current));
            current = parent;
        }
    }

    private static string? LongPath(string path)
    {
        try
        {
            var buffer = new char[1024];
            var length = GetLongPathNameW(path, buffer, (uint)buffer.Length);
            return length == 0 || length >= buffer.Length ? null : new string(buffer, 0, (int)length);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    private static string? KnownDownloads()
    {
        try
        {
            var id = DownloadsId;
            var hr = SHGetKnownFolderPath(ref id, 0, IntPtr.Zero, out var path);
            try
            {
                return hr == 0 ? Marshal.PtrToStringUni(path) : null;
            }
            finally
            {
                Marshal.FreeCoTaskMem(path);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or COMException)
        {
            return null;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetLongPathNameW(string shortPath, [Out] char[] longPath, uint bufferLength);

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath(ref Guid rfid, uint dwFlags, IntPtr hToken, out IntPtr ppszPath);
}
