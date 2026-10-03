namespace DnaEntropyGraph.Core.Viewers;

/// <summary>
/// Whether a viewer launch is safe to start (issue #586). A .bat or .cmd is run by CreateProcess through cmd.exe, which reads
/// <c>&amp; | ^ % &lt; &gt; ( ) !</c> and a quote in an argument as shell syntax, and <c>ProcessStartInfo.ArgumentList</c> does
/// not escape them: an output folder called <c>x&amp;calc</c> would run calc. Such a launch is refused. An .exe is started
/// directly and takes any argument.
/// </summary>
public static class ViewerLaunchSafety
{
    private const string Metacharacters = "&|^%<>()!\"";

    /// <summary>True when <paramref name="programPath"/> is a batch file and an argument holds a cmd.exe metacharacter.</summary>
    public static bool RefusesArguments(string programPath, IReadOnlyList<string> arguments)
        => IsBatchFile(programPath) && arguments.Any(a => a.AsSpan().IndexOfAny(Metacharacters) >= 0);

    private static bool IsBatchFile(string programPath)
        => programPath.EndsWith(".bat", StringComparison.OrdinalIgnoreCase) || programPath.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase);
}
