namespace DnaEntropyGraph.App.Services;

/// <summary>The main window's handle, set once when the window is created, for WinRT pickers and dialogs that need an owner.</summary>
public sealed class WindowHandleProvider
{
    public nint Hwnd { get; set; }
}
