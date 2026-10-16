namespace DnaEntropyGraph.Presentation.Services;

/// <summary>Reads text from the Windows clipboard (issue #110, Ctrl+Shift+V on New run). Abstracted so a ViewModel stays free of WinUI types (Hard Rule 8).</summary>
public interface IClipboardTextReader
{
    /// <summary>The clipboard's text, or null when it holds no text or could not be read.</summary>
    Task<string?> ReadTextAsync();
}
