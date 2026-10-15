using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Presentation.ViewModels;

namespace DnaEntropyGraph.Presentation.Services;

/// <summary>
/// What the app does when it is activated from outside (issue #107): a click on a Windows notification, or a second launch of the exe
/// that was handed over to this one. The window always comes to the front; a "run=ID" argument also opens that run's Results page.
/// The arguments arrive from outside the app, so only <see cref="AppDeepLink"/>'s plain job id is acted on.
/// </summary>
public sealed class ActivationRouter(INavigator navigator, IAppWindow window, IDispatcher dispatcher)
{
    /// <summary>Handles one activation (from any thread). True when it named a run and the page was opened.</summary>
    public bool Handle(string? arguments)
    {
        var hasRun = AppDeepLink.TryGetRunId(arguments, out var jobId);
        dispatcher.Enqueue(() =>
        {
            window.BringToFront();
            if (hasRun)
            {
                navigator.NavigateTo(ResultsViewModel.PageKey, jobId);
            }
        });
        return hasRun;
    }
}
