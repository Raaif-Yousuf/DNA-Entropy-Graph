namespace DnaEntropyGraph.Core.Abstractions;

/// <summary>
/// A dialog with up to three labelled buttons (a <c>ContentDialog</c> has a primary, a secondary and a close button), so Presentation can ask "Stop, Delete or
/// Keep waiting" and be faked in tests without a <c>XamlRoot</c> (issue #675). Separate from <see cref="IDialogService"/> so the many yes/no callers are unchanged.
/// </summary>
public interface IChoiceDialogService
{
    /// <summary>
    /// Shows <paramref name="title"/> and <paramref name="message"/> with <paramref name="buttonLabels"/> (one to three; the first is the primary button, the second
    /// the secondary, the third the close button, which is also what Escape picks). Returns the index of the pressed button, or null when the dialog could not be
    /// shown (no window yet) or <paramref name="cancellationToken"/> closed it: null is "no answer", never a choice.
    /// </summary>
    Task<int?> ChooseAsync(string title, string message, IReadOnlyList<string> buttonLabels, CancellationToken cancellationToken);
}
