using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Presentation.ViewModels;

namespace DnaEntropyGraph.Presentation.Services;

/// <summary>
/// What the app does when a run finishes (issue #643), each step only if its <see cref="NotificationSettings"/> switch is on:
/// a toast, opening the run's output folder, opening the Results page. The shell calls it for every run that reaches Completed
/// or PartiallyCompleted, so it works whatever page the user is on. It never throws: a step that fails must not stop the next.
/// </summary>
public sealed class RunCompletionActions(
    NotificationSettings settings,
    IToastService toasts,
    IFolderLauncher folders,
    INavigator navigator,
    IRunRepository runs,
    IStringResourceProvider strings,
    IDispatcher dispatcher,
    IDesktopNotifier? desktop = null)
{
    /// <summary>
    /// A run that ended in Failed (issue #107): a Windows notification only (the run's own page already says why, and it must not jump
    /// away or open a folder for a run with nothing in it), still behind the Notify when done switch.
    /// </summary>
    public async Task OnRunFailedAsync(string jobId)
    {
        var choices = await Task.Run(settings.Snapshot).ConfigureAwait(false);
        if (choices.NotifyWhenDone && MarkFailedNotified(jobId))
        {
            NotifyDesktop(strings.GetString("RunFailed_Title"), strings.GetString("RunFailed_Body"), jobId);
        }
    }

    // The same Failed message can arrive twice for one run (a reattach, a re-poll); one toast per run is enough.
    private readonly HashSet<string> _failedNotified = [];

    private bool MarkFailedNotified(string jobId)
    {
        lock (_failedNotified)
        {
            return _failedNotified.Add(jobId);
        }
    }

    private void NotifyDesktop(string title, string body, string jobId)
    {
        try
        {
            desktop?.Notify(title, body, jobId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A notification that cannot be shown (Focus Assist, a policy) must not stop the next step.
            System.Diagnostics.Trace.TraceWarning($"desktop notification failed: {ex.GetType().Name}");
        }
    }

    public async Task OnRunFinishedAsync(string jobId, JobPhase phase)
    {
        var partial = phase == JobPhase.PartiallyCompleted;

        // One read, on the thread pool: the caller is the thread that drives the run, and the settings store can wait on a mutex.
        var choices = await Task.Run(settings.Snapshot).ConfigureAwait(false);
        if (choices.NotifyWhenDone)
        {
            dispatcher.Enqueue(() => toasts.ShowToast(
                strings.GetString(partial ? "RunPartial_Title" : "RunDone_Title"),
                strings.GetString(partial ? "RunPartial_Body" : "RunDone_Body"),
                partial ? ToastSeverity.Warning : ToastSeverity.Success));
            NotifyDesktop(strings.GetString(partial ? "RunPartial_Title" : "RunDone_Title"), strings.GetString(partial ? "RunPartial_Body" : "RunDone_Body"), jobId);
        }

        if (choices.OpenFolderWhenDone)
        {
            await OpenFolderAsync(jobId).ConfigureAwait(false);
        }

        if (choices.OpenResultsWhenDone)
        {
            dispatcher.Enqueue(() => navigator.NavigateTo(ResultsViewModel.PageKey, jobId));
        }
    }

    private async Task OpenFolderAsync(string jobId)
    {
        try
        {
            var all = await runs.GetAllAsync(CancellationToken.None).ConfigureAwait(false);
            if (all.FirstOrDefault(r => r.JobId == jobId)?.OutputDir is { Length: > 0 } folder)
            {
                dispatcher.Enqueue(() => folders.OpenFolder(folder));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            System.Diagnostics.Trace.TraceWarning($"output folder could not be looked up after a run: {ex.GetType().Name}");
        }
    }
}
