namespace DnaEntropyGraph.Presentation.Services;

/// <summary>
/// Applies a theme choice ("Light", "Dark" or "System", the strings <c>SettingsViewModel</c> persists) to the running window
/// at once, so choosing one in Settings needs no restart (#639). Presentation has no WinUI reference (Hard Rule 8), so the
/// App project implements this over the shell's root element.
/// </summary>
public interface IThemeApplier
{
    void Apply(string theme);
}
