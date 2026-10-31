using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Cloud;
using DnaEntropyGraph.Presentation.Messaging;
using DnaEntropyGraph.Presentation.Services;

namespace DnaEntropyGraph.Presentation.ViewModels;

/// <summary>
/// Issue #830 review M3: the shell's "Sign in as (the account)" bar. The startup reconcile cannot look at a run, or stop its computer, when the Google
/// account the run was made under is signed out; the reconciler tells the app (<see cref="AccountSignInNeededMessage"/>) and this keeps the list of
/// accounts waiting. An entry goes away only when that account is signed in again (checked on the account list, never assumed from "a sign-in happened"),
/// so signing in as somebody else leaves the bar. A bar the person closed stays closed until a NEW account needs a sign-in (the reconnect probe repeats
/// the same message). The email is looked up for the text and never logged.
/// </summary>
public sealed partial class SignInNeededViewModel : ObservableObject
{
    /// <summary>The marker for an account whose id was not known.</summary>
    private const string UnknownSub = "";

    private readonly IGcpAccount _account;
    private readonly IStringResourceProvider _strings;
    private readonly IDispatcher _dispatcher;
    private readonly IDiagnosticsLog _log;
    private readonly HashSet<string> _waiting = new(StringComparer.Ordinal);

    [ObservableProperty]
    private bool _isOpen;

    /// <summary>Review F7a: set when the last Sign in press failed for a reason that is not a sign-in answer (the browser wait was cancelled, the network failed); the bar then says the sign-in failed. Cleared by a new account waiting or a sign-in that completes.</summary>
    private bool _signInFailed;

    [ObservableProperty]
    private string _title = string.Empty;

    public SignInNeededViewModel(IGcpAccount account, IMessenger messenger, IStringResourceProvider strings, IDispatcher dispatcher, IDiagnosticsLog? log = null)
    {
        _log = log ?? NullDiagnosticsLog.Instance;
        _account = account;
        _strings = strings;
        _dispatcher = dispatcher;
        messenger.Register<SignInNeededViewModel, AccountSignInNeededMessage>(this, static (vm, message) => vm._dispatcher.Enqueue(() => vm.Add(message.AccountSub)));

        // The shell lives as long as the app, so no unsubscribe.
        account.AccountChanged += (_, _) => _dispatcher.Enqueue(Prune);
    }

    /// <summary>What the bar says under its title: one line, the action and why.</summary>
    public string Message => _strings.GetString(_signInFailed ? "AuthError_SIGNIN_FAILED" : "SignInNeeded_Message");

    /// <summary>Not an [ObservableProperty]: nothing binds the flag itself, the bar binds <see cref="Message"/>, so a change raises PropertyChanged for Message only.</summary>
    private void SetSignInFailed(bool value)
    {
        if (_signInFailed == value)
        {
            return;
        }

        _signInFailed = value;
        OnPropertyChanged(nameof(Message));
    }

    private void Add(string? sub)
    {
        var grew = _waiting.Add(sub ?? UnknownSub);
        Prune();
        if (grew && _waiting.Count > 0)
        {
            SetSignInFailed(false);
            IsOpen = true;
        }
    }

    /// <summary>Drops every account that is signed in again and rewrites the title; closes the bar when none is left.</summary>
    private void Prune()
    {
        var accounts = _account.Accounts;
        _waiting.RemoveWhere(sub => sub.Length > 0 && accounts.Any(a => a.Sub == sub && !a.NeedsSignIn));
        Title = BuildTitle(accounts);
        if (_waiting.Count == 0)
        {
            IsOpen = false;
        }
    }

    private string BuildTitle(IReadOnlyList<AccountInfo> accounts)
    {
        if (_waiting.Count == 0)
        {
            return string.Empty;
        }

        var emails = _waiting
            .Select(sub => accounts.FirstOrDefault(a => a.Sub == sub)?.Email)
            .Where(email => !string.IsNullOrEmpty(email))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return emails.Count == 0
            ? _strings.GetString("SignInNeeded_TitleUnknown")
            : string.Format(System.Globalization.CultureInfo.CurrentCulture, _strings.GetString("SignInNeeded_Title"), string.Join(_strings.GetString("SignInNeeded_Joiner"), emails));
    }

    /// <summary>The bar's button: starts Google sign-in. A cancelled or failed sign-in keeps the bar; the sign-in screen already said why.</summary>
    [RelayCommand]
    private async Task SignInAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _account.SignInAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (AccountAuthException)
        {
            return;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Review F7a: not a sign-in answer (a cancelled browser wait, a network error): it must not escape the command. Log the class only; the bar says the sign-in failed and keeps its button.
            _log.Warning("sign-in-needed", null, ex.GetType().Name);
            _dispatcher.Enqueue(() => SetSignInFailed(true));
            return;
        }

        // An account whose id was never known cannot be matched to the list, so any completed sign-in answers it.
        _dispatcher.Enqueue(() =>
        {
            SetSignInFailed(false);
            _waiting.Remove(UnknownSub);
            Prune();
        });
    }
}
