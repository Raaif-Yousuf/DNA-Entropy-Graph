using System.Reflection;
using System.Runtime.InteropServices;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Diagnostics;
using Microsoft.Web.WebView2.Core;

namespace DnaEntropyGraph.App.Services;

/// <summary>Reads the machine facts the support zip records, and the strings it must scrub (every signed-in email, the Windows user name).</summary>
internal static class DiagnosticsInfoProvider
{
    public static DiagnosticsInfo Current(IGcpAccount account) => new(
        AppVersion: Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0",
        OsDescription: RuntimeInformation.OSDescription,
        DotNetVersion: RuntimeInformation.FrameworkDescription,
        WebView2Version: WebView2Version(),
        UserProfilePath: Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        SensitiveValues: [.. account.Accounts.Select(a => a.Email), Environment.UserName],
        CreatedUtc: DateTimeOffset.UtcNow);

    private static string? WebView2Version()
    {
        try
        {
            return CoreWebView2Environment.GetAvailableBrowserVersionString();
        }
        catch (Exception)
        {
            // A missing runtime throws; the bundle then says "not found", which is itself the useful fact.
            return null;
        }
    }
}
