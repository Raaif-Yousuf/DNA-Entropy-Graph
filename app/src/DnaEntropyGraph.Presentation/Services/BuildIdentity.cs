namespace DnaEntropyGraph.Presentation.Services;

/// <summary>
/// The running build's version and short git sha (issue #263), parsed from the assembly's informational version
/// (<c>1.2.3+0123456789abcdef</c>: the SDK appends the source revision after a plus sign). A build with no revision has an empty sha.
/// </summary>
public sealed record BuildInfo(string Version, string ShortSha)
{
    private const int ShaLength = 7;

    public static BuildInfo Parse(string? informationalVersion)
    {
        if (string.IsNullOrWhiteSpace(informationalVersion))
        {
            return new BuildInfo("0.0.0", string.Empty);
        }

        var plus = informationalVersion.IndexOf('+', StringComparison.Ordinal);
        if (plus < 0)
        {
            return new BuildInfo(informationalVersion.Trim(), string.Empty);
        }

        var sha = informationalVersion[(plus + 1)..].Trim();
        return new BuildInfo(informationalVersion[..plus].Trim(), sha.Length > ShaLength ? sha[..ShaLength] : sha);
    }

    /// <summary>"1.2.3 (0123456)", or just the version when the build has no revision.</summary>
    public string Display => ShortSha.Length == 0 ? Version : $"{Version} ({ShortSha})";
}

/// <summary>
/// Which build this is (issue #263): its <see cref="BuildInfo"/> and whether the running exe carries an Authenticode signature. An unsigned
/// exe is a development build or a CI artifact, never a release (release.yml signs the exe and verifies it), so it wears a banner.
/// </summary>
public sealed class BuildIdentity(BuildInfo info, bool isSigned)
{
    public BuildInfo Info { get; } = info;

    public bool IsSigned { get; } = isSigned;

    public bool IsDevBuild => !IsSigned;
}
