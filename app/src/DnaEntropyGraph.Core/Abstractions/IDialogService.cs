namespace DnaEntropyGraph.Core.Abstractions;

/// <summary>
/// Wraps <c>ContentDialog</c> (which needs an explicit <c>XamlRoot</c> or it
/// throws - winui-dev skill) so Presentation can be faked in tests instead
/// of needing a real <c>XamlRoot</c>.
/// </summary>
public interface IDialogService
{
    Task<bool> ConfirmAsync(string title, string message, CancellationToken cancellationToken);
}
