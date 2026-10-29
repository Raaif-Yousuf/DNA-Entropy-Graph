using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Cloud;
using DnaEntropyGraph.Presentation.Services;

namespace DnaEntropyGraph.Presentation.ViewModels;

/// <summary>
/// The Settings "Budget alert" group (issues #148 and #222): one button makes a Google Cloud Billing budget that emails the user at
/// 50, 90 and 100 percent of a monthly amount. The group edits and removes it too. The budget lives on the billing account, and a
/// project owner is often not a billing administrator, so a refusal is the common case: it becomes one plain sentence plus a link to
/// the console budgets page (<see cref="ConsoleLink"/>) and a copyable request for the administrator (<see cref="CopyRequestCommand"/>),
/// never Google's own text. The eventual standing-costs card (#219) binds the same <see cref="SaveCommand"/>, <see cref="HasBudget"/>,
/// <see cref="StatusText"/> and <see cref="ErrorText"/>, or hosts this group's view model as it is.
/// </summary>
public sealed partial class BudgetSettingsViewModel : ObservableObject
{
    private readonly IBudgetService _budgets;
    private readonly IGcpAccount _account;
    private readonly IShellLauncher _shell;
    private readonly IStringResourceProvider _strings;

    /// <summary>The amount box. Typed text is kept as typed; it is parsed only when Save is pressed.</summary>
    [ObservableProperty]
    private string _amountText = Format(BudgetNaming.DefaultAmountUsd);

    // True once the user typed in the box: a late read from Google must never overwrite it.
    private bool _amountEdited;
    private bool _settingAmount;
    private string? _shownProject;

    /// <summary>One plain line about the current state or the last success. Empty when there is nothing to say.</summary>
    [ObservableProperty]
    private string _statusText = string.Empty;

