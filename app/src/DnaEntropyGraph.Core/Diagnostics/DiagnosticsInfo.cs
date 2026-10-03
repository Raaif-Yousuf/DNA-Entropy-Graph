namespace DnaEntropyGraph.Core.Diagnostics;

/// <summary>What the bundle records about the machine, and what it must scrub from everything else.</summary>
/// <param name="AppVersion">The app's own version.</param>
/// <param name="OsDescription">Windows version text.</param>
/// <param name="DotNetVersion">The .NET runtime version.</param>
/// <param name="WebView2Version">The installed WebView2 runtime version, or null when it is not installed or not known.</param>
/// <param name="UserProfilePath">The user's profile folder (<c>C:\Users\name</c>); every path under it becomes <c>&lt;user&gt;</c>.</param>
/// <param name="SensitiveValues">Literal strings that must never appear (the user's email, their account name).</param>
/// <param name="CreatedUtc">When the bundle was made.</param>
public sealed record DiagnosticsInfo(
    string AppVersion,
    string OsDescription,
    string DotNetVersion,
    string? WebView2Version,
    string UserProfilePath,
    IReadOnlyList<string> SensitiveValues,
    DateTimeOffset CreatedUtc);

/// <summary>The bundle refused to be written because something that looks like sequence data survived redaction.</summary>
public sealed class DiagnosticsLeakException(string entryName)
    : Exception($"Sequence-like text was found in '{entryName}', so no diagnostics file was written.")
{
    /// <summary>The bundle entry that tripped the final scan.</summary>
    public string EntryName { get; } = entryName;
}
