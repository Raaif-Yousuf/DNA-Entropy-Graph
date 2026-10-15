using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Presentation.ViewModels;

namespace DnaEntropyGraph.Presentation.Services;

/// <summary>
/// What the app does when it is activated from outside (issue #107): a click on a Windows notification, or a second launch of the exe
/// that was handed over to this one. The window always comes to the front; a "run=ID" argument also opens that run's Results page.
/// A launch that names sequence files (Open with, issue #108) opens New run with them added. The arguments arrive from outside the app,
/// so only <see cref="AppDeepLink"/>'s plain job id and local sequence-file paths are acted on.
/// </summary>
public sealed class ActivationRouter(
    INavigator navigator,
    IAppWindow window,
    IDispatcher dispatcher,
    ShellReadiness? readiness = null,
    NewRunIntake? intake = null)
{
    /// <summary>
    /// Handles one activation (from any thread). True when it named a run. Nothing is done until the shell has made its first
    /// navigation (<paramref name="readiness"/>), because that navigation would otherwise land after ours and replace it.
    /// </summary>
    public bool Handle(string? arguments)
    {
        var hasRun = AppDeepLink.TryGetRunId(arguments, out var jobId);
        var hasFiles = AppDeepLink.TryGetInputFiles(arguments, out var inputFiles);
        void Act() => dispatcher.Enqueue(() =>
        {
            window.BringToFront();
            if (hasRun)
            {
                navigator.NavigateTo(ResultsViewModel.PageKey, jobId);
            }
            else if (hasFiles)
            {
                // "Open with" from Explorer (issue #108): into the New run screen that is showing, so its draft survives; else a new one.
                var items = new DroppedItems(inputFiles, 0, false);
                if (intake?.TryAdd(items) != true)
                {
                    navigator.NavigateTo(ShellViewModel.InitialPageKeyValue, items);
                }
            }
        });

        if (readiness is null)
        {
            Act();
        }
        else
        {
            readiness.WhenReady(Act);
        }

        return hasRun;
    }
}
