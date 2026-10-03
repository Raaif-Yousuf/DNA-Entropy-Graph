using DnaEntropyGraph.App.Services;
using DnaEntropyGraph.App.Views;

namespace DnaEntropyGraph.App.Startup;

/// <summary>
/// Every page key -> Page type mapping <see cref="NavigationService"/>
/// needs. "NewRun" (#63), "RunProgress" (#66), "Runs" (#101), "Settings" (#106, Diagnostics group; #104 extends it)
/// and the viewer (#72/#73) are registered: "Cloud" is a sibling issue's own View (#105 per
/// <c>scripts/app_wiring_allowlist.json</c>) and is not registered yet; until it lands, its NavigationView item
/// resolves to the fallback <see cref="PlaceholderPage"/> (issue #490), never an empty frame.
/// </summary>
public static class NavigationRoutes
{
    public static void RegisterAll(NavigationService navigationService)
    {
        // Issue #63: the New run page, the first item of the navigation menu (ShellViewModel.InitialPageKeyValue).
        navigationService.RegisterPage(DnaEntropyGraph.Presentation.ViewModels.ShellViewModel.InitialPageKeyValue, typeof(NewRunPage));

        navigationService.RegisterPage("RunProgress", typeof(RunProgressPage));

        // Issue #101: the Runs page (history, re-run, re-download, deletes). The nav item's Tag is "Runs".
        navigationService.RegisterPage("Runs", typeof(RunsPage));

        // Issues #72/#73: the igv.js viewer. Navigate with the run's output folder (a string) as the parameter.
        navigationService.RegisterPage(DnaEntropyGraph.Presentation.ViewModels.ViewerViewModel.PageKey, typeof(ViewerPage));

        // Issue #106: the Settings page, so far only its Diagnostics group (Save diagnostics). #104 adds the rest of the
        // groups to this same page. The nav item's Tag is "Settings".
        navigationService.RegisterPage("Settings", typeof(SettingsPage));

        // Issue #490: every destination without a page of its own lands on one
        // generic "not built yet" page rather than an empty frame.
        navigationService.RegisterFallbackPage(typeof(PlaceholderPage));
    }
}
