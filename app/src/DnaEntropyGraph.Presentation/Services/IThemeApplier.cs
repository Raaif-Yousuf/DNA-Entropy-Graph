namespace DnaEntropyGraph.Presentation.Services;

/// <summary>
/// Applies a theme choice ("Light", "Dark" or "System", the strings <c>SettingsViewModel</c> persists) to the running window
/// at once, so choosing one in Settings needs no restart (#639). Presentation has no WinUI reference (Hard Rule 8), so the
/// App project implements this over the shell's root element.
/// </summary>
public interface IThemeApplier
{
    /// <summary>The theme last applied to the window, or null before the first <see cref="Apply"/>. Settings reads it so its radio shows what is on screen even when the choice could not be saved (#558).</summary>
    string? CurrentTheme { get; }

    void Apply(string theme);
}
