using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Cost;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Persistence.Tests;

/// <summary>Issue #109: the ledger writer, the month-to-date sum and the cap check.</summary>
public class SpendLedgerTests
{
    // g2-standard-8 on demand 1.00/h, spot 0.40/h; disk 0.073 USD per GB-month so 150 GB is exactly 0.015/h (730 h month).
    private const string PricingJson = """
        {"schema":1,"asOf":"2026-10-01","currency":"USD","region":"us-central1","source":"test",
         "disk":{"usdPerGbMonth":0.073},
         "machines":{"g2-standard-8":{"accelerator":"L4","onDemandUsdPerHour":1.0,"spotUsdPerHour":0.4}}}
        """;

    private sealed class Pricing : IPricingSource
    {
        public PricingLoadResult Load() => PricingTable.Parse(PricingJson);
    }

    private sealed class Settings : ISettingsStore
    {
        public Dictionary<string, string> Values { get; } = [];

        public string? GetString(string key) => Values.GetValueOrDefault(key);

        public void SetString(string key, string value) => Values[key] = value;
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class MemoryRuns : IRunRepository
    {
        public List<RunRecord> Rows { get; } = [];

        /// <summary>When above zero, that many upserts throw before one succeeds.</summary>
        public int FailNext { get; set; }

        public Task<IReadOnlyList<RunRecord>> GetAllAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<RunRecord>>(Rows.ToList());

        public Task UpsertAsync(RunRecord run, CancellationToken cancellationToken)
        {
            if (FailNext > 0)
            {
                FailNext--;
                throw new IOException("disk full");
            }

            Rows.RemoveAll(r => r.JobId == run.JobId);
            Rows.Add(run);
            return Task.CompletedTask;
        }
    }

    private static readonly DateTimeOffset Now = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    /// <summary>A run whose VM existed for <paramref name="hours"/> and ended now.</summary>
    private static RunRecord Finished(string id, double hours, bool spot = false, JobPhase phase = JobPhase.Completed, string? machine = "g2-standard-8", bool vm = true)
        => new(id, phase, Now.AddHours(-hours - 1), Target: "cloud", StartedAt: Now.AddHours(-hours - 1), VmReadyAt: vm ? Now.AddHours(-hours) : null, FinishedAt: Now, MachineType: machine, IsSpot: spot);

    private static (SpendLedger Ledger, CostLedgerRepository Repo, Settings Settings, TempPaths Paths) Make(IRunRepository? runs = null, TimeZoneInfo? zone = null, DateTimeOffset? now = null)
    {
        var paths = new TempPaths();
        var repo = new CostLedgerRepository(new SqliteDatabase(paths.DatabasePath));
        var settings = new Settings();
        return (new SpendLedger(repo, new Pricing(), settings, new FixedTime(now ?? Now), zone ?? TimeZoneInfo.Utc, runs), repo, settings, paths);
    }

    [Fact]
    public async Task A_finished_run_appends_one_vm_runtime_row_priced_from_vm_time_rate_and_disk()
    {
        var (ledger, repo, _, paths) = Make();
        using (paths)
        {
            var entry = await ledger.RecordRunAsync(Finished("r1", hours: 2), CancellationToken.None);

            entry.ShouldNotBeNull();
            var row = (await repo.GetAllAsync(CancellationToken.None)).Single();
            row.Kind.ShouldBe("vm_runtime");
            row.RunId.ShouldBe("r1");
            row.UsdEst.ShouldNotBeNull().ShouldBe(2 * (1.0 + 0.015), 1e-9);
            row.StartedUtc.ShouldBe(Now.AddHours(-2));
            row.EndedUtc.ShouldBe(Now);
        }
    }

    [Fact]
    public async Task A_run_that_never_had_a_VM_books_nothing_whatever_its_phase_and_machine_type()
    {
        var (ledger, repo, _, paths) = Make();
        using (paths)
        {
            await ledger.RecordRunAsync(Finished("r1", hours: 3, phase: JobPhase.Failed, vm: false), CancellationToken.None);
            await ledger.RecordRunAsync(Finished("r2", hours: 3, phase: JobPhase.Completed, vm: false), CancellationToken.None);

            (await repo.GetAllAsync(CancellationToken.None)).ShouldBeEmpty("time before the VM existed (upload, preflight, stockout retries) is not VM time");
        }
    }

