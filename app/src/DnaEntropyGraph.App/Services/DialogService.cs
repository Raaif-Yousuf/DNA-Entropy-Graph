using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Presentation.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DnaEntropyGraph.App.Services;

/// <summary>
/// The real confirm dialog (issue #101): a <c>ContentDialog</c> on the main window's <c>XamlRoot</c>.
/// Until <see cref="Attach"/> is called with the window there is no root to show on, and the answer is
/// "no", so a destructive action can never go ahead without the user having seen the question
/// (Hard Rule 14). Confirming is the primary button; the default button is Cancel.
/// </summary>
public sealed class DialogService : IDialogService
{
    private readonly IStringResourceProvider _strings;
    private Window? _window;

    public DialogService(IStringResourceProvider strings) => _strings = strings;

    /// <summary>Gives the service the window whose content hosts the dialogs. Called once by <c>App.OnLaunched</c>.</summary>
    public void Attach(Window window) => _window = window;

    public Task<bool> ConfirmAsync(string title, string message, CancellationToken cancellationToken)
    {
        var window = _window;
        if (window is null)
        {
            return Task.FromResult(false);
        }

        if (!window.DispatcherQueue.HasThreadAccess)
        {
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!window.DispatcherQueue.TryEnqueue(async () => completion.SetResult(await ShowAsync(window, title, message))))
            {
                return Task.FromResult(false);
            }

            return completion.Task;
        }

        return ShowAsync(window, title, message);
    }

    private async Task<bool> ShowAsync(Window window, string title, string message)
    {
        if (window.Content?.XamlRoot is not { } root)
        {
            return false;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = _strings.GetString("Dialog_Confirm_Primary"),
            CloseButtonText = _strings.GetString("Dialog_Confirm_Cancel"),
            DefaultButton = ContentDialogButton.Close,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }
}
