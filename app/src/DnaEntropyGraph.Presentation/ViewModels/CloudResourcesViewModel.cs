using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Cloud;
using DnaEntropyGraph.Core.Cost;
using DnaEntropyGraph.Presentation.Services;

namespace DnaEntropyGraph.Presentation.ViewModels;

/// <summary>
/// The Cloud page (issue #105, docs/superpowers/specs Appendix A section 2.7): every computer, disk and job folder this installation has in the project, found by label,
/// with what it costs, a Stop / Start / Delete per computer, and "Delete everything" with a confirm that names what will go and a per-item report afterwards.
/// Computers that another installation labelled for this app in the same project are listed read-only (orchestrator DECISION, reversible: Hard Rule 9).
/// Every string is a Resources.resw key (Hard Rule 13); every failure names one action.
/// </summary>
public sealed partial class CloudResourcesViewModel : ObservableObject
{
    private readonly IGcpAccount _account;
    private readonly IDialogService _dialogs;
    private readonly IStringResourceProvider _strings;
    private readonly IComputeGateway _compute;
    private readonly InstallationCleanup _cleanup;
    private readonly ISettingsStore _settings;
    private readonly IPricingSource? _pricing;
    private readonly IDispatcher? _dispatcher;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string _errorText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBanner))]
    private string _bannerText = string.Empty;

    [ObservableProperty]
    private string _summaryText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasReport))]
    private string _reportHeadline = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    /// <summary>True once a listing has succeeded and found nothing: the page then says so. An error or a first load is not "empty".</summary>
    [ObservableProperty]
    private bool _isEmpty;

    public CloudResourcesViewModel(IGcpAccount account, IDialogService dialogs, IStringResourceProvider strings, IComputeGateway compute, InstallationCleanup cleanup, ISettingsStore settings, IPricingSource? pricing = null, IDispatcher? dispatcher = null)
    {
        _account = account;
        _dialogs = dialogs;
        _strings = strings;
        _compute = compute;
        _cleanup = cleanup;
        _settings = settings;
        _pricing = pricing;
        _dispatcher = dispatcher;
        // Issue #828: a project switch while this page is open must not leave the old project's rows on screen with live buttons.
        _lastRaisedProject = account.SelectedProjectId;
        _account.AccountChanged += OnAccountChangedEvent;
    }

    /// <summary>
    /// Ends the subscription to the account's AccountChanged event. The account is a singleton and this view model is transient, so without it every visit to the Cloud page
    /// would leave one more view model reachable and reacting (#828). The page calls it when it is navigated away from.
    /// </summary>
    public void Detach() => _account.AccountChanged -= OnAccountChangedEvent;

    /// <summary>
    /// Runs on the thread that raised the event, NOT the UI queue: the project is read and the generation counted here, because the queued handler may run long after
    /// (A to B to A before either runs would otherwise count nothing), and a listing started after the switch must not see the queued handler as a change during it.
    /// </summary>
    private void OnAccountChangedEvent(object? sender, EventArgs e)
    {
        var project = _account.SelectedProjectId;
        lock (_generationGate)
        {
            if (!string.Equals(project, _lastRaisedProject, StringComparison.Ordinal))
            {
                _lastRaisedProject = project;
                Interlocked.Increment(ref _projectGeneration);
            }
        }

        OnUiThread(() => OnAccountChanged(project));
    }

    /// <summary>The project the rows on screen were listed for; null while nothing is listed (first load in flight, last listing discarded). Every action compares it with the project chosen NOW (#828).</summary>
    private string? _listedProject;

    /// <summary>A listing in flight: the project it was started for and the token its gateway calls observe, so a switch can cancel it (and only it) when it goes stale.</summary>
    private sealed record InFlightListing(string Project, CancellationTokenSource Cts);

    private readonly List<InFlightListing> _inFlight = [];

    /// <summary>Counts every real change of the chosen project (A to B, and B back to A, are two). A listing compares it with the value it started at, because the gateway reads the project at call time and "same id at the end" does not mean "same project all along".</summary>
    private int _projectGeneration;

    private readonly object _generationGate = new();

    private string? _lastRaisedProject;

    /// <summary>The queued half of an account change; <paramref name="project"/> is the project chosen when the event was raised.</summary>
    private void OnAccountChanged(string? project)
    {
        // Every in-flight listing for another project is stale: cancel it so Refresh is available again. The same project (token refresh, sign-in) leaves it alone.
        foreach (var listing in _inFlight.ToArray())
        {
            if (!string.Equals(project, listing.Project, StringComparison.Ordinal))
            {
                try
                {
                    listing.Cts.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // The listing finished in the meantime: nothing to cancel.
                }
            }
        }

        // Nothing was listed, so there is nothing stale to clear and nothing to announce.
        if (_listedProject is null || string.Equals(project, _listedProject, StringComparison.Ordinal))
        {
            return;
        }

        ForgetListing();
        // No network from here: this view model is transient and the event is the account's, so a call per abandoned page would be waste. Refresh lists the new project.
        ErrorText = Format("CloudPage_ProjectChanged");
    }

    /// <summary>Everything on screen that belongs to the listing of one project, forgotten together: rows, summary, running banner, empty state.</summary>
    private void ForgetListing()
    {
        Vms.Clear();
        Disks.Clear();
        _listedProject = null;
        IsEmpty = false;
        SummaryText = string.Empty;
        BannerText = string.Empty;
    }
    private void OnUiThread(Action action)
    {
        if (_dispatcher is null)
        {
            action();
            return;
        }

        _dispatcher.Enqueue(action);
    }

    /// <summary>The gateway has no project parameter (it reads the chosen project per call), so an action is only safe while the project it was listed for is still the chosen one.</summary>
    private bool StillListedProject(string listedProject)
    {
        if (string.Equals(_account.SelectedProjectId, listedProject, StringComparison.Ordinal))
        {
            return true;
        }

        ErrorText = Format("CloudPage_ProjectChanged");
        return false;
    }

    public ObservableCollection<CloudVmRow> Vms { get; } = [];

    public ObservableCollection<CloudDiskRow> Disks { get; } = [];

    /// <summary>One line per item of the last "Delete everything", each with its outcome.</summary>
    public ObservableCollection<string> ReportLines { get; } = [];

    public bool HasError => ErrorText.Length > 0;

    public bool HasBanner => BannerText.Length > 0;

    public bool HasReport => ReportHeadline.Length > 0;

    private string Format(string key, params object[] args)
        => args.Length == 0 ? _strings.GetString(key) : string.Format(CultureInfo.CurrentCulture, _strings.GetString(key), args);

    /// <summary>Lists everything again from Google (the page never trusts what it showed a minute ago).</summary>
    [RelayCommand]
    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        if (_account.SelectedScope() is not { ProjectId.Length: > 0 } scope)
        {
            ErrorText = Format("CloudPage_NoProject");
            return;
        }

        var project = scope.ProjectId;

        IsBusy = true;
        var generation = Volatile.Read(ref _projectGeneration);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var listing = new InFlightListing(project, cts);
        _inFlight.Add(listing);
        try
        {
            var installation = InstallationId.GetOrCreate(_settings);
            var inventory = await _cleanup.InventoryAsync(scope, installation, cts.Token);
            var everyone = await _compute.ListAppVmsAsync(scope, cts.Token);
            var pricing = LoadPricing();

            // The project changed while Google was answering: these lists belong to the old one. Show nothing of them and say why here (the account-changed
            // handler stays quiet when no listing was on screen); Refresh lists the new project.
            // Also when it changed and changed back (the generation moved): the gateway reads the chosen project per call, so the answer may be for the other one.
            if (generation != Volatile.Read(ref _projectGeneration) || !string.Equals(_account.SelectedProjectId, project, StringComparison.Ordinal))
            {
                ForgetListing();
                ErrorText = Format("CloudPage_ProjectChanged");
                return;
            }

            _listedProject = project;
            Vms.Clear();
            foreach (var vm in inventory.Vms)
            {
                Vms.Add(OwnRow(vm, inventory.Disks, pricing, scope));
            }

            var ownNames = inventory.Vms.Select(v => (v.Name, v.Zone)).ToHashSet();
            foreach (var vm in everyone.Where(v => !ownNames.Contains((v.Name, v.Zone)) && !Owns(v, installation)))
            {
                Vms.Add(new CloudVmRow(vm.Name, vm.Zone, StatusOf(vm), string.Empty, Format("CloudVm_AnotherComputer"), isAnotherComputer: true));
            }

            Disks.Clear();
            foreach (var disk in inventory.Disks)
            {
                Disks.Add(new CloudDiskRow(
                    disk.Name,
                    disk.Zone,
                    Format(disk.Users.Count > 0 ? "CloudDisk_Attached" : "CloudDisk_Detached", disk.SizeGb),
                    pricing is null ? string.Empty : Format("CloudDisk_Cost", Money(pricing.DiskUsdPerDay(disk.SizeGb)))));
            }

            SummaryText = inventory.Bucket is { } bucket
                ? Format("CloudPage_Summary", bucket.JobCount, Size(bucket.TotalBytes), bucket.BucketName, Size(inventory.SharedCache.TotalBytes))
                : Format("CloudPage_SummaryNoBucket", Size(inventory.SharedCache.TotalBytes));
            var running = inventory.Vms.Count(v => IsRunning(v));
            BannerText = running > 0 ? Format("CloudPage_RunningBanner", running) : string.Empty;
            IsEmpty = Vms.Count == 0 && Disks.Count == 0 && inventory.Bucket is not { JobCount: > 0 };
            ErrorText = string.Empty;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Any failure, not only Google's: the page is opened fire-and-forget, so an exception left here would vanish and leave a blank page (#740).
            LogFailure("listing the project", ex);
            ErrorText = Format("CloudPage_ListFailed");
        }
        finally
        {
            _inFlight.Remove(listing);
            IsBusy = false;
        }
    }

    /// <summary>"Delete everything this app created": asks first with the list of what will go, deletes, then reports every item.</summary>
    [RelayCommand]
    private async Task DeleteEverythingAsync(CancellationToken cancellationToken)
    {
        if (_account.SelectedScope() is not { ProjectId.Length: > 0 } scope)
        {
            ErrorText = Format("CloudPage_NoProject");
            return;
        }

        var project = scope.ProjectId;

        // The last delete's report belongs to that delete: never leave it under a new error or a declined dialog (#740).
        ReportLines.Clear();
        ReportHeadline = string.Empty;
        var attempted = false;
        var pendingError = string.Empty;
        IsBusy = true;
        try
        {
            var installation = InstallationId.GetOrCreate(_settings);
            InstallationInventory inventory;
            try
            {
                inventory = await _cleanup.InventoryAsync(scope, installation, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogFailure("listing before delete everything", ex);
                ErrorText = Format("CloudPage_ListFailed");
                return;
            }

            var jobs = inventory.Bucket?.JobCount ?? 0;
            var size = Size(inventory.Bucket?.TotalBytes ?? 0);
            var confirmed = await _dialogs.ConfirmAsync(Format("CloudDeleteAll_Title"), Format("CloudDeleteAll_Body", inventory.Vms.Count, inventory.Disks.Count, jobs, size), cancellationToken);
            if (!confirmed)
            {
                return;
            }

            if (!StillListedProject(project))
            {
                return;
            }

            CleanupReport report;
            try
            {
                attempted = true;
                // Only what the dialog named: a computer, disk or folder that appeared meanwhile is left alone and reported (#740).
                report = await _cleanup.DeleteAllAsync(scope, installation, cancellationToken, inventory);
            }
            catch (CloudOperationException)
            {
                // The sweep contains every per-item failure and reports it (InstallationCleanup), so a cloud error reaches here only from the listings that come before the first delete.
                pendingError = Format("CloudPage_DeleteAllFailed");
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Anything else is not something the sweep promised to contain, so this message does not claim nothing was deleted.
                LogFailure("delete everything", ex);
                pendingError = Format("CloudPage_DeleteAllInterrupted");
                return;
            }

            foreach (var item in report.Items)
            {
                ReportLines.Add(Line(item));
            }

            ReportHeadline = Format(report.Complete ? "CloudReport_Complete" : "CloudReport_Incomplete");
            ErrorText = string.Empty;
        }
        finally
        {
            IsBusy = false;
            if (attempted)
            {
                // Whatever ended the sweep (a report, an error, the user's cancel), something may be gone: list again so the page never shows a deleted item. The cancelled token is not
                // used for that read. A failed listing says so by itself; an error from the delete is put back after it.
                await RefreshAsync(CancellationToken.None);
                if (pendingError.Length > 0)
                {
                    ErrorText = pendingError;
                }
            }
        }
    }

    private string Line(CleanupItem item)
    {
        var what = Format(item.Kind switch
        {
            CleanupKind.Vm => "CloudKind_Vm",
            CleanupKind.Disk => "CloudKind_Disk",
            _ => "CloudKind_JobData",
        }) + " " + item.Name;
        return item.Outcome switch
        {
            CleanupOutcome.Deleted when item.RecoverableForDays is { } days => Format("CloudReport_DeletedRecoverable", what, days),
            CleanupOutcome.Deleted => Format("CloudReport_Deleted", what),
            CleanupOutcome.Failed => Format("CloudReport_Failed", what),
            CleanupOutcome.NotVerified => Format("CloudReport_NotVerified", what),
            _ => Format("CloudReport_Skipped", what),
        };
    }

    private CloudVmRow OwnRow(VmDescriptor vm, IReadOnlyList<DiskDescriptor> disks, PricingTable? pricing, CloudScope listedScope)
    {
        var listedProject = listedScope.ProjectId;
        var running = IsRunning(vm);
        var stopped = !running && IsStopped(vm);
        var cost = string.Empty;
        if (pricing is not null)
        {
            var attached = disks.Where(d => d.Users.Any(u => u == vm.Name || u.EndsWith("/" + vm.Name, StringComparison.Ordinal))).Sum(d => (double)d.SizeGb);
            var machine = vm.MachineType is { Length: > 0 } type ? pricing.Find(type[(type.LastIndexOf('/') + 1)..]) : null;
            if (running && machine is not null)
            {
                cost = Format("CloudVm_CostRunning", Money(machine.OnDemandUsdPerHour + pricing.DiskUsdPerHour(attached)));
            }
            else if (stopped)
            {
                cost = Format("CloudVm_CostStopped", Money(pricing.DiskUsdPerDay(attached)));
            }
        }

        return new CloudVmRow(vm.Name, vm.Zone, StatusOf(vm), cost, string.Empty, isAnotherComputer: false)
        {
            StopCommand = new AsyncRelayCommand(ct => ActAsync(vm, listedProject, () => _compute.StopVmAsync(listedScope, vm.Name, vm.Zone, ct), ct), () => running),
            StartCommand = new AsyncRelayCommand(ct => ActAsync(vm, listedProject, () => _compute.StartVmAsync(listedScope, vm.Name, vm.Zone, ct), ct), () => stopped),
            DeleteCommand = new AsyncRelayCommand(ct => DeleteOneAsync(vm, listedScope, ct)),
        };
    }

    private async Task DeleteOneAsync(VmDescriptor vm, CloudScope listedScope, CancellationToken cancellationToken)
    {
        var listedProject = listedScope.ProjectId;
        if (!StillListedProject(listedProject))
        {
            return;
        }

        if (!await _dialogs.ConfirmAsync(Format("CloudDeleteVm_Title"), Format("CloudDeleteVm_Body", vm.Name), cancellationToken))
        {
            return;
        }

        await ActAsync(vm, listedProject, () => _compute.DeleteVmAsync(listedScope, vm.Name, vm.Zone, cancellationToken), cancellationToken);
    }

    /// <summary>One button's call, then a fresh listing so the row shows what Google says now. A refusal names the computer and the action.</summary>
    private async Task ActAsync(VmDescriptor vm, string listedProject, Func<Task> call, CancellationToken cancellationToken)
    {
        // Checked again here as well as before the confirm: the project can change while the confirm is open.
        if (!StillListedProject(listedProject))
        {
            return;
        }

        try
        {
            await call();
            ErrorText = string.Empty;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogFailure("a computer action", ex);
            ErrorText = Format("CloudPage_ActionFailed", vm.Name);
            return;
        }

        await RefreshAsync(cancellationToken);
    }

    /// <summary>The error class only, never a message (it can carry a project or file name).</summary>
    private static void LogFailure(string what, Exception ex)
        => System.Diagnostics.Trace.TraceWarning($"cloud page failed while {what}: {ex.GetType().Name}");

    private PricingTable? LoadPricing()
    {
        try
        {
            return _pricing?.Load().Table;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool Owns(VmDescriptor vm, string installation)
        => vm.Labels is { } labels && labels.TryGetValue("installation-id", out var owner) && owner == installation;

    private static bool IsRunning(VmDescriptor vm) => vm.Status is "RUNNING" or "STAGING" or "PROVISIONING";

    private static bool IsStopped(VmDescriptor vm) => vm.Status is "TERMINATED" or "STOPPED" or "SUSPENDED";

    private string StatusOf(VmDescriptor vm) => Format(IsRunning(vm) ? "CloudVm_Running" : IsStopped(vm) ? "CloudVm_Stopped" : "CloudVm_Changing");

    private static string Money(double usd) => usd.ToString("F2", CultureInfo.InvariantCulture);

    /// <summary>A byte count in the largest unit that fits; the unit words live in Resources.resw (Hard Rule 13), the number is culture-invariant.</summary>
    private string Size(long bytes) => bytes switch
    {
        >= 1_000_000_000 => Format("CloudSize_GB", (bytes / 1_000_000_000.0).ToString("F1", CultureInfo.InvariantCulture)),
        >= 1_000_000 => Format("CloudSize_MB", (bytes / 1_000_000.0).ToString("F1", CultureInfo.InvariantCulture)),
        >= 1_000 => Format("CloudSize_KB", (bytes / 1_000.0).ToString("F0", CultureInfo.InvariantCulture)),
        _ => Format("CloudSize_B", bytes.ToString(CultureInfo.InvariantCulture)),
    };
}
