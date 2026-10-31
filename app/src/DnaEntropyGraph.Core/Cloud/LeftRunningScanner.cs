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

/// <summary>A scope the scan could not list. <paramref name="NeedsSignIn"/> is true when its Google account is signed out (the one failure whose action is Sign in); any other failure (offline, a locked accounts file) is false.</summary>
public sealed record UnreadScope(CloudScope Scope, bool NeedsSignIn);

/// <summary>What the scan found. <see cref="IsClean"/> is true when nothing of this installation runs and no disk is being paid for. <paramref name="UnreadScopes"/> counts the scopes (project and account) that could not be listed (signed out, offline) while others were.</summary>
public sealed record LeftRunningReport(IReadOnlyList<LeftRunningVm> RunningVms, IReadOnlyList<LeftStoppedDisk> StoppedDisks, int UnreadScopes = 0, IReadOnlyList<UnreadScope>? Unread = null)
{
    /// <summary>Issue #830 review M4: which scopes could not be read and whether signing in is the fix, so the exit prompt can name the account and offer the action.</summary>
    public IReadOnlyList<UnreadScope> UnreadList => Unread ?? [];

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

        // Issue #830: a run is live for a computer only in the scope it runs in: the live set is keyed by (scope, job id), so a same-named job of another project or account never vouches for this one.
        var live = (await runs.GetAllAsync(cancellationToken).ConfigureAwait(false)).Where(r => !JobStateMachine.IsTerminal(r.Phase)).Select(r => (Scope: RunScope.Of(r), r.JobId)).ToList();
        var table = LoadPricing();
        var now = clock.GetUtcNow();

        var runningReport = running.Select(vm =>
        {
            var jobId = vm.Labels is not null && vm.Labels.TryGetValue("job-id", out var job) ? job : null;
            var machine = vm.MachineType is { Length: > 0 } type ? table?.Find(type[(type.LastIndexOf('/') + 1)..]) : null;
            return new LeftRunningVm(vm.Name, vm.Zone, jobId, RemainingOf(vm, now), machine?.OnDemandUsdPerHour, jobId is not null && live.Any(l => l.JobId == jobId && CloudScopes.Matches(l.Scope, scope)), vm, scope);
        }).ToList();

        var runningNames = running.Select(v => v.Name).ToHashSet(StringComparer.Ordinal);
        var stopped = disks
            .Where(d => !d.Users.Any(u => runningNames.Contains(u[(u.LastIndexOf('/') + 1)..])))
            .Select(d => new LeftStoppedDisk(d.Name, d.Zone, d.SizeGb, table is null ? null : d.SizeGb * table.DiskUsdPerGbMonth, scope))
            .ToList();
        return new LeftRunningReport(runningReport, stopped);
    }

    /// <summary>
    /// Issue #830: scans every scope a recent run of this installation was made in (<see cref="CloudScopes.ForSweep"/>) plus <paramref name="selected"/>, each once, and merges the reports. One scope
    /// that cannot be read (its account signed out, a network failure) does not hide the others: it is counted in <see cref="LeftRunningReport.UnreadScopes"/>. When every scope fails the first
    /// failure is thrown, so the caller degrades exactly as for a single scan. No scope at all is a clean report.
    /// </summary>
    public async Task<LeftRunningReport> ScanAllAsync(CloudScope? selected, CancellationToken cancellationToken)
    {
        var installation = InstallationId.GetOrCreate(settings);
        var scopes = CloudScopes.ForSweep(await runs.GetAllAsync(cancellationToken).ConfigureAwait(false), installation, clock.GetUtcNow(), selected);
        var results = await Task.WhenAll(scopes.Select(async scope =>
        {
            try
            {
                return (Scope: scope, Report: await ScanAsync(scope, cancellationToken).ConfigureAwait(false), Error: (Exception?)null);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                return (Scope: scope, Report: (LeftRunningReport?)null, Error: ex);
            }
        })).ConfigureAwait(false);

        var ok = results.Where(r => r.Report is not null).Select(r => r.Report!).ToList();
        var failed = results.Where(r => r.Error is not null).ToList();
        if (ok.Count == 0 && failed.Count > 0)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failed[0].Error!).Throw();
        }

        return new LeftRunningReport([.. ok.SelectMany(r => r.RunningVms)], [.. ok.SelectMany(r => r.StoppedDisks)], failed.Count, [.. failed.Select(f => new UnreadScope(f.Scope, f.Error is AccountAuthException { Code: AuthErrorCodes.SigninExpired }))]);
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
