namespace DnaEntropyGraph.Core;

/// <summary>
/// The one place that builds Explorer's "reveal this file" switch (issue #677). Explorer reads <c>/select,"path"</c> with its own rules, not the C runtime's, so
/// <c>ProcessStartInfo.ArgumentList</c> (which OpenFolder uses for a plain folder argument) is not a safe substitute: THEORY (unverified): it would send
/// <c>"/select,C:\a b\f.txt"</c>, a form Explorer is not documented to accept. The proven form is kept, with the paths it cannot carry refused up front.
/// </summary>
public static class ExplorerCommandLine
{
    /// <summary>
    /// <c>/select,"&lt;path&gt;"</c> for a fully qualified file path (a drive and a folder, or a share), or null when the path cannot be quoted safely: empty, relative (Explorer would resolve it against its own
    /// working directory), a device path (<c>\\.\pipe\x</c>, <c>\\?\C:\x</c>), one with a forward slash (the switch is only proven with backslashes; THEORY (unverified): Explorer would read <c>//server/share</c> the same), only rooted (<c>C:a.bed</c> is relative to that drive's current folder and <c>\Lab\a.bed</c> to whichever drive Explorer is on), containing a quote character (not legal in a Windows path), or ending in a backslash (a folder, and a quote after a trailing backslash is read as an escaped quote).
    /// </summary>
    public static string? Select(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || filePath.Contains('"') || filePath.EndsWith('\\') || filePath.Contains('/') || IsDevicePath(filePath) || !Path.IsPathFullyQualified(filePath))
        {
            return null;
        }

        return "/select,\"" + filePath + "\"";
    }

    /// <summary><c>\\.\</c> and <c>\\?\</c> (pipes, physical drives, the long-path prefix): fully qualified to .NET, but not a file Explorer can reveal. A plain share (<c>\\server\share\f</c>) is not one.</summary>
    private static bool IsDevicePath(string path) => path.StartsWith(@"\\.\", StringComparison.Ordinal) || path.StartsWith(@"\\?\", StringComparison.Ordinal);
}
