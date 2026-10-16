using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;

namespace DnaEntropyGraph.Presentation.Services;

/// <summary>The page the user was on and, for a run page, which run (issue #244).</summary>
public sealed record SessionState(string Page, string? RunId);

/// <summary>
/// Where the shell should open: <paramref name="NavKey"/> is the navigation item to select; when <paramref name="SubPage"/> is set the
/// shell then opens that page (with <paramref name="Parameter"/>, the run id) on top of it.
/// </summary>
public sealed record SessionTarget(string NavKey, string? SubPage, string? Parameter);

/// <summary>
/// The last page and run, kept in settings.json under <c>session.page</c> and <c>session.run</c> (issue #244). Both are excluded from
/// the settings export (they say where this PC's user was, not how they like the app). Never throws: a locked settings file means
/// "nothing remembered", and a failed save is dropped, because losing a restore point must never cost a navigation.
/// </summary>
public sealed class SessionStore(ISettingsStore settings)
{
    public const string PageKey = "session.page";
    public const string RunKey = "session.run";

    public SessionState? Load()
    {
        try
        {
            var page = settings.GetString(PageKey);
            if (string.IsNullOrWhiteSpace(page))
            {
                return null;
            }

            var run = settings.GetString(RunKey);
            return new SessionState(page, string.IsNullOrWhiteSpace(run) ? null : run);
        }
        catch (SettingsUnavailableException)
        {
            return null;
        }
    }

    public void Save(SessionState state)
    {
        try
        {
            if (settings.GetString(PageKey) != state.Page)
            {
                settings.SetString(PageKey, state.Page);
            }

            // Written even when empty: an old run id must not outlive a move to a page that has none.
            if ((settings.GetString(RunKey) ?? string.Empty) != (state.RunId ?? string.Empty))
            {
                settings.SetString(RunKey, state.RunId ?? string.Empty);
            }
        }
        catch (SettingsUnavailableException)
        {
            // Not remembered this time; navigation goes on.
        }
    }
}

/// <summary>
/// Decorates the app's <see cref="INavigator"/> so every navigation, from the nav rail or from any ViewModel, updates the
/// <see cref="SessionStore"/>. Only pages that can be reopened from a key are remembered: the four nav pages, and Results and
/// Run progress (with their run id). The viewer needs a folder and the wizard is a one-off, so they leave the last page alone.
/// </summary>
public sealed class RecordingNavigator(INavigator inner, SessionStore session) : INavigator
{
    public bool CanGoBack => inner.CanGoBack;

    // Forwarded, not swallowed (issues #499, #807): the shell subscribes to this decorator, which is what DI hands out.
    public string? CurrentPageKey => inner.CurrentPageKey;

    public event EventHandler? CurrentPageChanged
    {
        add => inner.CurrentPageChanged += value;
        remove => inner.CurrentPageChanged -= value;
    }

    public void GoBack() => inner.GoBack();

    public void NavigateTo(string pageKey, object? parameter = null)
    {
        inner.NavigateTo(pageKey, parameter);
        if (SessionPages.IsNavPage(pageKey))
        {
            session.Save(new SessionState(pageKey, null));
        }
        else if (SessionPages.IsRunPage(pageKey) && parameter is string runId && runId.Length > 0)
        {
            session.Save(new SessionState(pageKey, runId));
        }
    }
}

/// <summary>The page keys a session can reopen.</summary>
internal static class SessionPages
{
    public const string Results = "Results";
    public const string RunProgress = "RunProgress";
    public const string Runs = "Runs";

    public static bool IsNavPage(string key) => key is "NewRun" or Runs or "Cloud" or "Settings";

    public static bool IsRunPage(string key) => key is Results or RunProgress;
}

/// <summary>Turns the remembered <see cref="SessionState"/> into a <see cref="SessionTarget"/> at launch, checking the run still exists.</summary>
public sealed class SessionRestorer(SessionStore session, IRunRepository runs)
{
    /// <summary>Null means "open on the default page". Never throws.</summary>
    public async Task<SessionTarget?> ResolveAsync(CancellationToken cancellationToken)
    {
        var state = session.Load();
        if (state is null)
        {
            return null;
        }

        if (SessionPages.IsNavPage(state.Page))
        {
            return new SessionTarget(state.Page, null, null);
        }

        if (!SessionPages.IsRunPage(state.Page) || state.RunId is null)
        {
            return null;
        }

        RunRecord? run;
        try
        {
            run = (await runs.GetAllAsync(cancellationToken)).FirstOrDefault(r => string.Equals(r.JobId, state.RunId, StringComparison.Ordinal));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // An unreadable history is not a reason to stop the app opening.
            System.Diagnostics.Trace.TraceWarning($"session restore could not read the run history: {ex.GetType().Name}");
            return null;
        }

        if (run is null)
        {
            return null;
        }

        // A run that finished while the app was closed opens on its results, not on a progress page with nothing left to show.
        var page = state.Page == SessionPages.RunProgress && run.Phase is JobPhase.Completed or JobPhase.PartiallyCompleted
            ? SessionPages.Results
            : state.Page;
        return new SessionTarget(SessionPages.Runs, page, run.JobId);
    }
}
