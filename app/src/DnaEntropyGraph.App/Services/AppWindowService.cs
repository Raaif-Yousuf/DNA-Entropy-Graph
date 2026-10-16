using DnaEntropyGraph.Core.Abstractions;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;

namespace DnaEntropyGraph.App.Services;

/// <summary>
/// The main window for Presentation (<see cref="IAppWindow"/>, issue #107): whether it is in front, and how to bring it there when a
/// notification or a second launch hands over. Attached once by App.OnLaunched.
/// </summary>
public sealed class AppWindowService : IAppWindow
{
    private Window? _window;
    private volatile bool _active;

    public bool IsInForeground => _active;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    /// <summary>Called after the window is first activated; takes the real initial state (the Activated event only reports changes).</summary>
    public void Attach(Window window, Func<CancellationToken, Task<bool>>? confirmExit = null)
    {
        _window = window;
        _active = GetForegroundWindow() == WinRT.Interop.WindowNative.GetWindowHandle(window);
        window.Activated += (_, args) => _active = args.WindowActivationState != WindowActivationState.Deactivated;
        if (confirmExit is not null)
        {
            window.AppWindow.Closing += (_, args) => OnClosing(args, confirmExit);
        }
    }

    // Issue #113: the close is cancelled at once (the handler never blocks the UI thread) and the exit question runs asynchronously;
    // when it allows the exit, the window closes again with the question skipped. A second close while it is open is ignored.
    private bool _exitConfirmed;
    private bool _asking;

    private void OnClosing(AppWindowClosingEventArgs args, Func<CancellationToken, Task<bool>> confirmExit)
    {
        if (_exitConfirmed)
        {
            return;
        }

        args.Cancel = true;
        if (_asking)
        {
            return;
        }

        _asking = true;
        _ = AskThenCloseAsync(confirmExit);
    }

    private async Task AskThenCloseAsync(Func<CancellationToken, Task<bool>> confirmExit)
    {
        var allowed = true;
        try
        {
            allowed = await confirmExit(CancellationToken.None);
        }
        catch (Exception)
        {
            // The question is a safety net; a failure in it never traps the user in the app.
        }
        finally
        {
            _asking = false;
        }

        if (allowed)
        {
            _exitConfirmed = true;
            _window?.Close();
        }
    }

    /// <summary>Always called on the UI thread (<c>ActivationRouter</c> goes through the dispatcher).</summary>
    public void BringToFront()
    {
        if (_window is null)
        {
            return;
        }

        if (_window.AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
        {
            presenter.Restore();
        }

        _window.Activate();
    }
}
