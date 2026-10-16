namespace DnaEntropyGraph.Presentation.Services;

/// <summary>
/// Where the Help button goes (issue #143): the user guide page for the page the user is on. DECISION (agent-made, reversible):
/// the guide is read on GitHub (the markdown renders there and the link always shows the newest text), not bundled in the
/// installer (a copy goes stale the day after a release and the installer carries 10 more files), and not on GitHub Pages
/// (the repository publishes no Pages site today; the link below works now and one constant moves it later).
/// A guard (Guards.Tests/UserGuideLinksTests) proves every file named here exists under docs/user_guide.
/// </summary>
public static class UserGuideLinks
{
    public const string BaseUrl = "https://github.com/Raaif-Yousuf/DNA-Entropy-Graph/blob/main/docs/user_guide/";

    public const string IndexFile = "README.md";

    /// <summary>Every guide file a page key can map to, for the guard.</summary>
    public static IReadOnlyList<string> AllFiles { get; } =
    [
        IndexFile, "03-run.md", "05-history.md", "06-costs-and-cleanup.md", "08-settings.md", "02-connect-google-cloud.md",
    ];

    /// <summary>The guide file for a navigation page key (the same keys <c>INavigator</c> uses); the guide index for any other key.</summary>
    public static string FileFor(string pageKey) => pageKey switch
    {
        "NewRun" => "03-run.md",
        "Runs" => "05-history.md",
        "Cloud" => "06-costs-and-cleanup.md",
        "Settings" => "08-settings.md",
        "Wizard" => "02-connect-google-cloud.md",
        _ => IndexFile,
    };

    public static Uri UriFor(string pageKey) => new(BaseUrl + FileFor(pageKey));
}
