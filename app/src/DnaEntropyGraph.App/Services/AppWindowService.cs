using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Presentation.Services;
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
            var confirmation = new CloseConfirmation(confirmExit);
            window.AppWindow.Closing += (_, args) => OnClosing(args, confirmation);
        }
    }

    // Issue #113: the decision (cancel at once, ask once, re-close without asking again) is CloseConfirmation, tested without a window.
    private void OnClosing(AppWindowClosingEventArgs args, CloseConfirmation confirmation)
        => args.Cancel = confirmation.ShouldCancelClose(() => _window?.Close());

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
