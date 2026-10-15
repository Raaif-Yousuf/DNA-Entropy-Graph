using DnaEntropyGraph.Presentation.Services;
using Windows.ApplicationModel.DataTransfer;

namespace DnaEntropyGraph.App.Services;

/// <summary>Reads the clipboard's text for Ctrl+Shift+V on New run (issue #110). Never throws: a busy or empty clipboard is null, and the page says so.</summary>
public sealed class ClipboardTextReader : IClipboardTextReader
{
    public async Task<string?> ReadTextAsync()
    {
        try
        {
            var content = Clipboard.GetContent();
            return content.Contains(StandardDataFormats.Text) ? await content.GetTextAsync() : null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
