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
/// Issue #99: the OK/Fix rows of first-run wizard steps 4 to 7 (billing, turn on services, GPU allowance, storage and identity), which
/// are also the health report ("Run setup check again" in Settings runs exactly these). They run in the documented preflight order
/// (CLAUDE.md: billing, Compute API, GPU quota, then the bucket) and stop at the first red row; the GPU allowance may be skipped for now
/// when it was read and is zero, and the chain then carries on to storage. The project is the one the account has chosen.
/// Every run, re-check and link goes through one gate, so two never run side by side and <see cref="IsBusy"/> is true for all of them.
/// </summary>
public sealed partial class SetupStepsViewModel : ObservableObject
{
    private readonly ISetupCheckService _service;
    private readonly IGcpAccount _account;
    private readonly IShellLauncher _shell;
    private readonly IStringResourceProvider _strings;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>The region the GPU is asked for: the region of the zone ladder's defaults (the run starts there when nothing else is chosen): one source of truth, <see cref="CloudJobRequestFactory.DefaultRegion"/>.</summary>
    public static string GpuAskRegion => CloudJobRequestFactory.DefaultRegion;

    private readonly GpuQuotaRequester? _gpuRequester;

    public SetupStepsViewModel(ISetupCheckService service, IGcpAccount account, IShellLauncher shell, IStringResourceProvider strings, GpuQuotaRequester? gpuRequester = null)
    {
        _service = service;
        _account = account;
        _shell = shell;
        _strings = strings;
        _gpuRequester = gpuRequester;
        Rows =
        [
            Row(SetupStep.Billing),
            Row(SetupStep.Services),
            Row(SetupStep.Quota),
            Row(SetupStep.Storage),
        ];
    }

    public IReadOnlyList<SetupStepRow> Rows { get; }

    /// <summary>
    /// What the wizard does when a row's one button is <see cref="SetupActionKind.CreateProject"/> or <see cref="SetupActionKind.PickProject"/>
    /// (a project problem is the wizard's project step, not a row's). Set by the wizard that shows these rows.
    /// </summary>
    public Func<SetupActionKind, CancellationToken, Task>? ProjectActionHandler { get; set; }

    public System.Collections.ObjectModel.ObservableCollection<BillingAccountChoice> BillingChoices { get; } = [];

    public bool HasBillingChoices => BillingChoices.Count > 0;

    /// <summary>A line under the rows: "Copied" or why the clipboard would not take the text (with the action).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNotice))]
    private string _noticeText = string.Empty;

    public bool HasNotice => NoticeText.Length > 0;

    /// <summary>A check is in flight (a run, a re-check or a link): the wizard may not finish and the row buttons wait.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAllDone))]
    [NotifyCanExecuteChangedFor(nameof(RunAllCommand))]
    private bool _isBusy;

    /// <summary>Every row is green or skipped and nothing is being checked: the wizard may finish.</summary>
    public bool IsAllDone => !IsBusy && Rows.All(r => r.Status is SetupStatus.Ok or SetupStatus.Skipped);

    partial void OnIsBusyChanged(bool value)
    {
        foreach (var row in Rows)
        {
            row.ActionCommand?.NotifyCanExecuteChanged();
            row.RecheckCommand?.NotifyCanExecuteChanged();
            row.SkipCommand?.NotifyCanExecuteChanged();
            row.AskCommand?.NotifyCanExecuteChanged();
            row.AskActionCommand?.NotifyCanExecuteChanged();
        }
    }

    private SetupStepRow Row(SetupStep step)
    {
        var row = new SetupStepRow(step, _strings);
        row.ActionCommand = new AsyncRelayCommand(ct => PerformActionAsync(row, ct), () => !IsBusy);
        row.RecheckCommand = new AsyncRelayCommand(ct => RunFromAsync(row.Step, ct), () => !IsBusy);
        row.SkipCommand = new AsyncRelayCommand(ct => SkipAsync(row, ct), () => !IsBusy);
        if (step == SetupStep.Quota && _gpuRequester is not null)
        {
            row.AskCommand = new AsyncRelayCommand(ct => AskGoogleAsync(row, ct), () => !IsBusy);
        }

        return row;
    }

