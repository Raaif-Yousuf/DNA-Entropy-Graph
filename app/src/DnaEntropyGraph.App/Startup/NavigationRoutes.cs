using DnaEntropyGraph.App.Services;
using DnaEntropyGraph.App.Views;

namespace DnaEntropyGraph.App.Startup;

/// <summary>
/// Every page key -> Page type mapping <see cref="NavigationService"/>
/// needs. Only "RunProgress" is registered today (issue #66): "New run",
/// "Runs", "Cloud" and "Settings" are sibling issues' own Views (#63, #101,
/// #105, #104 per <c>scripts/app_wiring_allowlist.json</c>) and are
/// deliberately left unregistered rather than built here - clicking one of
/// those NavigationView items today is <see cref="NavigationService"/>'s
/// own documented no-op-rather-than-crash, not a new gap this session
/// introduced. See this session's report for the tracking issue filed on
/// making that gap visible to a user instead of silent.
/// </summary>
public static class NavigationRoutes
{
    public static void RegisterAll(NavigationService navigationService)
        => navigationService.RegisterPage("RunProgress", typeof(RunProgressPage));
}
