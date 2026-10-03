using Microsoft.UI.Xaml;
using Windows.ApplicationModel.DataTransfer;

namespace DnaEntropyGraph.App.Services;

/// <summary>Reads what the user dropped on the New run page: files and folders, as paths. Kept out of the page's code-behind because it branches (Hard Rule 8).</summary>
internal static class DropPaths
{
    /// <summary>Shows the copy cursor for a drag that carries files or folders, and the "no" cursor otherwise.</summary>
    public static void AcceptFiles(DragEventArgs e)
        => e.AcceptedOperation = e.DataView.Contains(StandardDataFormats.StorageItems) ? DataPackageOperation.Copy : DataPackageOperation.None;

    /// <summary>The paths of every file and folder dropped; empty when the drop carried none.</summary>
    public static async Task<IReadOnlyList<string>> ReadAsync(DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            return [];
        }

        var items = await e.DataView.GetStorageItemsAsync();
        return items.Select(item => item.Path).ToList();
    }
}
