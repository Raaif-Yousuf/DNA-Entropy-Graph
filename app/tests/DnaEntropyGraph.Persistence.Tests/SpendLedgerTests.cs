using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Cost;
using DnaEntropyGraph.Core.Runs;
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

        public int GetAllCalls { get; private set; }

        public int GetByJobIdCalls { get; private set; }

        public Task<IReadOnlyList<RunRecord>> GetAllAsync(CancellationToken cancellationToken)
        {
            GetAllCalls++;
            return Task.FromResult<IReadOnlyList<RunRecord>>(Rows.ToList());
        }

        public Task<RunRecord?> GetByJobIdAsync(string jobId, CancellationToken cancellationToken)
        {
            GetByJobIdCalls++;
            return Task.FromResult(Rows.Where(r => r.JobId == jobId).OrderBy(r => r.CreatedUtc).LastOrDefault());
        }

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
    public async Task A_run_with_no_VM_start_yet_adds_nothing_to_the_hard_stop_whatever_its_options_say()
    {
        // A queued flex request has no VmReadyAt (stamped only when the create is answered), so it is skipped on that alone. The granted-and-still-Provisioning
        // case is covered through the real runner in Cloud.Tests (SpendLedgerRunnerTests), not with a hand-built row.
        var runs = new MemoryRuns();
        var (ledger, _, _, paths) = Make(runs);
        using (paths)
        {
            var flex = RunOptionsJson.Serialize(new RunOptions { ModelId = "evo2_7b", RunTarget = "Cloud", WaitForGpu = true });
            runs.Rows.Add(new RunRecord("queued", JobPhase.Provisioning, Now.AddHours(-3), Target: "cloud", MachineType: "g2-standard-8", OptionsJson: flex));
            runs.Rows.Add(new RunRecord("granted", JobPhase.Provisioning, Now.AddHours(-3), Target: "cloud", MachineType: "g2-standard-8", VmReadyAt: Now.AddHours(-1), OptionsJson: flex));

            var summary = await ledger.MonthToDateAsync(CancellationToken.None);

            summary.InProgressUsd.ShouldBe(1.015, 1e-9, "the granted flex VM (1 h) bills although its phase is still Provisioning; the queued request adds nothing");
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
    public async Task A_run_on_this_PC_rather_than_in_the_cloud_books_nothing()
    {
        var (ledger, repo, _, paths) = Make();
        using (paths)
        {
            await ledger.RecordRunAsync(Finished("r1", 2) with { Target = "local" }, CancellationToken.None);

            (await repo.GetAllAsync(CancellationToken.None)).ShouldBeEmpty("a local-engine run rents no VM");
        }
    }

    [Fact]
    public async Task A_run_whose_ledger_append_failed_is_booked_the_next_time_the_month_is_read_and_only_once()
    {
        var runs = new MemoryRuns();
        var (ledger, repo, _, paths) = Make(runs);
        using (paths)
        {
            runs.Rows.Add(Finished("lost", 2));

            var first = await ledger.MonthToDateAsync(CancellationToken.None);
            var second = await ledger.MonthToDateAsync(CancellationToken.None);

            first.RecordedUsd.ShouldBe(2 * 1.015, 1e-9);
            second.RecordedUsd.ShouldBe(2 * 1.015, 1e-9);
            (await repo.GetAllAsync(CancellationToken.None)).Count.ShouldBe(1);
        }
    }

    [Fact]
    public async Task A_run_row_left_in_progress_for_thirty_days_accrues_no_more_than_its_max_run_duration()
    {
        var runs = new MemoryRuns();
        var (ledger, _, settings, paths) = Make(runs);
        using (paths)
        {
            // Thirty days ago it was last month and its VM is long gone; three days ago it is this month and capped.
            runs.Rows.Add(new RunRecord("orphan30", JobPhase.Running, Now.AddDays(-30), Target: "cloud", MachineType: "g2-standard-8", VmReadyAt: Now.AddDays(-30)));
            runs.Rows.Add(new RunRecord("orphan3", JobPhase.Running, Now.AddDays(-3), Target: "cloud", MachineType: "g2-standard-8", VmReadyAt: Now.AddDays(-3)));
            settings.Values[SpendCapSettings.CapUsdKey] = "100";
            settings.Values[SpendCapSettings.HardStopKey] = "true";

            var summary = await ledger.MonthToDateAsync(CancellationToken.None);

            // The default run limit is 240 minutes plus the 15 minute grace the VM is created with: the VM cannot outlive it.
            summary.InProgressUsd.ShouldBe(4.25 * 1.015, 1e-9);
            (await ledger.CheckAsync(null, CancellationToken.None)).Level.ShouldBe(SpendCapLevel.None);
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

    private sealed class Pricing2(string json) : IPricingSource
    {
        public PricingLoadResult Load() => PricingTable.Parse(json);
    }

    [Fact]
    public async Task A_later_upsert_of_an_ended_row_keeps_the_first_booked_cost_even_if_the_price_list_changed()
    {
        var runs = new MemoryRuns();
        var (ledger, repo, settings, paths) = Make();
        using (paths)
        {
            await new LedgerRecordingRunRepository(runs, ledger).UpsertAsync(Finished("r1", 1), CancellationToken.None);
            var booked = (await repo.GetAllAsync(CancellationToken.None)).Single().UsdEst.ShouldNotBeNull();

            // The price doubles, and a writer upserts the row again from an older copy that carries no figure.
            var doubled = new SpendLedger(repo, new Pricing2(PricingJson.Replace("\"onDemandUsdPerHour\":1.0", "\"onDemandUsdPerHour\":2.0", StringComparison.Ordinal)), settings, new FixedTime(Now), TimeZoneInfo.Utc, runs);
            await new LedgerRecordingRunRepository(runs, doubled).UpsertAsync(Finished("r1", 1), CancellationToken.None);

            runs.Rows.Single().EstimatedCostUsd.ShouldNotBeNull().ShouldBe(booked, 1e-12, "the run row keeps what the ledger booked");
            (await repo.GetAllAsync(CancellationToken.None)).Single().UsdEst.ShouldNotBeNull().ShouldBe(booked, 1e-12);
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

    [Fact]
    public async Task A_terminal_upsert_reads_the_one_job_not_the_whole_table()
    {
        var runs = new MemoryRuns();
        var (ledger, _, _, paths) = Make();
        using (paths)
        {
            for (var i = 0; i < 5; i++)
            {
                runs.Rows.Add(Finished("other" + i, 1));
            }

            var decorated = new LedgerRecordingRunRepository(runs, ledger);
            runs.Rows.Add(Finished("r9", 1) with { Phase = JobPhase.Running, FinishedAt = null });
            var before = runs.GetAllCalls;

            await decorated.UpsertAsync(Finished("r9", 1), CancellationToken.None);

            runs.GetAllCalls.ShouldBe(before, "the stored-figure look is per job");
            runs.GetByJobIdCalls.ShouldBe(1);
        }
    }

    [Fact]
    public async Task An_upsert_carrying_a_different_figure_keeps_the_booked_one_on_the_row_so_row_and_ledger_agree()
    {
        var runs = new MemoryRuns();
        var (ledger, repo, _, paths) = Make();
        using (paths)
        {
            var decorated = new LedgerRecordingRunRepository(runs, ledger);
            await decorated.UpsertAsync(Finished("r7", 1), CancellationToken.None);
            var booked = runs.Rows.Single().EstimatedCostUsd.ShouldNotBeNull();

            await decorated.UpsertAsync(Finished("r7", 1) with { EstimatedCostUsd = 99, VmSeconds = 7 }, CancellationToken.None);

            runs.Rows.Single().EstimatedCostUsd.ShouldNotBeNull().ShouldBe(booked, 1e-12, "the ledger is first-write-wins, so the row is too");
            runs.Rows.Single().VmSeconds.ShouldBe(3600);
            (await repo.GetAllAsync(CancellationToken.None)).Single().UsdEst.ShouldNotBeNull().ShouldBe(booked, 1e-12);
        }
    }

    [Fact]
    public void A_carried_figure_whose_VM_seconds_disagree_with_the_rows_span_is_priced_again()
    {
        var (ledger, _, _, paths) = Make();
        using (paths)
        {
            var stale = Finished("re-end", 2) with { EstimatedCostUsd = 0.5, VmSeconds = 1800 };
            ledger.Quote(stale).ShouldNotBeNull().UsdEst.ShouldNotBeNull().ShouldBe(2 * 1.015, 1e-9, "a span of two hours is not the half hour the stale figure was booked for");

            var honest = Finished("same-span", 2) with { EstimatedCostUsd = 0.5, VmSeconds = 7200 };
            ledger.Quote(honest).ShouldNotBeNull().UsdEst.ShouldNotBeNull().ShouldBe(0.5, 1e-12, "a figure that matches its own span stays first-write-wins");
        }
    }

    [Fact]
    public async Task The_real_repository_reads_one_job_by_id()
    {
        using var paths = new TempPaths();
        var real = new RunRepository(new SqliteDatabase(paths.DatabasePath));
        await real.UpsertAsync(Finished("a", 1), CancellationToken.None);
        await real.UpsertAsync(Finished("b", 2), CancellationToken.None);

        (await real.GetByJobIdAsync("b", CancellationToken.None)).ShouldNotBeNull().JobId.ShouldBe("b");
        (await real.GetByJobIdAsync("missing", CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task The_decorator_keeps_the_phase_guard_a_Cancelled_row_is_not_overwritten_by_a_stale_Completed()
    {
        var (ledger, repo, _, paths) = Make();
        using (paths)
        {
            var real = new RunRepository(new SqliteDatabase(paths.DatabasePath));
            IRunRepository decorated = new LedgerRecordingRunRepository(real, ledger);
            await real.UpsertAsync(Finished("r1", 1, phase: JobPhase.Cancelled), CancellationToken.None);

            var written = await decorated.TryUpsertIfPhaseAsync(Finished("r1", 1, phase: JobPhase.Completed), JobPhase.Running, CancellationToken.None);

            written.ShouldBeFalse();
            (await real.GetAllAsync(CancellationToken.None)).Single().Phase.ShouldBe(JobPhase.Cancelled);
            (await repo.GetAllAsync(CancellationToken.None)).ShouldBeEmpty("the refused Completed books nothing");
        }
    }

    [Fact]
    public async Task The_decorator_books_a_conditional_write_that_succeeds_and_books_nothing_for_one_that_is_refused()
    {
        var (ledger, repo, _, paths) = Make();
        using (paths)
        {
            var real = new RunRepository(new SqliteDatabase(paths.DatabasePath));
            IRunRepository decorated = new LedgerRecordingRunRepository(real, ledger);
            await real.UpsertAsync(new RunRecord("r2", JobPhase.Running, Now.AddHours(-2), Target: "cloud", VmReadyAt: Now.AddHours(-1), MachineType: "g2-standard-8"), CancellationToken.None);

            (await decorated.TryUpsertIfPhaseAsync(Finished("r2", 1), JobPhase.Validating, CancellationToken.None)).ShouldBeFalse();
            (await repo.GetAllAsync(CancellationToken.None)).ShouldBeEmpty("a refused write books nothing");

            (await decorated.TryUpsertIfPhaseAsync(Finished("r2", 1), JobPhase.Running, CancellationToken.None)).ShouldBeTrue();
            (await repo.GetAllAsync(CancellationToken.None)).Count.ShouldBe(1);
            (await real.GetAllAsync(CancellationToken.None)).Single().EstimatedCostUsd.ShouldNotBeNull().ShouldBe(1.015, 1e-9);
        }
    }

    [Fact]
    public void The_decorator_declares_every_IRunRepository_member_itself_so_none_falls_back_to_an_interface_default()
    {
        var map = typeof(LedgerRecordingRunRepository).GetInterfaceMap(typeof(IRunRepository));
        map.InterfaceMethods.Length.ShouldBeGreaterThanOrEqualTo(3, "vacuity: the scan must see GetAll, Upsert and TryUpsertIfPhase");
        for (var i = 0; i < map.InterfaceMethods.Length; i++)
        {
            map.TargetMethods[i].DeclaringType.ShouldBe(typeof(LedgerRecordingRunRepository), $"{map.InterfaceMethods[i].Name} is inherited from the interface default, so the decorator silently drops its behaviour (#113 phase guard)");
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
