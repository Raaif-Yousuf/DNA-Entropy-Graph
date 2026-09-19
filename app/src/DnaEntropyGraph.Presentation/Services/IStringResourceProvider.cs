namespace DnaEntropyGraph.Presentation.Services;

/// <summary>
/// Resolves a <c>Strings/en-US/Resources.resw</c> key to display text (Hard
/// Rule 13: every user-visible string lives in the .resw, never inline in
/// XAML or C#). Abstracted the same way <c>IDialogService</c>/<c>IFilePicker</c>
/// wrap a WinUI-touching concern behind a plain interface, so a ViewModel
/// can build copy from a resource key with no WinUI reference of its own
/// (Hard Rule 8) and a test can substitute a trivial fake instead of a real
/// packaged resource map.
/// </summary>
public interface IStringResourceProvider
{
    /// <summary>
    /// The resolved string for <paramref name="key"/>, or the key itself if
    /// it cannot be resolved - never null, never an empty string standing
    /// in for "not found" (that would render identically to a real empty
    /// value and hide the failure, the same "blank label" trap CLAUDE.md's
    /// Critical Pitfalls names for a .resw that never made it into the
    /// packaged PRI resources).
    /// </summary>
    string GetString(string key);
}