    /// <summary>One plain sentence naming what went wrong and the one thing to do. Empty when the last action worked.</summary>
    [ObservableProperty]
    private string _errorText = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveCommand))]
    private bool _hasBudget;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    [NotifyCanExecuteChangedFor(nameof(RemoveCommand))]
    private bool _hasProject;

    // True only while the LAST read of the budget state succeeded. Save and Remove need it: blind, they could overwrite or delete a budget the page never showed.
    private bool _stateKnown;

    // Plain state, not an observable property: no view binds it, only Save and Remove gate on it, so a change raises their CanExecuteChanged directly.
    private bool StateKnown
    {
        get => _stateKnown;
        set
        {
            if (_stateKnown == value)
            {
                return;
            }

            _stateKnown = value;
            SaveCommand.NotifyCanExecuteChanged();
            RemoveCommand.NotifyCanExecuteChanged();
        }
    }

    // The project that successful read was FOR. The selection can change without a Refresh, and Save and Remove re-read the live selection, so they also need it to equal the known project.
    private string? _knownProject;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    [NotifyCanExecuteChangedFor(nameof(RemoveCommand))]
    [NotifyPropertyChangedFor(nameof(CanEdit))]
    private bool _isBusy;

    // Which request text the Copy button puts on the clipboard: a billing administrator's, or the organization's administrator's.
    private string _requestKey = "BudgetRequestText";

    /// <summary>The console page the error's link action opens (the account's budgets page, or the billing home). Null when no error offers one.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowConsoleLink))]
    private Uri? _consoleLink;

    [ObservableProperty]
    private bool _showCopyRequest;

    public BudgetSettingsViewModel(IBudgetService budgets, IGcpAccount account, IShellLauncher shell, IStringResourceProvider strings)
    {
        _budgets = budgets;
        _account = account;
        _shell = shell;
        _strings = strings;
        // Fields, not properties: constructing the page must not run any command.
        _hasProject = ProjectId() is not null;
    }

    public bool ShowConsoleLink => ConsoleLink is not null;

    /// <summary>False while a save, remove or look runs: text typed during it would be replaced by its result.</summary>
    public bool CanEdit => !IsBusy;

    partial void OnAmountTextChanged(string value)
    {
        if (!_settingAmount)
        {
            _amountEdited = true;
        }
    }

    /// <summary>Looks at the budget every time the page opens, so the box shows what Google holds. Never throws.</summary>
    [RelayCommand]
    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        // A refresh that lands while a save or remove runs would race it and clear its messages.
        if (IsBusy)
        {
            return;
        }

        ClearMessages();
        StateKnown = false;
        _knownProject = null;

        // A budget the last read found belongs to a read that is over: every look starts with none shown, and a failed one leaves none.
        HasBudget = false;
        _requestKey = "BudgetRequestText";
        HasProject = ProjectId() is not null;
        if (ProjectId() is not { } project)
        {
            HasBudget = false;
            StatusText = _strings.GetString("BudgetNeedsProject");
            return;
        }

        // What was typed was typed for one project: another project's budget is shown (and the box is Google's again) when it is chosen.
        // Only a real CHANGE counts: the first open has no other project whose text could leak, and anything typed before the first look is the user's own.
        if (!string.Equals(project, _shownProject, StringComparison.Ordinal))
        {
            var changed = _shownProject is not null;
            _shownProject = project;
            if (changed)
            {
                _amountEdited = false;

                // Text typed for the other project must not survive into this one (and be saved as its budget); an existing budget here replaces the default below.
                SetAmount(Format(BudgetNaming.DefaultAmountUsd));
            }
        }

        IsBusy = true;
        try
        {
            Show(await _budgets.GetAsync(project, cancellationToken), project);
        }
        catch (OperationCanceledException)
        {
            // The look was interrupted (the page went away, or it was cancelled): Create and Remove stay off, so say the one thing that turns them back on.
            StatusText = _strings.GetString("BudgetStatusCouldNotCheck");
        }
        catch (CloudOperationException ex)
        {
            // Nobody asked for this look, so a refusal is quiet information, not a red error; red is for a Save the user pressed. Each kind names the action that can work for it.
            System.Diagnostics.Trace.TraceWarning($"Budget look refused: {ex.Kind}");
            await ReportLookAsync(ex, project, cancellationToken);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceError($"Budget look failed: {ex.GetType().Name}");
            StatusText = _strings.GetString("BudgetStatusCouldNotCheck");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        // ExecuteAsync does not consult CanExecute: the invariant (no write after a failed or missing read) is enforced here.
        if (!CanSave())
        {
            return;
        }

        ClearMessages();
        if (ProjectId() is not { } project)
        {
            return;
        }

        if (!BudgetAmountParser.TryParse(AmountText, CultureInfo.CurrentCulture, out var amount) || !BudgetNaming.IsValidAmount(amount))
        {
            ErrorText = _strings.GetString("BudgetAmountInvalid");
            return;
        }

        await RunAsync(async () =>
        {
            var saved = await _budgets.SaveAsync(project, amount, cancellationToken);
            // What was saved (rounded to cents) replaces what was typed, and the box is Google's again.
            _amountEdited = false;
            Show(saved, project);
            StatusText = saved.ThresholdPercents.Count == 0
                ? _strings.GetString("BudgetStatusNoThresholds")
                : string.Format(CultureInfo.CurrentCulture, _strings.GetString("BudgetSaved"), Format(saved.AmountUsd), Thresholds(saved));
        }, project, cancellationToken);
    }

    private bool CanSave() => HasProject && KnownForSelection() && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanRemove))]
    private async Task RemoveAsync(CancellationToken cancellationToken)
    {
        if (!CanRemove())
        {
            return;
        }

        ClearMessages();
        if (ProjectId() is not { } project)
        {
            return;
        }

        await RunAsync(async () =>
        {
            // False means Google held none (already gone): say that, not "removed".
            var removed = await _budgets.RemoveAsync(project, cancellationToken);
            HasBudget = false;
            RestoreDefaultAmountIfEmpty();
            StatusText = _strings.GetString(removed ? "BudgetRemoved" : "BudgetRemovedNone");
        }, project, cancellationToken, removing: true);
    }

    private bool CanRemove() => HasBudget && HasProject && KnownForSelection() && !IsBusy;

    // The last successful read must be of the project that is selected NOW (the selection can change without a Refresh).
    private bool KnownForSelection() => StateKnown && _knownProject is { } known && string.Equals(known, ProjectId(), StringComparison.Ordinal);

    /// <summary>Copies the plain-words request the user can send to their billing administrator.</summary>
    [RelayCommand]
    private void CopyRequest()
    {
        // The parsed amount (or the default offered), never the raw box text: "abc" must not become "a monthly budget of abc".
        var amount = BudgetAmountParser.TryParse(AmountText, CultureInfo.CurrentCulture, out var parsed) && BudgetNaming.IsValidAmount(parsed) ? parsed : BudgetNaming.DefaultAmountUsd;
        var text = string.Format(CultureInfo.CurrentCulture, _strings.GetString(_requestKey), ProjectId() ?? string.Empty, Format(amount));
        // The confirmation names who the copied request is for, so it follows the request that was copied.
        var copied = _requestKey switch
        {
            "BudgetRequestText_ApiRead" => "BudgetRequestCopied_ApiRead",
            "BudgetRequestText_OrgPolicy" => "BudgetRequestCopied_OrgPolicy",
            _ => "BudgetRequestCopied",
        };
        StatusText = _strings.GetString(_shell.CopyText(text) ? copied : "BudgetRequestCopyFailed");
    }

    private async Task RunAsync(Func<Task> action, string project, CancellationToken cancellationToken, bool removing = false)
    {
        IsBusy = true;
        try
        {
            await action();
        }
        catch (OperationCanceledException)
        {
            // The page went away: nothing to report.
        }
        catch (CloudOperationException ex)
        {
            await ReportAsync(ex, project, cancellationToken, removing);
        }
        catch (Exception ex)
        {
            // A command must never throw into the UI. Only the class is traced (a message can carry a name or an account id).
            System.Diagnostics.Trace.TraceError($"Budget action failed: {ex.GetType().Name}");
            ErrorText = _strings.GetString("BudgetError_Other");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ReportAsync(CloudOperationException ex, string project, CancellationToken cancellationToken, bool removing)
    {
        // The raw Google text goes to the diagnostics log by class only; the screen gets one sentence and one action (Hard Rule 13).
        System.Diagnostics.Trace.TraceWarning($"Budget call refused: {ex.Kind}");
        if (ex.Error.Code == BudgetErrorCodes.InvalidAmount)
        {
            ErrorText = _strings.GetString("BudgetAmountInvalid");
            return;
        }

        switch (ex.Kind)
        {
            case CloudErrorKind.Permission when ex.Error.Code == BudgetErrorCodes.ApiEnablePermission:
                // The right that is missing is on the project, not a billing role: the action is a project owner, or the API's own page.
                ErrorText = _strings.GetString("BudgetError_ApiEnablePermission");
                ConsoleLink = new Uri(BudgetLinks.ForApi(project));
                break;
            case CloudErrorKind.Permission when ex.Error.Code == BudgetErrorCodes.ApiReadPermission:
                // The read of the project's API state was refused: the person to ask is a project owner (Service Usage Viewer), and nothing was saved.
                ErrorText = _strings.GetString("BudgetError_ApiReadPermission");
                _requestKey = "BudgetRequestText_ApiRead";
                ShowCopyRequest = true;
                break;
            case CloudErrorKind.Permission when ex.Error.Code == BudgetErrorCodes.BillingReadPermission:
                // The read of the project's billing link was refused: nothing was saved, and the person to ask is a project owner.
                ErrorText = _strings.GetString("BudgetError_BillingReadPermission");
                break;
            case CloudErrorKind.Permission when removing:
                // The create request would ask for the wrong thing; the budget is removed on Google's own page.
                ErrorText = _strings.GetString("BudgetError_PermissionRemove");
                await OfferConsoleAsync(project, cancellationToken);
                break;
            case CloudErrorKind.Permission:
                ErrorText = _strings.GetString("BudgetError_Permission");
                _requestKey = "BudgetRequestText";
                ShowCopyRequest = true;
                await OfferConsoleAsync(project, cancellationToken);
                break;
            case CloudErrorKind.Billing:
                ErrorText = _strings.GetString(ex.Error.Code == BudgetErrorCodes.BillingDisabled ? "BudgetError_BillingDisabled" : "BudgetError_NoBilling");
                await OfferConsoleAsync(project, cancellationToken);
                break;
            case CloudErrorKind.OrgPolicy:
                ErrorText = _strings.GetString("BudgetError_OrgPolicy");
                _requestKey = "BudgetRequestText_OrgPolicy";
                ShowCopyRequest = true;
                break;
            case CloudErrorKind.ApiDisabled:
                ErrorText = _strings.GetString("BudgetError_ApiDisabled");
                ConsoleLink = new Uri(BudgetLinks.ForApi(project));
                break;
            case CloudErrorKind.Network:
                ErrorText = _strings.GetString("BudgetError_Network");
                break;
            default:
                ErrorText = _strings.GetString("BudgetError_Other");
                break;
        }
    }

    private async Task ReportLookAsync(CloudOperationException ex, string project, CancellationToken cancellationToken)
    {
        switch (ex.Kind)
        {
            case CloudErrorKind.Permission when ex.Error.Code == BudgetErrorCodes.ApiReadPermission:
                // The refused read was of the PROJECT's API state: the person to ask is a project owner, and the request text says so.
                StatusText = _strings.GetString("BudgetStatusApiReadNoAccess");
                _requestKey = "BudgetRequestText_ApiRead";
                ShowCopyRequest = true;
                break;
            case CloudErrorKind.Permission when ex.Error.Code == BudgetErrorCodes.BillingReadPermission:
                // The refused read was of the project's billing link: a project right again, so the billing administrator is the wrong person to ask.
                StatusText = _strings.GetString("BudgetStatusBillingReadNoAccess");
                break;
            case CloudErrorKind.Permission:
                StatusText = _strings.GetString("BudgetStatusNoAccess");
                break;
            case CloudErrorKind.Billing:
                StatusText = _strings.GetString("BudgetStatusBilling");
                await OfferConsoleAsync(project, cancellationToken);
                break;
            case CloudErrorKind.OrgPolicy:
                StatusText = _strings.GetString("BudgetStatusOrgPolicy");
                _requestKey = "BudgetRequestText_OrgPolicy";
                ShowCopyRequest = true;
                break;
            case CloudErrorKind.ApiDisabled:
                StatusText = _strings.GetString("BudgetStatusApiDisabled");
                ConsoleLink = new Uri(BudgetLinks.ForApi(project));
                break;
            case CloudErrorKind.Network:
                StatusText = _strings.GetString("BudgetStatusNetwork");
                break;
            default:
                StatusText = _strings.GetString("BudgetStatusCouldNotCheck");
                break;
        }
    }

    private async Task OfferConsoleAsync(string project, CancellationToken cancellationToken)
    {
        try
        {
            ConsoleLink = new Uri(await _budgets.ConsoleLinkAsync(project, cancellationToken));
        }
        catch (Exception ex) when (ex is CloudOperationException or UriFormatException)
        {
            ConsoleLink = new Uri(BudgetLinks.ForBillingAccount(null));
        }
    }

    private void Show(BudgetInfo? budget, string project)
    {
        _knownProject = project;
        StateKnown = true;
        HasBudget = budget is not null;
        if (budget is null)
        {
            RestoreDefaultAmountIfEmpty();
            StatusText = _strings.GetString("BudgetStatusNone");
            return;
        }

        if (!budget.AmountKnown)
        {
            // A budget that follows last month's spend has no amount to show, and "0 a month" would be wrong. An untouched box is left EMPTY, not at the default:
            // Save would otherwise turn a follow-last-month budget into a fixed default. Save with an empty box asks for an amount (BudgetAmountInvalid) and changes nothing.
            if (!_amountEdited)
            {
                SetAmount(string.Empty);
            }

            // The box is empty here, so "press Create or update" alone would only ask for an amount: this wording says to type one first.
            StatusText = budget.ThresholdPercents.Count == 0
                ? _strings.GetString("BudgetStatusNoThresholdsNoAmount")
                : string.Format(CultureInfo.CurrentCulture, _strings.GetString("BudgetStatusExistsNoAmount"), Thresholds(budget));
            return;
        }

        // What the user typed is theirs: only an untouched box shows what Google holds.
        if (!_amountEdited)
        {
            SetAmount(Format(budget.AmountUsd));
        }

        StatusText = budget.ThresholdPercents.Count == 0
            ? _strings.GetString("BudgetStatusNoThresholds")
            : string.Format(CultureInfo.CurrentCulture, _strings.GetString("BudgetStatusExists"), Format(budget.AmountUsd), Thresholds(budget));
    }

    /// <summary>
    /// The percentages the budget really emails at, as a phrase ("50%", "50% and 90%", "50%, 90% and 100%"). Read from the budget, never assumed.
    /// Every word of it (the percent sign, the joiner, the "and") is resw copy (Hard Rule 13): BudgetThresholdItem, BudgetThresholdsJoiner, BudgetThresholdsAnd.
    /// </summary>
    private string Thresholds(BudgetInfo budget)
    {
        var parts = budget.ThresholdPercents.Select(p => string.Format(CultureInfo.CurrentCulture, _strings.GetString("BudgetThresholdItem"), p.ToString("0.##", CultureInfo.CurrentCulture))).ToList();
        return parts.Count == 1
            ? parts[0]
            : string.Format(CultureInfo.CurrentCulture, _strings.GetString("BudgetThresholdsAnd"), string.Join(_strings.GetString("BudgetThresholdsJoiner"), parts.Take(parts.Count - 1)), parts[^1]);
    }

    // A follow-last-month budget leaves the box empty; once there is no budget the box offers the default again. Typed text is never touched, and a box the user cleared stays cleared.
    private void RestoreDefaultAmountIfEmpty()
    {
        if (!_amountEdited && string.IsNullOrWhiteSpace(AmountText))
        {
            SetAmount(Format(BudgetNaming.DefaultAmountUsd));
        }
    }

    private void SetAmount(string text)
    {
        _settingAmount = true;
        try
        {
            AmountText = text;
        }
        finally
        {
            _settingAmount = false;
        }
    }

    private void ClearMessages()
    {
        ErrorText = string.Empty;
        StatusText = string.Empty;
        ConsoleLink = null;
        ShowCopyRequest = false;
    }

    private string? ProjectId() => _account.SelectedProjectId is { Length: > 0 } id ? id : null;

    private static string Format(decimal amount) => amount.ToString("0.##", CultureInfo.CurrentCulture);
}
