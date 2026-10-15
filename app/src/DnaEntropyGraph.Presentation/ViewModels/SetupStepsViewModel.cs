using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Cloud;
using DnaEntropyGraph.Core.Setup;
using DnaEntropyGraph.Presentation.Services;

namespace DnaEntropyGraph.Presentation.ViewModels;

/// <summary>One billing account the user can pick, with the command that links it (a command on the item, so the list needs no code-behind).</summary>
public sealed record BillingAccountChoice(string AccountId, string DisplayName, IAsyncRelayCommand ChooseCommand);

/// <summary>
/// Issue #99: the OK/Fix rows of first-run wizard steps 4 to 7 (billing, turn on services, storage and identity, GPU allowance), which
/// are also the health report ("Run setup check again" in Settings runs exactly these). They run in the documented preflight order and
/// stop at the first red blocking row; the GPU allowance is last and may be skipped for now. The project is the one the account has chosen.
/// </summary>
public sealed partial class SetupStepsViewModel : ObservableObject
{
    private readonly ISetupCheckService _service;
    private readonly IGcpAccount _account;
    private readonly IShellLauncher _shell;
    private readonly IStringResourceProvider _strings;

    public SetupStepsViewModel(ISetupCheckService service, IGcpAccount account, IShellLauncher shell, IStringResourceProvider strings)
    {
        _service = service;
        _account = account;
        _shell = shell;
        _strings = strings;
        Rows =
        [
            Row(SetupStep.Billing, "Billing"),
            Row(SetupStep.Services, "Services"),
            Row(SetupStep.Storage, "Storage"),
            Row(SetupStep.Quota, "Quota"),
        ];
    }

    public IReadOnlyList<SetupStepRow> Rows { get; }

    public System.Collections.ObjectModel.ObservableCollection<BillingAccountChoice> BillingChoices { get; } = [];

    public bool HasBillingChoices => BillingChoices.Count > 0;

    /// <summary>A line under the rows: "Copied" or why the clipboard would not take the text (with the action).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNotice))]
    private string _noticeText = string.Empty;

    public bool HasNotice => NoticeText.Length > 0;

    /// <summary>Every row is green or skipped: the wizard may finish.</summary>
    public bool IsAllDone => Rows.All(r => r.Status is SetupStatus.Ok or SetupStatus.Skipped);

    private SetupStepRow Row(SetupStep step, string key)
    {
        var row = new SetupStepRow(step, key, _strings);
        row.ActionCommand = new AsyncRelayCommand(ct => PerformActionAsync(row, ct));
        row.RecheckCommand = new AsyncRelayCommand(ct => RunFromAsync(row.Step, ct));
        row.SkipCommand = new AsyncRelayCommand(() =>
        {
            row.MarkSkipped();
            RefreshDone();
            return Task.CompletedTask;
        });
        return row;
    }

    /// <summary>Checks every step from the first, in order.</summary>
    [RelayCommand]
    private Task RunAllAsync(CancellationToken cancellationToken) => RunFromAsync(SetupStep.Billing, cancellationToken);

    private async Task RunFromAsync(SetupStep first, CancellationToken cancellationToken)
    {
        NoticeText = string.Empty;
        BillingChoices.Clear();
        OnPropertyChanged(nameof(HasBillingChoices));
        if (_account.SelectedProjectId is not { Length: > 0 } project)
        {
            foreach (var row in Rows.Where(r => r.Step >= first))
            {
                row.MarkPending();
            }

            RefreshDone();
            return;
        }

        foreach (var row in Rows.Where(r => r.Step >= first))
        {
            row.MarkPending();
        }

        foreach (var row in Rows.Where(r => r.Step >= first))
        {
            row.MarkRunning();
            var result = await RunStepAsync(row.Step, project, cancellationToken);
            row.Apply(result, project);
            if (row.Step == SetupStep.Billing)
            {
                ShowBillingChoices(result);
            }

            if (result.Status != SetupStatus.Ok)
            {
                break;
            }
        }

        RefreshDone();
    }

    private Task<SetupStepResult> RunStepAsync(SetupStep step, string project, CancellationToken cancellationToken) => step switch
    {
        SetupStep.Billing => _service.CheckBillingAsync(project, cancellationToken),
        SetupStep.Services => _service.EnableServicesAsync(project, cancellationToken),
        SetupStep.Storage => _service.EnsureStorageAsync(project, cancellationToken),
        _ => _service.CheckQuotaAsync(project, cancellationToken),
    };

    private void ShowBillingChoices(SetupStepResult result)
    {
        BillingChoices.Clear();
        foreach (var account in result.Accounts.Where(_ => result.Action.Kind == SetupActionKind.PickBillingAccount))
        {
            BillingChoices.Add(new BillingAccountChoice(account.AccountId, account.DisplayName, new AsyncRelayCommand(ct => LinkAsync(account.AccountId, ct))));
        }

        OnPropertyChanged(nameof(HasBillingChoices));
    }

    private async Task LinkAsync(string accountId, CancellationToken cancellationToken)
    {
        if (_account.SelectedProjectId is not { Length: > 0 } project)
        {
            return;
        }

        var row = Rows[0];
        row.MarkRunning();
        var result = await _service.LinkBillingAsync(project, accountId, cancellationToken);
        row.Apply(result, project);
        ShowBillingChoices(result);
        if (result.Status == SetupStatus.Ok)
        {
            await RunFromAsync(SetupStep.Services, cancellationToken);
        }

        RefreshDone();
    }

    private async Task PerformActionAsync(SetupStepRow row, CancellationToken cancellationToken)
    {
        switch (row.ActionKind)
        {
            case SetupActionKind.CopyText:
                NoticeText = _strings.GetString(_shell.CopyText(row.RequestText) ? "WizardCopied" : "WizardCopyFailed");
                break;
            case SetupActionKind.Retry:
                await RunFromAsync(row.Step, cancellationToken);
                break;
            default:
                // OpenLink is a hyperlink button on the page, and PickBillingAccount is the list under the row; a project problem is the
                // project step above (the wizard), so there is nothing else to run here.
                break;
        }
    }

    private void RefreshDone() => OnPropertyChanged(nameof(IsAllDone));
}
