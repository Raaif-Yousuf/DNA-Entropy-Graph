using DnaEntropyGraph.App.Services;
using DnaEntropyGraph.App.Views;

namespace DnaEntropyGraph.App.Startup;

/// <summary>
/// Every page key -> Page type mapping <see cref="NavigationService"/>
/// needs. Only "RunProgress" is registered today (issue #66): "New run",
/// "Runs", "Cloud" and "Settings" are sibling issues' own Views (#63, #101,
/// #105, #104 per <c>scripts/app_wiring_allowlist.json</c>) and are not
/// registered yet; until each lands, its NavigationView item resolves to the
/// fallback <see cref="PlaceholderPage"/> (issue #490), never an empty frame.
/// </summary>
public static class NavigationRoutes
{
    public static void RegisterAll(NavigationService navigationService)
    {
        navigationService.RegisterPage("RunProgress", typeof(RunProgressPage));

        // Issues #72/#73: the igv.js viewer. Navigate with the run's output folder (a string) as the parameter.
        navigationService.RegisterPage(DnaEntropyGraph.Presentation.ViewModels.ViewerViewModel.PageKey, typeof(ViewerPage));

        // Issue #490: every destination without a page of its own lands on one
        // generic "not built yet" page rather than an empty frame.
        navigationService.RegisterFallbackPage(typeof(PlaceholderPage));
    }
}
