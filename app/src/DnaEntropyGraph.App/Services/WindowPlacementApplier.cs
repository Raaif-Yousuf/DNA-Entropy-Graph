using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace DnaEntropyGraph.App.Services;

/// <summary>
/// Applies a loaded <see cref="WindowPlacement"/> to a real <see cref="Window"/>'s
/// <c>AppWindow</c> and saves it back on every move/resize (issue #62).
/// Kept out of <c>MainWindow.xaml.cs</c> so that file stays a flat sequence
/// of one-line calls (Hard Rule 8) even though nothing here actually
/// branches - the point is that a future change to this logic never has to
/// go anywhere near code-behind.
/// </summary>
public static class WindowPlacementApplier
{
    public static void Apply(Window window, WindowPlacementService placementService)
    {
        var appWindow = window.AppWindow;
        var placement = placementService.Load();
        appWindow.MoveAndResize(new RectInt32(placement.X, placement.Y, placement.Width, placement.Height));

        appWindow.Changed += (sender, _) => placementService.Save(
            new WindowPlacement(sender.Position.X, sender.Position.Y, sender.Size.Width, sender.Size.Height));
    }
}
