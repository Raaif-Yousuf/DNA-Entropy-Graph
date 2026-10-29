using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Cloud;
using DnaEntropyGraph.Core.Cost;
using DnaEntropyGraph.Core.LocalEngine;
using DnaEntropyGraph.Core.Runs;

namespace DnaEntropyGraph.Persistence;

/// <summary>
/// The cost ledger's writer and the month-to-date reader (issue #109). Every figure is an ESTIMATE from the shipped price list.
/// WHAT IS COUNTED: VM time only, from <c>RunRecord.VmReadyAt</c> (the moment the run's VM is known to exist) to the run's end, times the
/// machine's hourly rate (Spot rate for a Spot run) plus the hourly share of the run's boot disk. A run that never had a VM books nothing.
/// Runs still going are counted separately (<see cref="SpendSummary.InProgressUsd"/>): VM time so far, from the later of the VM's start and
/// the start of the month, up to now. WHAT IS NOT COUNTED: a VM kept alive or left stopped after its run ended (the run's row stops at its
/// end; the Cloud page lists those VMs), upload and other time before the VM existed, bucket storage, and anything Google bills that this
/// app did not start. The month is the calendar month in the PC's local time zone, taken from when a row ended, so a run that ends after
/// midnight on the 1st counts toward the new month. MonthlySpend is not a stored table: the sum is read from the append-only ledger, so
/// there is no second number to drift. Issue #557 will replace the estimate with the instance-timestamp figure.
/// </summary>
public sealed class SpendLedger : ISpendCapGate
{
    public const string VmRuntimeKind = "vm_runtime";

    /// <summary>The boot disk <c>RunOptions.BootDiskGb</c> defaults to, used when a row's options cannot be read.</summary>
    private const int DefaultBootDiskGb = 150;

    /// <summary>The run limit <c>RunOptions.MaxRunDurationMinutes</c> defaults to, used when a row's options cannot be read.</summary>
    private const int DefaultMaxRunMinutes = 240;

    private readonly ICostLedgerRepository _ledger;
    private readonly IPricingSource _pricing;
    private readonly ISettingsStore _settings;
    private readonly TimeProvider _time;
    private readonly TimeZoneInfo _zone;
    private readonly IRunRepository? _runs;
    private readonly Lazy<PricingLoadResult> _table;

    /// <param name="runs">
    /// The UNDECORATED run repository (reading in-progress runs). Null means in-progress runs are not counted. It must not be the
    /// <see cref="LedgerRecordingRunRepository"/> that wraps this ledger: that would be a construction cycle.
    /// </param>
    public SpendLedger(ICostLedgerRepository ledger, IPricingSource pricing, ISettingsStore settings, TimeProvider time, TimeZoneInfo? zone = null, IRunRepository? runs = null)
    {
        _ledger = ledger;
        _pricing = pricing;
        _settings = settings;
        _time = time;
        _zone = zone ?? TimeZoneInfo.Local;
        _runs = runs;
        _table = new Lazy<PricingLoadResult>(() => _pricing.Load());
    }

    /// <summary>
    /// The ledger row an ended run stands for, without writing it, or null when it books nothing (not ended, no VM ever existed, no
    /// machine, no price). Deterministic from the row, so asking twice gives the same answer. Nothing is guessed.
    /// </summary>
    public CostLedgerEntry? Quote(RunRecord run)
    {
        ArgumentNullException.ThrowIfNull(run);
        // A local-engine run (Target "local") rents no VM and books nothing. There is deliberately no fallback to StartedAt for a row with no
        // VmReadyAt: the engine writes the planned VM name up front, so a name is no proof a VM ever existed.
        if (!string.Equals(run.Target, RunTargets.CloudRecord, StringComparison.OrdinalIgnoreCase)
            || !JobStateMachine.IsTerminal(run.Phase)
            || run.VmReadyAt is not { } vmStart
            || run.FinishedAt is not { } finished
            || UsdFor(run, vmStart, finished) is not { } usd)
        {
            return null;
        }

        return new CostLedgerEntry(VmRuntimeKind, vmStart, run.JobId, EndedUtc: finished, UsdEst: usd);
    }

    /// <summary>Appends the run's row once. Returns it, or null when nothing was written (books nothing, or it was already booked, also by a concurrent writer).</summary>
    public async Task<CostLedgerEntry?> RecordRunAsync(RunRecord run, CancellationToken cancellationToken)
        => Quote(run) is { } entry ? await _ledger.TryAppendAsync(entry, cancellationToken).ConfigureAwait(false) : null;