    [Fact]
    public async Task A_spot_run_is_priced_at_the_spot_rate()
    {
        var (ledger, repo, _, paths) = Make();
        using (paths)
        {
            await ledger.RecordRunAsync(Finished("r1", hours: 1, spot: true), CancellationToken.None);

            (await repo.GetAllAsync(CancellationToken.None)).Single().UsdEst.ShouldNotBeNull().ShouldBe(0.4 + 0.015, 1e-9);
        }
    }

    [Fact]
    public async Task Recording_the_same_run_twice_writes_one_row()
    {
        var (ledger, repo, _, paths) = Make();
        using (paths)
        {
            await ledger.RecordRunAsync(Finished("r1", 1), CancellationToken.None);
            var again = await ledger.RecordRunAsync(Finished("r1", 1), CancellationToken.None);

            again.ShouldBeNull();
            (await repo.GetAllAsync(CancellationToken.None)).Count.ShouldBe(1);
        }
    }

    [Fact]
    public async Task Two_writers_recording_the_same_run_at_once_leave_one_row()
    {
        var (ledger, repo, _, paths) = Make();
        using (paths)
        {
            var run = Finished("r1", 1);

            var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => ledger.RecordRunAsync(run, CancellationToken.None), CancellationToken.None)));

            results.Count(r => r is not null).ShouldBe(1);
            (await repo.GetAllAsync(CancellationToken.None)).Count.ShouldBe(1);
        }
    }

    [Fact]
    public async Task The_database_itself_refuses_a_second_row_for_the_same_run_and_kind_but_not_rows_with_no_run()
    {
        var (_, repo, _, paths) = Make();
        using (paths)
        {
            (await repo.TryAppendAsync(new CostLedgerEntry("vm_runtime", Now, "r1", UsdEst: 1), CancellationToken.None)).ShouldNotBeNull();
            (await repo.TryAppendAsync(new CostLedgerEntry("vm_runtime", Now, "r1", UsdEst: 2), CancellationToken.None)).ShouldBeNull();
            (await repo.TryAppendAsync(new CostLedgerEntry("storage", Now, "r1", UsdEst: 2), CancellationToken.None)).ShouldNotBeNull();
            (await repo.TryAppendAsync(new CostLedgerEntry("storage", Now, null, UsdEst: 2), CancellationToken.None)).ShouldNotBeNull();
            (await repo.TryAppendAsync(new CostLedgerEntry("storage", Now, null, UsdEst: 2), CancellationToken.None)).ShouldNotBeNull();
            (await repo.GetAllAsync(CancellationToken.None)).Count.ShouldBe(4);
        }
    }

    [Theory]
    [InlineData(JobPhase.Running)]
    [InlineData(JobPhase.Provisioning)]
    [InlineData(JobPhase.Draft)]
    public async Task A_run_that_has_not_ended_writes_no_row(JobPhase phase)
    {
        var (ledger, repo, _, paths) = Make();
        using (paths)
        {
            await ledger.RecordRunAsync(Finished("r1", 1, phase: phase), CancellationToken.None);

            (await repo.GetAllAsync(CancellationToken.None)).ShouldBeEmpty();
        }
    }

    [Fact]
    public async Task A_run_with_no_machine_type_writes_nothing()
    {
        var (ledger, repo, _, paths) = Make();
        using (paths)
        {
            await ledger.RecordRunAsync(Finished("r1", 1, phase: JobPhase.Failed, machine: null), CancellationToken.None);

            (await repo.GetAllAsync(CancellationToken.None)).ShouldBeEmpty();
        }
    }

    [Fact]
    public async Task A_machine_missing_from_the_price_list_writes_nothing_instead_of_a_guess()
    {
        var (ledger, repo, _, paths) = Make();
        using (paths)
        {
            await ledger.RecordRunAsync(Finished("r1", 1, machine: "a2-highgpu-1g"), CancellationToken.None);

            (await repo.GetAllAsync(CancellationToken.None)).ShouldBeEmpty();
        }
    }

    [Fact]
    public async Task Month_to_date_sums_only_rows_ended_in_this_local_calendar_month()
    {
        var (ledger, repo, _, paths) = Make();
        using (paths)
        {
            await repo.AppendAsync(new CostLedgerEntry("vm_runtime", Now.AddDays(-20), "old", EndedUtc: Now.AddDays(-20), UsdEst: 100), CancellationToken.None);
            await repo.AppendAsync(new CostLedgerEntry("vm_runtime", Now.AddDays(-2), "a", EndedUtc: Now.AddDays(-2), UsdEst: 1.5), CancellationToken.None);
            await repo.AppendAsync(new CostLedgerEntry("vm_runtime", Now.AddDays(-1), "b", EndedUtc: Now.AddDays(-1), UsdEst: 2.0), CancellationToken.None);
            await repo.AppendAsync(new CostLedgerEntry("vm_runtime", Now, "c", UsdEst: null), CancellationToken.None);

            var summary = await ledger.MonthToDateAsync(CancellationToken.None);

            summary.RecordedUsd.ShouldBe(3.5, 1e-9);
            summary.InProgressUsd.ShouldBe(0);
        }
    }

    [Fact]
    public async Task The_month_starts_at_local_midnight_not_utc_midnight()
    {
        // UTC+10. Now is 2026-09-30 20:00 UTC = 2026-10-01 06:00 local, so October has begun locally.
        var zone = TimeZoneInfo.CreateCustomTimeZone("test+10", TimeSpan.FromHours(10), "test+10", "test+10");
        var now = new DateTimeOffset(2026, 9, 30, 20, 0, 0, TimeSpan.Zero);
        var (ledger, repo, _, paths) = Make(zone: zone, now: now);
        using (paths)
        {
            // 12:00 UTC on the 30th is 22:00 local on 30 September: last month.
            await repo.AppendAsync(new CostLedgerEntry("vm_runtime", now.AddHours(-9), "september", EndedUtc: now.AddHours(-8), UsdEst: 50), CancellationToken.None);
            // 15:00 UTC on the 30th is 01:00 local on 1 October: this month, though UTC still says September.
            await repo.AppendAsync(new CostLedgerEntry("vm_runtime", now.AddHours(-5), "october", EndedUtc: now.AddHours(-5), UsdEst: 2), CancellationToken.None);

            (await ledger.MonthToDateAsync(CancellationToken.None)).RecordedUsd.ShouldBe(2, 1e-9);
        }
    }

    [Fact]
    public async Task A_run_in_progress_counts_its_accrued_vm_time_and_is_reported_separately()
    {
        var runs = new MemoryRuns();
        var (ledger, repo, _, paths) = Make(runs);
        using (paths)
        {
            await repo.AppendAsync(new CostLedgerEntry("vm_runtime", Now.AddDays(-1), "done", EndedUtc: Now.AddDays(-1), UsdEst: 3), CancellationToken.None);
            runs.Rows.Add(new RunRecord("live", JobPhase.Running, Now.AddHours(-3), Target: "cloud", MachineType: "g2-standard-8", VmReadyAt: Now.AddHours(-2)));
            runs.Rows.Add(new RunRecord("novm", JobPhase.Uploading, Now.AddHours(-3), Target: "cloud", MachineType: "g2-standard-8"));

            var summary = await ledger.MonthToDateAsync(CancellationToken.None);

            summary.RecordedUsd.ShouldBe(3, 1e-9);
            summary.InProgressUsd.ShouldBe(2 * 1.015, 1e-9, "two hours of VM time; the run with no VM yet adds nothing");
            summary.TotalUsd.ShouldBe(3 + 2 * 1.015, 1e-9);
        }
    }

    [Fact]
    public async Task A_run_in_progress_is_only_counted_from_the_start_of_the_month()
    {
        var runs = new MemoryRuns();
        var zone = TimeZoneInfo.Utc;
        var now = new DateTimeOffset(2026, 10, 1, 1, 0, 0, TimeSpan.Zero);
        var (ledger, _, _, paths) = Make(runs, zone, now);
        using (paths)
        {
            runs.Rows.Add(new RunRecord("live", JobPhase.Running, now.AddHours(-5), Target: "cloud", MachineType: "g2-standard-8", VmReadyAt: now.AddHours(-3)));

            (await ledger.MonthToDateAsync(CancellationToken.None)).InProgressUsd.ShouldBe(1.015, 1e-9, "only the hour since midnight is October's");
        }
    }

    [Fact]
    public async Task The_cap_check_includes_runs_in_progress()
    {
        var runs = new MemoryRuns();
        var (ledger, _, settings, paths) = Make(runs);
        using (paths)
        {
            settings.Values[SpendCapSettings.CapUsdKey] = "2";
            runs.Rows.Add(new RunRecord("live", JobPhase.Running, Now.AddHours(-3), Target: "cloud", MachineType: "g2-standard-8", VmReadyAt: Now.AddHours(-2)));

            var status = await ledger.CheckAsync(null, CancellationToken.None);

            status.Level.ShouldBe(SpendCapLevel.Warning);
            status.InProgressUsd.ShouldBe(2 * 1.015, 1e-9);
        }
    }

    [Fact]
    public async Task No_cap_set_means_no_card()
    {
        var (ledger, _, _, paths) = Make();
        using (paths)
        {
            (await ledger.CheckAsync(50, CancellationToken.None)).Level.ShouldBe(SpendCapLevel.None);
        }
    }

    [Fact]
    public async Task Under_the_cap_shows_nothing_and_reaching_it_with_the_estimate_warns()
    {
        var (ledger, repo, settings, paths) = Make();
        using (paths)
        {
            settings.Values[SpendCapSettings.CapUsdKey] = "10";
            await repo.AppendAsync(new CostLedgerEntry("vm_runtime", Now, "a", EndedUtc: Now, UsdEst: 6), CancellationToken.None);

            (await ledger.CheckAsync(3, CancellationToken.None)).Level.ShouldBe(SpendCapLevel.None);
            var warn = await ledger.CheckAsync(4, CancellationToken.None);
            warn.Level.ShouldBe(SpendCapLevel.Warning);
            warn.MonthToDateUsd.ShouldBe(6, 1e-9);
            warn.CapUsd.ShouldBe(10);
        }
    }

    [Fact]
    public async Task With_hard_stop_on_the_same_overage_is_blocked()
    {
        var (ledger, repo, settings, paths) = Make();
        using (paths)
        {
            settings.Values[SpendCapSettings.CapUsdKey] = "10";
            settings.Values[SpendCapSettings.HardStopKey] = "true";
            await repo.AppendAsync(new CostLedgerEntry("vm_runtime", Now, "a", EndedUtc: Now, UsdEst: 12), CancellationToken.None);

            (await ledger.CheckAsync(null, CancellationToken.None)).Level.ShouldBe(SpendCapLevel.Blocked);
        }
    }

    [Fact]
    public async Task The_run_row_is_stored_before_the_ledger_row_so_a_failed_store_leaves_no_ledger_row_and_the_retry_books_it_once()
    {
        var runs = new MemoryRuns();
        var (ledger, repo, _, paths) = Make();
        using (paths)
        {
            var decorated = new LedgerRecordingRunRepository(runs, ledger);
            var run = Finished("r1", 1);
            runs.FailNext = 1;

            await Should.ThrowAsync<IOException>(() => decorated.UpsertAsync(run, CancellationToken.None));
            (await repo.GetAllAsync(CancellationToken.None)).ShouldBeEmpty("nothing is booked for a row that was never stored");

            await decorated.UpsertAsync(run, CancellationToken.None);

            (await repo.GetAllAsync(CancellationToken.None)).Count.ShouldBe(1);
            var stored = runs.Rows.Single();
            stored.EstimatedCostUsd.ShouldNotBeNull().ShouldBe(1.015, 1e-9);
            stored.VmSeconds.ShouldBe(3600);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("NaN")]
    public void An_unusable_cap_value_reads_as_no_cap(string? raw)
    {
        var settings = new Settings();
        if (raw is not null)
        {
            settings.Values[SpendCapSettings.CapUsdKey] = raw;
        }

        SpendCapSettings.ReadCapUsd(settings).ShouldBeNull();
    }
}
