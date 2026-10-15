namespace DnaEntropyGraph.Presentation.Viewer;

/// <summary>
/// The virtual hosts and URLs the embedded igv.js viewer uses (issue #72).
/// <c>file://</c> is blocked inside WebView2, so the bundle is served from
/// <c>https://viewer.deg/</c> (the app's Assets/viewer folder) and the run's
/// output folder from <c>https://run.deg/</c>, both through
/// <c>SetVirtualHostNameToFolderMapping</c>. Pure, so it is tested without a UI thread.
/// </summary>
public static class ViewerUrls
{
    public const string ViewerHost = "viewer.deg";
    public const string RunHost = "run.deg";
    public const string StartUrl = "https://viewer.deg/index.html";
    public const string RunBaseUrl = "https://run.deg/";

    /// <summary>Microsoft's Evergreen WebView2 Runtime bootstrapper page (the named action when the runtime is missing).</summary>
    public const string RuntimeDownloadUrl = "https://go.microsoft.com/fwlink/p/?LinkId=2124703";

    /// <summary>True only for https://viewer.deg/... : every other navigation is cancelled.</summary>
    public static bool IsAllowedNavigation(string? uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed))
        {
            return false;
        }

        return parsed.Scheme == Uri.UriSchemeHttps
            && string.Equals(parsed.Host, ViewerHost, StringComparison.OrdinalIgnoreCase)
            && parsed.IsDefaultPort
            && parsed.UserInfo.Length == 0;
    }
}

/// <summary>Decides whether the Evergreen WebView2 Runtime is installed from its version-string probe.</summary>
public static class WebViewRuntimeDecision
{
    /// <summary>
    /// <c>CoreWebView2Environment.GetAvailableBrowserVersionString()</c> throws, or returns
    /// empty, when no runtime is installed. Either means unavailable.
    /// </summary>
    public static bool IsAvailable(Func<string?> getVersion)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(getVersion());
        }
        catch (Exception)
        {
            return false;
        }
    }
}

/// <summary>Wraps the WinUI-bound runtime probe so the ViewModel stays free of a WinUI reference (Hard Rule 8).</summary>
public interface IWebViewRuntimeProbe
{
    /// <summary>The installed runtime's version, or null / throws when it is missing.</summary>
    string? GetVersion();
}