    public async Task<SpendSummary> MonthToDateAsync(CancellationToken cancellationToken)
    {
        var (start, end) = MonthBounds();
        var rows = await ReconcileAsync(await _ledger.GetAllAsync(cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
        var recorded = rows
            .Where(r => r.UsdEst is not null && (r.EndedUtc ?? r.StartedUtc) is var when && when >= start && when < end)
            .Sum(r => r.UsdEst!.Value);
        return new SpendSummary(recorded, await InProgressUsdAsync(rows, start, cancellationToken).ConfigureAwait(false));
    }

    public async Task<SpendCapStatus> CheckAsync(double? estimatedUsd, CancellationToken cancellationToken)
    {
        var summary = await MonthToDateAsync(cancellationToken).ConfigureAwait(false);
        if (SpendCapSettings.ReadCapUsd(_settings) is not { } cap)
        {
            return new SpendCapStatus(SpendCapLevel.None, summary.TotalUsd, null, summary.InProgressUsd);
        }

        if (summary.TotalUsd + Math.Max(0, estimatedUsd ?? 0) < cap)
        {
            return new SpendCapStatus(SpendCapLevel.None, summary.TotalUsd, cap, summary.InProgressUsd);
        }

        return new SpendCapStatus(SpendCapSettings.IsHardStop(_settings) ? SpendCapLevel.Blocked : SpendCapLevel.Warning, summary.TotalUsd, cap, summary.InProgressUsd);
    }

    /// <summary>
    /// Books every ended run that has a VM and no ledger row yet (a ledger append that failed after its run row was stored is otherwise
    /// never re-driven). Idempotent through the unique index. Every read of the month, so every cap check, runs it. Returns the rows
    /// including what it added.
    /// </summary>
    /// <summary>
    /// Issues #100 and #557: the same repair for the pages that show a run's booked cost. The Runs and Results pages never make a month-to-date read, so a ledger append that
    /// failed after its run row was stored would show no figure until the next cap check; they call this first. Idempotent (the unique index), and a no-op with no run history.
    /// </summary>
    public async Task BookUnbookedRunsAsync(CancellationToken cancellationToken)
        => await ReconcileAsync(await _ledger.GetByKindAsync(VmRuntimeKind, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);

    private async Task<IReadOnlyList<CostLedgerEntry>> ReconcileAsync(IReadOnlyList<CostLedgerEntry> rows, CancellationToken cancellationToken)
    {
        if (_runs is null)
        {
            return rows;
        }

        var booked = rows.Where(r => r.RunId is not null && r.Kind == VmRuntimeKind).Select(r => r.RunId!).ToHashSet(StringComparer.Ordinal);
        List<CostLedgerEntry>? added = null;
        foreach (var run in LatestPerJob(await _runs.GetAllAsync(cancellationToken).ConfigureAwait(false)))
        {
            if (booked.Contains(run.JobId) || Quote(run) is not { } quote)
            {
                continue;
            }

            if (await _ledger.TryAppendAsync(quote, cancellationToken).ConfigureAwait(false) is { } entry)
            {
                (added ??= []).Add(entry);
            }
        }

        return added is null ? rows : [.. rows, .. added];
    }

    private static IEnumerable<RunRecord> LatestPerJob(IReadOnlyList<RunRecord> runs)
        => runs.GroupBy(r => r.JobId).Select(g => g.OrderBy(r => r.CreatedUtc).Last());

    private async Task<double> InProgressUsdAsync(IReadOnlyList<CostLedgerEntry> ledgerRows, DateTimeOffset monthStart, CancellationToken cancellationToken)
    {
        if (_runs is null)
        {
            return 0;
        }

        var booked = ledgerRows.Where(r => r.RunId is not null && r.Kind == VmRuntimeKind).Select(r => r.RunId!).ToHashSet(StringComparer.Ordinal);
        var now = _time.GetUtcNow();
        var latest = LatestPerJob(await _runs.GetAllAsync(cancellationToken).ConfigureAwait(false));
        var total = 0.0;
        foreach (var run in latest)
        {
            if (!string.Equals(run.Target, RunTargets.CloudRecord, StringComparison.OrdinalIgnoreCase) || JobStateMachine.IsTerminal(run.Phase) || booked.Contains(run.JobId) || run.VmReadyAt is not { } vmStart)
            {
                continue;
            }

            // A flex-start (wait for a card) run that is still queued is not counted, and needs no test here: VmReadyAt is stamped only when the create is answered with a
            // granted VM, so a queued request has none and is skipped above. THEORY (unverified, no live project; ToTest #92): the create operation completes at the grant, not at
            // queueing. A granted flex VM that is still booting IS counted: it bills, and the phase alone (Provisioning through the boot) says nothing about that.

            var from = vmStart > monthStart ? vmStart : monthStart;
            if (now > from && UsdFor(run, from, now) is { } usd)
            {
                total += usd;
            }
        }

        return total;
    }

    /// <summary>VM time from <paramref name="from"/> to <paramref name="to"/> at the run's machine rate plus its disk share; null when there is no price.</summary>
    private double? UsdFor(RunRecord run, DateTimeOffset from, DateTimeOffset to)
    {
        if (run.MachineType is null || _table.Value.Table is not { } table || table.Find(run.MachineType) is not { } machine)
        {
            return null;
        }

        // Deliberately conservative, not exact: a flex-start run (#92) is priced at the on-demand rate (the price table has no flex rate and
        // the row does not record flex), and a Spot run that was preempted and retried (#91) has IsSpot false by the end, so its whole
        // span (the first attempt included) is booked at on-demand. Both over-estimate spend, which is the safe side of a hard stop.
        var rate = run.IsSpot == true ? machine.SpotUsdPerHour ?? machine.OnDemandUsdPerHour : machine.OnDemandUsdPerHour;
        var options = RunOptionsJson.TryDeserialize(run.OptionsJson);
        var diskGb = options?.BootDiskGb ?? DefaultBootDiskGb;

        // The VM cannot outlive its maxRunDuration (the run limit plus the grace it is created with, Hard Rule 10), so a row stuck
        // in progress (an orphan) stops accruing there instead of growing forever and blocking every run.
        if (run.VmReadyAt is { } start)
        {
            var limit = TimeSpan.FromMinutes(options?.MaxRunDurationMinutes ?? DefaultMaxRunMinutes) + CloudJobRequestFactory.VmGrace;
            var latestEnd = start + limit;
            if (to > latestEnd)
            {
                to = latestEnd;
            }
        }

        return (rate + table.DiskUsdPerHour(diskGb)) * Math.Max(0, (to - from).TotalHours);
    }

    private (DateTimeOffset Start, DateTimeOffset End) MonthBounds()
    {
        var local = TimeZoneInfo.ConvertTime(_time.GetUtcNow(), _zone);
        var first = new DateTime(local.Year, local.Month, 1, 0, 0, 0, DateTimeKind.Unspecified);
        return (new DateTimeOffset(first, _zone.GetUtcOffset(first)), new DateTimeOffset(first.AddMonths(1), _zone.GetUtcOffset(first.AddMonths(1))));
    }
}

/// <summary>
/// Wraps the run repository so every writer of a run row (the runner, the reconciler, the cleanup) feeds the ledger when a row first
/// reaches an ended phase, without any of them knowing. The row is stored FIRST, then the ledger row is appended: a store that throws
/// leaves no ledger row, so the retry books it once, and a ledger append is idempotent (unique index, first write wins), so a retry after
/// a failed append books it too. A ledger failure never fails the run row. The ledger entry is the only record of what a run cost: nothing
/// is written onto the run row (issues #100 and #557 read the figure from the ledger).
/// </summary>
public sealed class LedgerRecordingRunRepository(IRunRepository inner, SpendLedger ledger) : IRunRepository
{
    public Task<IReadOnlyList<RunRecord>> GetAllAsync(CancellationToken cancellationToken) => inner.GetAllAsync(cancellationToken);

    public async Task UpsertAsync(RunRecord run, CancellationToken cancellationToken)
    {
        await inner.UpsertAsync(run, cancellationToken).ConfigureAwait(false);
        await BookAsync(run).ConfigureAwait(false);
    }

    /// <summary>
    /// Declared here on purpose: <see cref="IRunRepository.TryUpsertIfPhaseAsync"/> has a NON-conditional default body, so a decorator that left it out would
    /// inherit a plain upsert and silently drop the #113 phase guard (a Cancelled row overwritten with Completed). The inner repository decides; a ledger row is
    /// booked only when the write was accepted.
    /// </summary>
    public async Task<bool> TryUpsertIfPhaseAsync(RunRecord run, JobPhase? expectedPhase, CancellationToken cancellationToken)
    {
        if (!await inner.TryUpsertIfPhaseAsync(run, expectedPhase, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        await BookAsync(run).ConfigureAwait(false);
        return true;
    }

    private async Task BookAsync(RunRecord stored)
    {
        try
        {
            await ledger.RecordRunAsync(stored, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            System.Diagnostics.Trace.TraceWarning($"cost_ledger_write_failed: {ex.GetType().Name}");
        }
    }
}
