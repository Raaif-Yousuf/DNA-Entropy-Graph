using DnaEntropyGraph.Presentation.ViewModels;
using Microsoft.UI.Xaml;
using Windows.ApplicationModel.DataTransfer;

namespace DnaEntropyGraph.App.Services;

/// <summary>Reads what the user dropped on the New run page: files and folders, as paths. Kept out of the page's code-behind because it branches (Hard Rule 8).</summary>
internal static class DropPaths
{
    /// <summary>Shows the copy cursor for a drag that carries files or folders, and the "no" cursor otherwise.</summary>
    public static void AcceptFiles(DragEventArgs e)
        => e.AcceptedOperation = e.DataView.Contains(StandardDataFormats.StorageItems) ? DataPackageOperation.Copy : DataPackageOperation.None;

    /// <summary>
    /// The paths of every file and folder dropped. Never throws (the page calls this from an <c>async void</c> event
    /// handler, where an exception ends the app): a drop that cannot be read comes back as <c>Failed</c>, and an
    /// item with no path (a file inside a zip, an e-mail attachment) is counted, not turned into a blank pill.
    /// </summary>
    public static async Task<DroppedItems> ReadAsync(DragEventArgs e)
    {
        var deferral = e.GetDeferral();
        try
        {
            if (!e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                return new DroppedItems([], 0, false);
            }

            var items = await e.DataView.GetStorageItemsAsync();
            var paths = items.Select(item => item.Path).Where(path => !string.IsNullOrEmpty(path)).ToList();
            return new DroppedItems(paths, items.Count - paths.Count, false);
        }
        catch (Exception)
        {
            // Any failure of the shell's drag data (a revoked drop, a COM error) means "could not read this drop".
            return new DroppedItems([], 0, true);
        }
        finally
        {
            deferral.Complete();
        }
    }
}
