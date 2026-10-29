namespace DnaEntropyGraph.Presentation.Services;

/// <summary>
/// Whether Windows is in a high contrast theme, and when that changes while the app runs (issue #701). Presentation has no WinUI
/// reference (Hard Rule 8), so the App project implements this over <c>AccessibilitySettings</c>. <see cref="Changed"/> may be raised on any thread.
/// </summary>
public interface IHighContrastSource
{
    bool IsHighContrast { get; }

    event EventHandler? Changed;
}
