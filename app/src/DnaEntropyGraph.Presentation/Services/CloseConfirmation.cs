namespace DnaEntropyGraph.Presentation.Services;

/// <summary>
/// Issue #113: the decision behind the window's Closing handler, with no WinUI in it (<c>AppWindowService</c> only forwards the event). The first close is cancelled at once
/// (the handler never blocks the UI thread) and the exit question runs asynchronously; a close while it is open is cancelled and does not ask again; when the question allows
/// the exit the window is closed again and THAT close goes through without a second question. A refused exit leaves the window open, and the next close asks again.
/// A question that throws allows the exit: the question is a safety net and never traps the user in the app.
/// </summary>
public sealed class CloseConfirmation(Func<CancellationToken, Task<bool>> confirmExit)
{
    private readonly object _gate = new();
    private bool _confirmed;
    private bool _asking;

    /// <summary>The most recent question (completed when none was asked), so a test can wait for it. Never faults.</summary>
    public Task Settled { get; private set; } = Task.CompletedTask;

    /// <summary>True when the close in progress must be cancelled. <paramref name="closeWindow"/> closes the window again once the exit was allowed.</summary>
    public bool ShouldCancelClose(Action closeWindow)
    {
        lock (_gate)
        {
            if (_confirmed)
            {
                return false;
            }

            if (_asking)
            {
                return true;
            }

            _asking = true;
            Settled = AskThenCloseAsync(closeWindow);
            return true;
        }
    }

    private async Task AskThenCloseAsync(Action closeWindow)
    {
        // Stays on the caller's (UI) context: closeWindow touches the window. Never closes the window from inside the Closing event that started the question, even when the answer is ready at once.
        var allowed = true;
        try
        {
            allowed = await confirmExit(CancellationToken.None);
        }
        catch (Exception)
        {
            // See the summary: allowed stays true.
        }

        await Task.Yield();

        lock (_gate)
        {
            _asking = false;
            _confirmed = allowed;
        }

        if (allowed)
        {
            closeWindow();
        }
    }
}
