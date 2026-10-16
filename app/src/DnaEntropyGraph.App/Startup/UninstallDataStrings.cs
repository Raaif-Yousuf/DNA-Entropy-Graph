namespace DnaEntropyGraph.App.Startup;

/// <summary>
/// Issue #264 review: the text of the uninstall question. The hook runs in the uninstaller's process before <c>Application.Start</c>, where the
/// resource loader may not resolve yet, and <c>ReswStringResourceProvider</c> then returns the KEY itself, which would put "UninstallRemoveData_Body"
/// in a dialog. So the lookup is tried first and these embedded English constants are the fallback. They are a deliberate, narrow carve-out from
/// Hard Rule 13 (strings live in Resources.resw): <c>UninstallStringsMatchResourcesTests</c> in Guards.Tests fails if either constant drifts
/// from the resw value, so the file stays the one source of truth.
/// </summary>
public static class UninstallDataStrings
{
    public const string TitleKey = "UninstallRemoveData_Title";
    public const string BodyKey = "UninstallRemoveData_Body";

    public const string EnglishTitle = "Remove your data too?";

    public const string EnglishBody = "DNA Entropy Graph is being removed. Also delete your run history, settings and saved copies of your input files from this computer? Choose No to keep them for a later reinstall. Your result files in your output folder are never deleted.";

    /// <summary>The dialog's title and body: the looked-up value when it is a real string, the English constant when the lookup throws, returns nothing, or returns the key.</summary>
    public static (string Title, string Body) Resolve(Func<string, string?> lookup)
        => (Pick(lookup, TitleKey, EnglishTitle), Pick(lookup, BodyKey, EnglishBody));

    private static string Pick(Func<string, string?> lookup, string key, string english)
    {
        try
        {
            var value = lookup(key);
            return string.IsNullOrWhiteSpace(value) || string.Equals(value, key, StringComparison.Ordinal) ? english : value;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning($"uninstall string lookup failed, using English: {ex.GetType().Name}");
            return english;
        }
    }
}
