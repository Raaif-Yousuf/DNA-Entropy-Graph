namespace DnaEntropyGraph.Core.Runs;

/// <summary>
/// Which result files the Results page may hand to Windows with "Open" (issue #102). The worker only writes data files, but the
/// output folder is the user's and anything can be dropped into it; opening a program or script from a button labelled
/// Open would run it, so those are refused. Show in folder still works for them.
/// </summary>
public static class ShellOpenPolicy
{
    private static readonly HashSet<string> Refused = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".com", ".bat", ".cmd", ".ps1", ".psm1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh", ".hta",
        ".msi", ".msp", ".scr", ".pif", ".lnk", ".url", ".reg", ".cpl", ".jar",
    };

    public static bool MayOpen(string path) => !Refused.Contains(Path.GetExtension(path));
}
