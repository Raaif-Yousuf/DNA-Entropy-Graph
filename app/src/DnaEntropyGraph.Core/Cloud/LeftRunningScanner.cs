using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Cost;

namespace DnaEntropyGraph.Core.Cloud;

/// <summary>
/// One running computer of this installation. <paramref name="Remaining"/> is the time before Compute Engine ends it at its <c>maxRunDuration</c> (Hard Rule 11's
/// expiry: a computer left running is never left indefinitely), null when the limit is unknown. <paramref name="UsdPerHour"/> is the computer's own rate, null when
/// the price list could not be read. <paramref name="HasLiveRun"/> is true when a run of this installation that has not ended carries the VM's job id.
/// </summary>
public sealed record LeftRunningVm(string Name, string Zone, string? JobId, TimeSpan? Remaining, double? UsdPerHour, bool HasLiveRun, VmDescriptor Descriptor, CloudScope? Scope = null);

/// <summary>A disk of this installation that no running computer holds: it keeps billing every month until it is deleted.</summary>
public sealed record LeftStoppedDisk(string Name, string Zone, int SizeGb, double? UsdPerMonth, CloudScope? Scope = null);

/// <summary>What the scan found. <see cref="IsClean"/> is true when nothing of this installation runs and no disk is being paid for.</summary>
public sealed record LeftRunningReport(IReadOnlyList<LeftRunningVm> RunningVms, IReadOnlyList<LeftStoppedDisk> StoppedDisks)
{
    public bool IsClean => RunningVms.Count == 0 && StoppedDisks.Count == 0;
}

/// <summary>
/// Issue #113: the nothing-left-running scan. It reuses the Cloud page's label-filtered listings (<see cref="IComputeGateway.ListByInstallationAsync"/> and
/// <see cref="IComputeGateway.ListDisksByInstallationAsync"/>), so discovery is by label and only this installation's resources are ever reported (Hard Rules 9 and 10).
/// Read-only: it changes nothing; <c>LeftRunningGuard</c> decides what to do with the report. Throws what the gateway throws (the caller decides how to degrade).
/// </summary>
public sealed class LeftRunningScanner(IComputeGateway compute, ISettingsStore settings, IRunRepository runs, IPricingSource? pricing, TimeProvider clock)
{
    public async Task<LeftRunningReport> ScanAsync(CloudScope scope, CancellationToken cancellationToken)
    {
        var installation = InstallationId.GetOrCreate(settings);
        var vms = (await compute.ListByInstallationAsync(scope, installation, cancellationToken).ConfigureAwait(false)).Where(v => Owns(v.Labels, installation)).ToList();
        var disks = (await compute.ListDisksByInstallationAsync(scope, installation, cancellationToken).ConfigureAwait(false)).Where(d => d.BelongsTo(installation)).ToList();
        var running = vms.Where(IsRunning).ToList();
        if (running.Count == 0 && disks.Count == 0)
        {
            return new LeftRunningReport([], []);
        }

        var live = (await runs.GetAllAsync(cancellationToken).ConfigureAwait(false)).Where(r => !JobStateMachine.IsTerminal(r.Phase)).Select(r => r.JobId).ToHashSet(StringComparer.Ordinal);
        var table = LoadPricing();
        var now = clock.GetUtcNow();

        var runningReport = running.Select(vm =>
        {
            var jobId = vm.Labels is not null && vm.Labels.TryGetValue("job-id", out var job) ? job : null;
            var machine = vm.MachineType is { Length: > 0 } type ? table?.Find(type[(type.LastIndexOf('/') + 1)..]) : null;
            return new LeftRunningVm(vm.Name, vm.Zone, jobId, RemainingOf(vm, now), machine?.OnDemandUsdPerHour, jobId is not null && live.Contains(jobId), vm, scope);
        }).ToList();

        var runningNames = running.Select(v => v.Name).ToHashSet(StringComparer.Ordinal);
        var stopped = disks
            .Where(d => !d.Users.Any(u => runningNames.Contains(u[(u.LastIndexOf('/') + 1)..])))
            .Select(d => new LeftStoppedDisk(d.Name, d.Zone, d.SizeGb, table is null ? null : d.SizeGb * table.DiskUsdPerGbMonth, scope))
            .ToList();
        return new LeftRunningReport(runningReport, stopped);
    }

    private static TimeSpan? RemainingOf(VmDescriptor vm, DateTimeOffset localNow)
    {
        if (vm.MaxRunDuration is not { } limit || vm.RunClockStart is not { } start)
        {
            return null;
        }

        // The platform's own clock when the read carried one (#691): the laptop's clock may be an hour out.
        var remaining = start + limit - (vm.ServerNow ?? localNow);
        return remaining < TimeSpan.Zero ? TimeSpan.Zero : remaining;
    }

    private PricingTable? LoadPricing()
    {
        try
        {
            return pricing?.Load().Table;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool Owns(IReadOnlyDictionary<string, string>? labels, string installation)
        => labels is not null
            && labels.TryGetValue("app", out var app) && app == VmSpec.AppLabelValue
            && labels.TryGetValue("installation-id", out var owner) && owner == installation;

    private static bool IsRunning(VmDescriptor vm) => vm.Status is "RUNNING" or "STAGING" or "PROVISIONING";
}
