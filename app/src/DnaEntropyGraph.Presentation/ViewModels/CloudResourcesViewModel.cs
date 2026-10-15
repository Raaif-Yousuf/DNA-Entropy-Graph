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

    public CloudResourcesViewModel(IGcpAccount account, IDialogService dialogs, IStringResourceProvider strings, IComputeGateway compute, InstallationCleanup cleanup, ISettingsStore settings, IPricingSource? pricing = null)
    {
        _account = account;
        _dialogs = dialogs;
        _strings = strings;
        _compute = compute;
        _cleanup = cleanup;
        _settings = settings;
        _pricing = pricing;
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
        if (_account.SelectedProjectId is not { Length: > 0 } project)
        {
            ErrorText = Format("CloudPage_NoProject");
            return;
        }

        IsBusy = true;
        try
        {
            var installation = InstallationId.GetOrCreate(_settings);
            var inventory = await _cleanup.InventoryAsync(project, installation, cancellationToken);
            var everyone = await _compute.ListAppVmsAsync(cancellationToken);
            var pricing = LoadPricing();

            Vms.Clear();
            foreach (var vm in inventory.Vms)
            {
                Vms.Add(OwnRow(vm, inventory.Disks, pricing));
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
                    Format(disk.Users.Count > 0 ? "CloudDisk_Attached" : "CloudDisk_Detached", disk.SizeGb, disk.Status),
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
        catch (CloudOperationException)
        {
            ErrorText = Format("CloudPage_ListFailed");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>"Delete everything this app created": asks first with the list of what will go, deletes, then reports every item.</summary>
    [RelayCommand]
    private async Task DeleteEverythingAsync(CancellationToken cancellationToken)
    {
        if (_account.SelectedProjectId is not { Length: > 0 } project)
        {
            ErrorText = Format("CloudPage_NoProject");
            return;
        }

        IsBusy = true;
        try
        {
            var installation = InstallationId.GetOrCreate(_settings);
            InstallationInventory inventory;
            try
            {
                inventory = await _cleanup.InventoryAsync(project, installation, cancellationToken);
            }
            catch (CloudOperationException)
            {
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

            CleanupReport report;
            try
            {
                report = await _cleanup.DeleteAllAsync(project, installation, cancellationToken);
            }
            catch (CloudOperationException)
            {
                ErrorText = Format("CloudPage_DeleteAllFailed");
                return;
            }

            ReportLines.Clear();
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
        }

        await RefreshAsync(cancellationToken);
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

    private CloudVmRow OwnRow(VmDescriptor vm, IReadOnlyList<DiskDescriptor> disks, PricingTable? pricing)
    {
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
            StopCommand = new AsyncRelayCommand(ct => ActAsync(vm, () => _compute.StopVmAsync(vm.Name, vm.Zone, ct), ct), () => running),
            StartCommand = new AsyncRelayCommand(ct => ActAsync(vm, () => _compute.StartVmAsync(vm.Name, vm.Zone, ct), ct), () => stopped),
            DeleteCommand = new AsyncRelayCommand(ct => DeleteOneAsync(vm, ct)),
        };
    }

    private async Task DeleteOneAsync(VmDescriptor vm, CancellationToken cancellationToken)
    {
        if (!await _dialogs.ConfirmAsync(Format("CloudDeleteVm_Title"), Format("CloudDeleteVm_Body", vm.Name), cancellationToken))
        {
            return;
        }

        await ActAsync(vm, () => _compute.DeleteVmAsync(vm.Name, vm.Zone, cancellationToken), cancellationToken);
    }

    /// <summary>One button's call, then a fresh listing so the row shows what Google says now. A refusal names the computer and the action.</summary>
    private async Task ActAsync(VmDescriptor vm, Func<Task> call, CancellationToken cancellationToken)
    {
        try
        {
            await call();
            ErrorText = string.Empty;
        }
        catch (CloudOperationException)
        {
            ErrorText = Format("CloudPage_ActionFailed", vm.Name);
            return;
        }

        await RefreshAsync(cancellationToken);
    }

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

    private static string Size(long bytes) => bytes switch
    {
        >= 1_000_000_000 => (bytes / 1_000_000_000.0).ToString("F1", CultureInfo.InvariantCulture) + " GB",
        >= 1_000_000 => (bytes / 1_000_000.0).ToString("F1", CultureInfo.InvariantCulture) + " MB",
        >= 1_000 => (bytes / 1_000.0).ToString("F0", CultureInfo.InvariantCulture) + " KB",
        _ => bytes.ToString(CultureInfo.InvariantCulture) + " B",
    };
}
