namespace DnaEntropyGraph.Core.Diagnostics;

/// <summary>What the bundle records about the machine, and what it must scrub from everything else.</summary>
/// <param name="AppVersion">The app's own version.</param>
/// <param name="OsDescription">Windows version text.</param>
/// <param name="DotNetVersion">The .NET runtime version.</param>
/// <param name="WebView2Version">The installed WebView2 runtime version, or null when it is not installed or not known.</param>
/// <param name="UserProfilePath">The user's profile folder (<c>%USERPROFILE%</c>); every path under it becomes <c>&lt;user&gt;</c>.</param>
/// <param name="SensitiveValues">Literal strings that must never appear (the user's email, their account name).</param>
/// <param name="CreatedUtc">When the bundle was made.</param>
/// <param name="ReadmeText">The text of the zip's <c>README.txt</c>. User-visible, so the caller reads it from the resw file (Hard Rule 13); Core holds no copy.</param>
public sealed record DiagnosticsInfo(
    string AppVersion,
    string OsDescription,
    string DotNetVersion,
    string? WebView2Version,
    string UserProfilePath,
    IReadOnlyList<string> SensitiveValues,
    DateTimeOffset CreatedUtc,
    string ReadmeText);

/// <summary>The bundle refused to be written because sequence, a credential, an email, a path or one of the user's own identifiers survived redaction.</summary>
public sealed class DiagnosticsLeakException(string entryName)
    : Exception($"Identifying or sequence-like text was found in '{entryName}', so no diagnostics file was written.")
{
    /// <summary>The bundle entry that tripped the final scan.</summary>
    public string EntryName { get; } = entryName;
}
