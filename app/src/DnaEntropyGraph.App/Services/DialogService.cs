using DnaEntropyGraph.Core.Abstractions;

namespace DnaEntropyGraph.App.Services;

/// <summary>
/// Placeholder for <c>ContentDialog</c>, which needs an explicit
/// <c>XamlRoot</c> or it throws (winui-dev skill). Wiring a real XamlRoot
/// is a follow-up issue once a page exists to own one.
/// </summary>
public sealed class DialogService : IDialogService
{
    public Task<bool> ConfirmAsync(string title, string message, CancellationToken cancellationToken) => Task.FromResult(false);
}
