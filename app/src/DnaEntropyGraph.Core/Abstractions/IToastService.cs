namespace DnaEntropyGraph.Core.Abstractions;

/// <summary>How a message looks and how long it stays (issue #585): info and success close themselves, warning and error stay until the user closes them.</summary>
public enum ToastSeverity
{
    Info,
    Success,
    Warning,
    Error,
}

/// <summary>
/// Shows a short message inside the app window. The implementation is the shell's message bar
/// (<c>InAppMessageCenter</c> in Presentation, bound to an InfoBar in MainWindow), so Presentation never touches WinUI types.
/// Safe to call from any thread. A Windows notification for a finished run while the app is in the background
/// (issue #107) is a different channel; it would be a second implementation behind this seam, composed with the in-app one.
/// </summary>
public interface IToastService
{
    void ShowToast(string title, string body, ToastSeverity severity = ToastSeverity.Info);
}
