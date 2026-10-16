using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Cloud;
using DnaEntropyGraph.Core.Cost;
using DnaEntropyGraph.Core.Runs;

namespace DnaEntropyGraph.Persistence;

/// <summary>
/// The cost ledger's writer and the month-to-date reader (issue #109). Every figure is an ESTIMATE from the shipped price list:
/// the run's recorded start to end, times the machine's hourly rate (Spot rate for a Spot run) plus the hourly share of the run's
/// boot disk. It is not the Google bill (issue #557 will add the instance-timestamp figure). The month is the calendar month in the
/// PC's local time zone, taken from when a row ended, so a run that ends after midnight on the 1st counts toward the new month.
/// MonthlySpend is not a stored table: the sum is read from the append-only ledger, so there is no second number to drift.
/// </summary>
public sealed class SpendLedger : ISpendCapGate
{
    public const string VmRuntimeKind = "vm_runtime";

    /// <summary>The boot disk <c>RunOptions.BootDiskGb</c> defaults to, used when a row's options cannot be read.</summary>
    private const int DefaultBootDiskGb = 150;

    private readonly ICostLedgerRepository _ledger;
    private readonly IPricingSource _pricing;
    private readonly ISettingsStore _settings;
    private readonly TimeProvider _time;
    private readonly TimeZoneInfo _zone;
    private readonly Lazy<PricingLoadResult> _table;

    public SpendLedger(ICostLedgerRepository ledger, IPricingSource pricing, ISettingsStore settings, TimeProvider time, TimeZoneInfo? zone = null)
    {
        _ledger = ledger;
        _pricing = pricing;
        _settings = settings;
        _time = time;
        _zone = zone ?? TimeZoneInfo.Local;
        _table = new Lazy<PricingLoadResult>(() => _pricing.Load());
    }

    /// <summary>
    /// Appends one <c>vm_runtime</c> row for an ended run that had a machine, once per run. Returns the row, or null when nothing was
    /// written (not ended, no machine, no price, already recorded). Nothing is guessed: a machine the price list lacks writes no row.
    /// </summary>
    public async Task<CostLedgerEntry?> RecordRunAsync(RunRecord run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (!JobStateMachine.IsTerminal(run.Phase)
            || run.MachineType is null
            || run.StartedAt is not { } started
            || run.FinishedAt is not { } finished
            || _table.Value.Table is not { } table
            || table.Find(run.MachineType) is not { } machine)
        {
            return null;
        }

        var existing = await _ledger.GetAllAsync(cancellationToken).ConfigureAwait(false);
        if (existing.Any(e => e.RunId == run.JobId && e.Kind == VmRuntimeKind))
        {
            return null;
        }

        var rate = run.IsSpot == true ? machine.SpotUsdPerHour ?? machine.OnDemandUsdPerHour : machine.OnDemandUsdPerHour;
        var diskGb = RunOptionsJson.TryDeserialize(run.OptionsJson)?.BootDiskGb ?? DefaultBootDiskGb;
        var hours = Math.Max(0, (finished - started).TotalHours);
        var usd = (rate + table.DiskUsdPerHour(diskGb)) * hours;
        return await _ledger.AppendAsync(new CostLedgerEntry(VmRuntimeKind, started, run.JobId, EndedUtc: finished, UsdEst: usd), cancellationToken).ConfigureAwait(false);
    }

    public async Task<double> MonthToDateUsdAsync(CancellationToken cancellationToken)
    {
        var (start, end) = MonthBounds();
        var rows = await _ledger.GetAllAsync(cancellationToken).ConfigureAwait(false);
        return rows
            .Where(r => r.UsdEst is not null && (r.EndedUtc ?? r.StartedUtc) is var when && when >= start && when < end)
            .Sum(r => r.UsdEst!.Value);
    }

    public async Task<SpendCapStatus> CheckAsync(double? estimatedUsd, CancellationToken cancellationToken)
    {
        var spent = await MonthToDateUsdAsync(cancellationToken).ConfigureAwait(false);
        if (SpendCapSettings.ReadCapUsd(_settings) is not { } cap)
        {
            return new SpendCapStatus(SpendCapLevel.None, spent, null);
        }

        if (spent + Math.Max(0, estimatedUsd ?? 0) < cap)
        {
            return new SpendCapStatus(SpendCapLevel.None, spent, cap);
        }

        return new SpendCapStatus(SpendCapSettings.IsHardStop(_settings) ? SpendCapLevel.Blocked : SpendCapLevel.Warning, spent, cap);
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
/// reaches an ended phase, without any of them knowing. The ledger row is written BEFORE the row is stored, so by the time the phase
/// message reaches the UI the spend is counted. A ledger failure never fails the run row.
/// </summary>
public sealed class LedgerRecordingRunRepository(IRunRepository inner, SpendLedger ledger) : IRunRepository
{
    public Task<IReadOnlyList<RunRecord>> GetAllAsync(CancellationToken cancellationToken) => inner.GetAllAsync(cancellationToken);

    public async Task UpsertAsync(RunRecord run, CancellationToken cancellationToken)
    {
        try
        {
            if (await ledger.RecordRunAsync(run, CancellationToken.None).ConfigureAwait(false) is { } entry)
            {
                run = run with { EstimatedCostUsd = entry.UsdEst, VmSeconds = (long)Math.Round(((entry.EndedUtc ?? entry.StartedUtc) - entry.StartedUtc).TotalSeconds) };
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            System.Diagnostics.Trace.TraceWarning($"cost_ledger_write_failed: {ex.GetType().Name}");
        }

        await inner.UpsertAsync(run, cancellationToken).ConfigureAwait(false);
    }
}
