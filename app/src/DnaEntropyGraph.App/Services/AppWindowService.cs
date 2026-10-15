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
    public void Attach(Window window)
    {
        _window = window;
        _active = GetForegroundWindow() == WinRT.Interop.WindowNative.GetWindowHandle(window);
        window.Activated += (_, args) => _active = args.WindowActivationState != WindowActivationState.Deactivated;
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
