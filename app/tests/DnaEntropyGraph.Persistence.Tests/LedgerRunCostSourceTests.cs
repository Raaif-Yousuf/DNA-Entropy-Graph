using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Cost;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Persistence.Tests;

/// <summary>
/// Issues #100 and #557: the figure a run shows is the ledger entry for it and nothing else. Every case drives the real <see cref="SpendLedger"/>, the real
/// <see cref="LedgerRecordingRunRepository"/> and the real SQLite ledger, then reads what the pages read (<see cref="IRunCostSource"/>) and compares it with the entry.
/// </summary>
public class LedgerRunCostSourceTests
{
    private const string PricingJson = """
        {"schema":1,"asOf":"2026-10-01","currency":"USD","region":"us-central1","source":"test",
         "disk":{"usdPerGbMonth":0.073},
         "machines":{"g2-standard-8":{"accelerator":"L4","onDemandUsdPerHour":1.0,"spotUsdPerHour":0.4}}}
        """;

    private static readonly DateTimeOffset Now = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    private sealed class Pricing : IPricingSource
    {
        public PricingLoadResult Load() => PricingTable.Parse(PricingJson);
    }

    private sealed class Settings : ISettingsStore
    {
        public string? GetString(string key) => null;

        public void SetString(string key, string value)
        {
        }
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class Rows : IRunRepository
    {
        private readonly object _gate = new();
        private readonly List<RunRecord> _rows = [];

        public IReadOnlyList<RunRecord> Snapshot() { lock (_gate) { return _rows.ToList(); } }

        public Task<IReadOnlyList<RunRecord>> GetAllAsync(CancellationToken cancellationToken) => Task.FromResult(Snapshot());

        public Task UpsertAsync(RunRecord run, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                _rows.RemoveAll(r => r.JobId == run.JobId);
                _rows.Add(run);
            }

            return Task.CompletedTask;
        }
    }

    private static RunRecord Ended(string id, double hours, DateTimeOffset? end = null, string? machine = "g2-standard-8")
        => new(id, JobPhase.Completed, Now.AddHours(-hours - 1), Target: "cloud", VmReadyAt: Now.AddHours(-hours), FinishedAt: end ?? Now, MachineType: machine);

    private static (IRunRepository Decorated, ICostLedgerRepository Ledger, IRunCostSource Source, TempPaths Paths) Make()
    {
        var paths = new TempPaths();
        var ledger = new CostLedgerRepository(new SqliteDatabase(paths.DatabasePath));
        var spend = new SpendLedger(ledger, new Pricing(), new Settings(), new FixedTime(Now), TimeZoneInfo.Utc);
        return (new LedgerRecordingRunRepository(new Rows(), spend), ledger, new LedgerRunCostSource(ledger), paths);
    }

    [Fact]
    public async Task A_run_shows_exactly_its_ledger_entry()
    {
        var (runs, ledger, source, paths) = Make();
        using (paths)
        {
            await runs.UpsertAsync(Ended("r1", 2), CancellationToken.None);

            var entry = (await ledger.GetAllAsync(CancellationToken.None)).Single();
            entry.UsdEst.ShouldNotBeNull().ShouldBe(2 * 1.015, 1e-9);
            (await source.GetBookedAsync(CancellationToken.None))["r1"].ShouldBe(entry.UsdEst!.Value, 1e-12);
        }
    }

    [Fact]
    public async Task A_later_copy_of_the_row_that_cannot_be_priced_does_not_change_what_the_run_shows()
    {
        var (runs, ledger, source, paths) = Make();
        using (paths)
        {
            await runs.UpsertAsync(Ended("r1", 1), CancellationToken.None);
            await runs.UpsertAsync(Ended("r1", 1, machine: null), CancellationToken.None);

            var entry = (await ledger.GetAllAsync(CancellationToken.None)).Single();
            (await source.GetBookedAsync(CancellationToken.None))["r1"].ShouldBe(entry.UsdEst!.Value, 1e-12);
            entry.UsdEst.ShouldNotBeNull().ShouldBe(1.015, 1e-9, "the first booking stands");
        }
    }

    [Fact]
    public async Task A_later_copy_with_a_later_finish_does_not_change_what_the_run_shows()
    {
        var (runs, ledger, source, paths) = Make();
        using (paths)
        {
            await runs.UpsertAsync(Ended("r1", 1), CancellationToken.None);
            await runs.UpsertAsync(Ended("r1", 1, end: Now.AddHours(3)), CancellationToken.None);

            var entry = (await ledger.GetAllAsync(CancellationToken.None)).Single();
            (await source.GetBookedAsync(CancellationToken.None))["r1"].ShouldBe(entry.UsdEst!.Value, 1e-12);
            entry.UsdEst.ShouldNotBeNull().ShouldBe(1.015, 1e-9);
        }
    }