    /// <summary>
    /// "Ask Google for a GPU": runs <see cref="GpuQuotaRequester"/> for the chosen project and turns its report into one message and one action on the row.
    /// A grant at once checks the step again (so the row turns green from what Google now says, not from our word).
    /// </summary>
    private Task AskGoogleAsync(SetupStepRow row, CancellationToken cancellationToken) => ExclusiveAsync(cancellationToken, async () =>
    {
        if (_gpuRequester is null || _account.SelectedProjectId is not { Length: > 0 } project)
        {
            return;
        }

        GpuQuotaRequestReport report;
        try
        {
            report = await _gpuRequester.RequestAsync(project, GpuAskRegion, _account.CurrentAccount?.Email, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            ShowTryAgain(row);
            return;
        }

        if (report.Sufficient)
        {
            await RunFromCoreAsync(SetupStep.Quota, cancellationToken);
            return;
        }

        var failed = report.Items.FirstOrDefault(i => i.Outcome == QuotaRequestOutcome.Failed);
        var refused = report.Items.FirstOrDefault(i => i.Outcome == QuotaRequestOutcome.NotEligible);
        var quotaPage = new Uri(QuotaLinks.ForProject(project));
        var recheck = new AsyncRelayCommand(ct => RunFromAsync(SetupStep.Quota, ct), () => !IsBusy);
        if (failed is not null)
        {
            if (failed.ErrorKind is CloudErrorKind.Permission or CloudErrorKind.OrgPolicy or CloudErrorKind.ApiDisabled)
            {
                row.ShowAskResult("WizardGpuAsk_Permission", "WizardGpuAsk_OpenQuotaPage", null, quotaPage);
            }
            else
            {
                ShowTryAgain(row);
            }
        }
        else if (refused is not null)
        {
            switch (refused.Ineligibility)
            {
                case QuotaIneligibilityReason.NoValidBillingAccount:
                    row.ShowAskResult("WizardGpuAsk_NoBilling", "WizardGpuAsk_CheckBilling", new AsyncRelayCommand(ct => RunFromAsync(SetupStep.Billing, ct), () => !IsBusy), null);
                    break;
                case QuotaIneligibilityReason.NotEnoughUsageHistory:
                    row.ShowAskResult("WizardGpuAsk_NoHistory", "WizardGpuAsk_OpenQuotaPage", null, quotaPage);
                    break;
                default:
                    row.ShowAskResult("WizardGpuAsk_NotEligible", "WizardGpuAsk_OpenQuotaPage", null, quotaPage);
                    break;
            }
        }
        else
        {
            row.ShowAskResult("WizardGpuAsk_Requested", "WizardGpuAsk_CheckAgain", recheck, null);
        }
    });

    private void ShowTryAgain(SetupStepRow row)
        => row.ShowAskResult("WizardGpuAsk_Failed", "WizardGpuAsk_TryAgain", row.AskCommand, null);

    /// <summary>Checks every step from the first, in order.</summary>
    [RelayCommand(CanExecute = nameof(CanRunAll))]
    public Task RunAllAsync(CancellationToken cancellationToken) => RunFromAsync(SetupStep.Billing, cancellationToken);

    private bool CanRunAll() => !IsBusy;

    private async Task SkipAsync(SetupStepRow row, CancellationToken cancellationToken)
    {
        // Only a row that offers it: a permission, billing or API failure must be fixed, not skipped, and Finish never opens over one.
        if (!row.HasSkip)
        {
            return;
        }

        row.MarkSkipped();
        RefreshDone();
        var index = Rows.ToList().IndexOf(row);
        if (index + 1 < Rows.Count)
        {
            await RunFromAsync(Rows[index + 1].Step, cancellationToken);
        }
    }

    /// <summary>Takes the gate, runs, and always gives the gate back and leaves no row on "Checking...".</summary>
    private async Task ExclusiveAsync(CancellationToken cancellationToken, Func<Task> work)
    {
        await _gate.WaitAsync(cancellationToken);
        IsBusy = true;
        try
        {
            await work();
        }
        finally
        {
            foreach (var row in Rows.Where(r => r.IsRunning))
            {
                row.MarkPending();
            }

            IsBusy = false;
            _gate.Release();
            RefreshDone();
        }
    }

    private Task RunFromAsync(SetupStep first, CancellationToken cancellationToken)
        => ExclusiveAsync(cancellationToken, () => RunFromCoreAsync(first, cancellationToken));

    private async Task RunFromCoreAsync(SetupStep first, CancellationToken cancellationToken)
    {
        NoticeText = string.Empty;
        BillingChoices.Clear();
        OnPropertyChanged(nameof(HasBillingChoices));
        var rows = Rows.SkipWhile(r => r.Step != first).ToList();
        foreach (var row in rows)
        {
            row.MarkPending();
        }

        RefreshDone();
        if (_account.SelectedProjectId is not { Length: > 0 } project)
        {
            return;
        }

        foreach (var row in rows)
        {
            row.MarkRunning();
            var result = await GuardedAsync(row.Step, () => RunStepAsync(row.Step, project, cancellationToken));
            row.Apply(result, project);
            if (row.Step == SetupStep.Billing)
            {
                ShowBillingChoices(result);
            }

            RefreshDone();
            if (result.Status != SetupStatus.Ok)
            {
                break;
            }
        }
    }

    private Task<SetupStepResult> RunStepAsync(SetupStep step, string project, CancellationToken cancellationToken) => step switch
    {
        SetupStep.Billing => _service.CheckBillingAsync(project, cancellationToken),
        SetupStep.Services => _service.EnableServicesAsync(project, cancellationToken),
        SetupStep.Quota => _service.CheckQuotaAsync(project, cancellationToken),
        _ => _service.EnsureStorageAsync(project, cancellationToken),
    };

    /// <summary>
    /// The service turns a Google failure into a result, but anything else (a bug, a broken file) would escape and leave the row on
    /// "Checking..." forever. It becomes the row's catch-all failure with "Try again". Cancellation still propagates.
    /// </summary>
    private static async Task<SetupStepResult> GuardedAsync(SetupStep step, Func<Task<SetupStepResult>> call)
    {
        try
        {
            return await call();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return new SetupStepResult(step, SetupStatus.NeedsAction) { Action = new SetupAction(SetupActionKind.Retry, "SetupAction_TryAgain") };
        }
    }

    private void ShowBillingChoices(SetupStepResult result)
    {
        BillingChoices.Clear();
        foreach (var account in result.Accounts.Where(_ => result.Action.Kind == SetupActionKind.PickBillingAccount))
        {
            BillingChoices.Add(new BillingAccountChoice(account.AccountId, account.DisplayName, new AsyncRelayCommand(ct => LinkAsync(account.AccountId, ct))));
        }

        OnPropertyChanged(nameof(HasBillingChoices));
    }

    private Task LinkAsync(string accountId, CancellationToken cancellationToken) => ExclusiveAsync(cancellationToken, async () =>
    {
        if (_account.SelectedProjectId is not { Length: > 0 } project)
        {
            return;
        }

        var row = Rows.First(r => r.Step == SetupStep.Billing);
        row.MarkRunning();
        var result = await GuardedAsync(SetupStep.Billing, () => _service.LinkBillingAsync(project, accountId, cancellationToken));
        row.Apply(result, project);
        ShowBillingChoices(result);
        if (result.Status == SetupStatus.Ok)
        {
            await RunFromCoreAsync(SetupStep.Services, cancellationToken);
        }
    });

    private async Task PerformActionAsync(SetupStepRow row, CancellationToken cancellationToken)
    {
        switch (row.ActionKind)
        {
            case SetupActionKind.CopyText:
                NoticeText = _strings.GetString(_shell.CopyText(row.RequestText) ? "WizardCopied" : "WizardCopyFailed");
                break;
            case SetupActionKind.CreateProject:
            case SetupActionKind.PickProject:
                // A project problem is the wizard's project step above these rows: it makes the project or shows the list.
                if (ProjectActionHandler is { } handler)
                {
                    await handler(row.ActionKind, cancellationToken);
                }

                break;
            case SetupActionKind.None:
            case SetupActionKind.PickBillingAccount:
                // No button is shown for these (the list under the billing row is the action), so there is nothing to run.
                break;
            default:
                // Retry, Continue, and an OpenLink that somehow reached the command: check the step (and the ones after it) again.
                await RunFromAsync(row.Step, cancellationToken);
                break;
        }
    }

    private void RefreshDone() => OnPropertyChanged(nameof(IsAllDone));
}
