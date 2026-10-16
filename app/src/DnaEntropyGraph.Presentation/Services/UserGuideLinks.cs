namespace DnaEntropyGraph.Presentation.Services;

/// <summary>
/// Where the Help button goes (issue #143): the user guide page for the page the user is on. DECISION (agent-made, reversible):
/// the guide is read on GitHub (the markdown renders there), not bundled in the installer (a copy goes stale and adds files) and not on
/// GitHub Pages (the repository publishes no Pages site today). A release build links the guide as it was at its own tag
/// (<c>blob/v1.2.3/...</c>) so a rename on main never breaks an installed copy; a development build links main.
/// A guard (Guards.Tests/UserGuideLinksTests) proves every file named here exists under docs/user_guide.
/// </summary>
public static class UserGuideLinks
{
    /// <summary>The guide folder on the main branch (what a development build links).</summary>
    public const string BaseUrl = "https://github.com/Raaif-Yousuf/DNA-Entropy-Graph/blob/main/docs/user_guide/";

    private const string BlobRoot = "https://github.com/Raaif-Yousuf/DNA-Entropy-Graph/blob/";

    private const string GuideFolder = "/docs/user_guide/";

    public const string IndexFile = "README.md";

    /// <summary>Every guide file a page key can map to, for the guard.</summary>
    public static IReadOnlyList<string> AllFiles { get; } =
    [
        IndexFile, "02-connect-google-cloud.md", "03-run.md", "04-view.md", "05-history.md", "06-costs-and-cleanup.md", "08-settings.md",
    ];

    /// <summary>The guide file for a navigation page key (the keys <c>INavigator</c> uses); the guide index for any other key.</summary>
    public static string FileFor(string pageKey) => pageKey switch
    {
        "NewRun" or "RunProgress" => "03-run.md",
        "Runs" => "05-history.md",
        "Results" or "Viewer" => "04-view.md",
        "Cloud" => "06-costs-and-cleanup.md",
        "Settings" => "08-settings.md",
        "Wizard" => "02-connect-google-cloud.md",
        _ => IndexFile,
    };

    /// <summary>The guide folder for this build: its own release tag when it is a signed release build, else main.</summary>
    public static string BaseUrlFor(BuildIdentity? build)
        => build is { IsDevBuild: false } release && release.Info.Version is { Length: > 0 } version && version != "0.0.0"
            ? BlobRoot + "v" + version.TrimStart('v') + GuideFolder
            : BaseUrl;

    public static Uri UriFor(string pageKey, BuildIdentity? build = null) => new(BaseUrlFor(build) + FileFor(pageKey));
}
