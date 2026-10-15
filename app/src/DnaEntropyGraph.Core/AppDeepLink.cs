namespace DnaEntropyGraph.Core;

/// <summary>
/// The argument text that carries "open this run" from a Windows notification, or from a second launch of the exe, into the running
/// app (issue #107). It comes from outside the app, so a job id is accepted only in the plain shape the app itself makes
/// (letters, digits, '-' and '_'), never a path or anything that could reach a page as more than an id.
/// </summary>
public static class AppDeepLink
{
    public const string RunKey = "run";

    private const int MaxJobIdLength = 64;

    /// <summary>The argument text a notification carries for <paramref name="jobId"/>.</summary>
    public static string ForRun(string jobId)
        => IsPlainId(jobId) ? RunKey + "=" + jobId : throw new ArgumentException("A job id is letters, digits, '-' and '_' only.", nameof(jobId));

    /// <summary>Finds the run in <paramref name="arguments"/> ("run=ID", alone or among others separated by ';' or '&amp;'); false when there is none or it is not a plain id.</summary>
    public static bool TryGetRunId(string? arguments, out string jobId)
    {
        jobId = string.Empty;
        if (string.IsNullOrWhiteSpace(arguments))
        {
            return false;
        }

        foreach (var part in SplitOutsideQuotes(arguments))
        {
            var separator = part.IndexOf('=', StringComparison.Ordinal);
            if (separator > 0 && part[..separator] == RunKey && IsPlainId(part[(separator + 1)..]))
            {
                jobId = part[(separator + 1)..];
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The sequence files in a launch's arguments (a double-clicked or "Open with" file arrives as the command line, quoted when it has
    /// spaces). Only a local full path to a sequence type is kept: never a relative path, a UNC share (opening one would reach out to
    /// another machine), a wildcard, or another file type.
    /// </summary>
    public static bool TryGetInputFiles(string? arguments, out IReadOnlyList<string> paths)
    {
        var found = new List<string>();
        paths = found;
        if (string.IsNullOrWhiteSpace(arguments))
        {
            return false;
        }

        var token = new System.Text.StringBuilder();
        var quoted = false;
        void Flush()
        {
            if (token.Length > 0 && found.Count < MaxFiles && IsLocalSequenceFile(token.ToString()))
            {
                found.Add(token.ToString());
            }

            token.Clear();
        }

        foreach (var c in arguments)
        {
            if (c == '"')
            {
                quoted = !quoted;
            }
            else if (char.IsWhiteSpace(c) && !quoted)
            {
                Flush();
            }
            else
            {
                token.Append(c);
            }
        }

        Flush();
        return found.Count > 0;
    }

    private const int MaxFiles = 20;

    /// <summary>
    /// Splits on ';' and '&amp;' except inside double quotes, so a quoted path that contains one (a folder named "a;run=x") stays one
    /// piece and cannot pose as a second argument.
    /// </summary>
    private static List<string> SplitOutsideQuotes(string arguments)
    {
        var parts = new List<string>();
        var current = new System.Text.StringBuilder();
        var quoted = false;
        foreach (var c in arguments)
        {
            if (c == '"')
            {
                quoted = !quoted;
            }

            if (!quoted && c is ';' or '&')
            {
                parts.Add(current.ToString().Trim());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        parts.Add(current.ToString().Trim());
        parts.RemoveAll(p => p.Length == 0);
        return parts;
    }

    /// <summary>
    /// A full path to a sequence file on this PC. Refused: a relative path, a wildcard, another file type, and any path whose root is
    /// a network share or a device path (<c>\\host\share</c>, <c>//host/share</c>, <c>\\?\UNC\...</c>, <c>\\.\</c>; both slash forms),
    /// because opening one would make this PC reach out to another machine on the say-so of whatever started the app.
    /// DECISION (agent-made, reversible): a drive letter that is mapped to a network share (<c>Z:\</c>) is ALLOWED. The mapping is the
    /// user's own, Explorer passes it when they choose Open with on a file there, and labs keep sequences on such drives; a mapped
    /// drive cannot send credentials to a host the user did not already choose, which is the risk a UNC path carries. Rejecting
    /// <c>DriveInfo.DriveType == Network</c> would silently break Open with for those labs.
    /// </summary>
    private static bool IsLocalSequenceFile(string text)
        => text.IndexOfAny(['*', '?', '<', '>', '|']) < 0
            && !HasNetworkOrDeviceRoot(text)
            && Path.IsPathFullyQualified(text)
            && FileAssociations.IsSequenceFile(text);

    private static bool HasNetworkOrDeviceRoot(string text)
    {
        // Path.GetPathRoot is asked too, so a form this check did not think of but .NET reads as a UNC root is still refused.
        var root = Path.GetPathRoot(text) ?? string.Empty;
        return IsDirectorySeparator(text, 0) && IsDirectorySeparator(text, 1)
            || IsDirectorySeparator(root, 0) && IsDirectorySeparator(root, 1);
    }

    private static bool IsDirectorySeparator(string text, int index) => index < text.Length && text[index] is '\\' or '/';

    private static bool IsPlainId(string? value)
        => !string.IsNullOrEmpty(value) && value.Length <= MaxJobIdLength && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
}
