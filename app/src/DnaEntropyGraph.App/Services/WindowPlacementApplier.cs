using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;
using WinRT.Interop;

namespace DnaEntropyGraph.App.Services;

/// <summary>
/// Applies a loaded <see cref="WindowPlacement"/> to a real <see cref="Window"/>'s
/// <c>AppWindow</c> and saves it back on every move/resize (issue #62).
/// Kept out of <c>MainWindow.xaml.cs</c> so that file stays a flat sequence
/// of one-line calls (Hard Rule 8).
///
/// Issue #489: while the window is minimized or maximized nothing is saved, so the
/// last normal placement survives (a maximized window's rect is the work area, and
/// restoring it as a normal window would be wrong; the maximized state itself is
/// not persisted). On a fresh profile the 1280x800 DIP default is scaled by the
/// window's DPI, and a saved rect that no longer overlaps any monitor falls back to it.
/// </summary>
public static class WindowPlacementApplier
{
    public static void Apply(Window window, WindowPlacementService placementService)
    {
        var appWindow = window.AppWindow;
        var scale = GetDpiForWindow(WindowNative.GetWindowHandle(window)) / 96.0;
        var saved = placementService.TryLoad();
        var probe = saved ?? WindowPlacement.Default.Scaled(scale);
        var workArea = DisplayArea.GetFromRect(new RectInt32(probe.X, probe.Y, probe.Width, probe.Height), DisplayAreaFallback.Nearest).WorkArea;

        var placement = saved;
        if (placement is null || !placement.IsReachableOn(workArea.X, workArea.Y, workArea.Width, workArea.Height))
        {
            // MEASURED 2026-10-02: on this 3200x2000 px screen at 250% the DIP default is the whole screen,
            // so a first launch (or an unreachable saved rect) is kept inside a 5% margin of the work area.
            var marginX = workArea.Width / 20;
            var marginY = workArea.Height / 20;
            placement = WindowPlacement.Default.Scaled(scale)
                .FitInto(workArea.X + marginX, workArea.Y + marginY, workArea.Width - (2 * marginX), workArea.Height - (2 * marginY));
        }

        appWindow.MoveAndResize(new RectInt32(placement.X, placement.Y, placement.Width, placement.Height));

        appWindow.Changed += (sender, _) =>
        {
            if (sender.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Restored })
            {
                placementService.Save(new WindowPlacement(sender.Position.X, sender.Position.Y, sender.Size.Width, sender.Size.Height));
            }
        };
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);
}