    [Fact]
    public async Task Two_terminal_writers_racing_with_different_spans_leave_one_entry_and_the_run_shows_it()
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var (runs, ledger, source, paths) = Make();
            using (paths)
            {
                await Task.WhenAll(
                    Task.Run(() => runs.UpsertAsync(Ended("r1", 1), TestContext.Current.CancellationToken), TestContext.Current.CancellationToken),
                    Task.Run(() => runs.UpsertAsync(Ended("r1", 1, end: Now.AddHours(1)), TestContext.Current.CancellationToken), TestContext.Current.CancellationToken));

                var entry = (await ledger.GetAllAsync(CancellationToken.None)).Single();
                (await source.GetBookedAsync(CancellationToken.None))["r1"].ShouldBe(entry.UsdEst!.Value, 1e-12, $"attempt {attempt}");
            }
        }
    }

    [Fact]
    public async Task A_run_with_no_ledger_entry_is_absent_and_other_kinds_are_not_costs()
    {
        var (runs, ledger, source, paths) = Make();
        using (paths)
        {
            await runs.UpsertAsync(Ended("no-vm", 1) with { VmReadyAt = null }, CancellationToken.None);
            await ledger.AppendAsync(new CostLedgerEntry("disk", Now, "disk-run", UsdEst: 9), CancellationToken.None);
            await ledger.AppendAsync(new CostLedgerEntry("vm_runtime", Now, "bad", UsdEst: -1), CancellationToken.None);

            (await source.GetBookedAsync(CancellationToken.None)).ShouldBeEmpty();
        }
    }

    /// <summary>A real ledger whose appends can be made to fail (the disk was busy), and whose unfiltered read can be forbidden.</summary>
    private sealed class FlakyLedger(ICostLedgerRepository inner) : ICostLedgerRepository
    {
        public bool FailAppends { get; set; }

        public bool ForbidGetAll { get; set; }

        /// <summary>Called on every append attempt, so a test can prove a read made none.</summary>
        public Action? OnAppend { get; set; }

        public Task<IReadOnlyList<CostLedgerEntry>> GetAllAsync(CancellationToken cancellationToken)
            => ForbidGetAll ? throw new InvalidOperationException("the unfiltered read was used") : inner.GetAllAsync(cancellationToken);

        public Task<IReadOnlyList<CostLedgerEntry>> GetByKindAsync(string kind, CancellationToken cancellationToken) => inner.GetByKindAsync(kind, cancellationToken);

        public Task<CostLedgerEntry> AppendAsync(CostLedgerEntry entry, CancellationToken cancellationToken)
        {
            OnAppend?.Invoke();
            return inner.AppendAsync(entry, cancellationToken);
        }

        public Task<CostLedgerEntry?> TryAppendAsync(CostLedgerEntry entry, CancellationToken cancellationToken)
        {
            OnAppend?.Invoke();
            return FailAppends ? throw new IOException("database is locked") : inner.TryAppendAsync(entry, cancellationToken);
        }
    }

    [Fact]
    public async Task The_source_reads_only_the_vm_runtime_rows_through_the_query()
    {
        using var paths = new TempPaths();
        var real = new CostLedgerRepository(new SqliteDatabase(paths.DatabasePath));
        await real.AppendAsync(new CostLedgerEntry(SpendLedger.VmRuntimeKind, Now, "r1", UsdEst: 3), CancellationToken.None);
        var ledger = new FlakyLedger(real) { ForbidGetAll = true };

        (await new LedgerRunCostSource(ledger).GetBookedAsync(CancellationToken.None))["r1"].ShouldBe(3);
    }

    [Fact]
    public async Task Reading_the_run_cost_never_writes_to_the_ledger_a_failed_append_stays_unbooked_until_the_startup_repair()
    {
        // Reads never write: the Runs and Results pages and the support zip read through this source, and a read that books is a page that mutates money records.
        using var paths = new TempPaths();
        var flaky = new FlakyLedger(new CostLedgerRepository(new SqliteDatabase(paths.DatabasePath))) { FailAppends = true };
        var rows = new Rows();
        var spend = new SpendLedger(flaky, new Pricing(), new Settings(), new FixedTime(Now), TimeZoneInfo.Utc, rows);
        var runs = new LedgerRecordingRunRepository(rows, spend);
        await runs.UpsertAsync(Ended("r1", 2), CancellationToken.None);
        flaky.FailAppends = false;
        var appends = 0;
        flaky.OnAppend = () => Interlocked.Increment(ref appends);

        (await new LedgerRunCostSource(flaky).GetBookedAsync(CancellationToken.None)).ShouldBeEmpty("nothing was booked, and a read does not book it");
        (await new LedgerRunCostSource(flaky).GetBookedAsync(CancellationToken.None)).ShouldBeEmpty();
        appends.ShouldBe(0, "a read made no ledger write");
        (await flaky.GetAllAsync(CancellationToken.None)).ShouldBeEmpty();

        // The one place that books it: the startup repair, idempotent.
        await spend.BookUnbookedRunsAsync(CancellationToken.None);
        await spend.BookUnbookedRunsAsync(CancellationToken.None);
        (await flaky.GetAllAsync(CancellationToken.None)).Count.ShouldBe(1, "booked once; a second repair adds nothing");
        (await new LedgerRunCostSource(flaky).GetBookedAsync(CancellationToken.None))["r1"].ShouldBe(2 * 1.015, 1e-9);
    }
}
