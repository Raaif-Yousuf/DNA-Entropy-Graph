using System.Diagnostics;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Presentation.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DnaEntropyGraph.App.Services;

/// <summary>
/// The real confirm dialog (issue #101): a <c>ContentDialog</c> on the main window's <c>XamlRoot</c>.
/// Until <see cref="Attach"/> is called with the window there is no root to show on, and the answer is
/// "no" (traced), so a destructive action can never go ahead without the user having seen the question
/// (Hard Rule 14). Confirming is the primary button; the default button is Cancel.
/// WinUI allows one open ContentDialog per window, so calls are queued one at a time; a caller whose token
/// is cancelled while waiting gets <see cref="OperationCanceledException"/> and never shows a dialog.
/// </summary>
public sealed class DialogService : IDialogService
{
    private readonly IStringResourceProvider _strings;
    private readonly SemaphoreSlim _one = new(1, 1);
    private Window? _window;

    public DialogService(IStringResourceProvider strings) => _strings = strings;

    /// <summary>Gives the service the window whose content hosts the dialogs. Called once by <c>App.OnLaunched</c>.</summary>
    public void Attach(Window window) => _window = window;

    public async Task<bool> ConfirmAsync(string title, string message, CancellationToken cancellationToken)
    {
        var window = _window;
        if (window is null)
        {
            Trace.TraceWarning("DialogService: a confirmation was requested before a window was attached; answering no.");
            return false;
        }

        await _one.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return await OnUiThreadAsync(window, () => ShowAsync(window, title, message, cancellationToken)).ConfigureAwait(false);
        }
        finally
        {
            _one.Release();
        }
    }

    private static Task<bool> OnUiThreadAsync(Window window, Func<Task<bool>> show)
    {
        if (window.DispatcherQueue.HasThreadAccess)
        {
            return show();
        }

        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var queued = window.DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                completion.SetResult(await show());
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        });
        if (!queued)
        {
            completion.SetException(new InvalidOperationException("The window is closing; the dialog could not be shown."));
        }

        return completion.Task;
    }

    private async Task<bool> ShowAsync(Window window, string title, string message, CancellationToken cancellationToken)
    {
        if (window.Content?.XamlRoot is not { } root)
        {
            Trace.TraceWarning("DialogService: the window has no XamlRoot yet; answering no.");
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

        // A cancelled token closes the open dialog and counts as "no".
        using var registration = cancellationToken.Register(() => window.DispatcherQueue.TryEnqueue(dialog.Hide));
        var result = await dialog.ShowAsync();
        cancellationToken.ThrowIfCancellationRequested();
        return result == ContentDialogResult.Primary;
    }
}
