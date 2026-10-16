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

    private static readonly DateTimeOffset Now = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    private static RunRecord Finished(string id, double hours, bool spot = false, JobPhase phase = JobPhase.Completed, string? machine = "g2-standard-8")
        => new(id, phase, Now.AddHours(-hours), Target: "cloud", StartedAt: Now.AddHours(-hours), FinishedAt: Now, MachineType: machine, IsSpot: spot);

    private static (SpendLedger Ledger, CostLedgerRepository Repo, Settings Settings, TempPaths Paths) Make()
    {
        var paths = new TempPaths();
        var repo = new CostLedgerRepository(new SqliteDatabase(paths.DatabasePath));
        var settings = new Settings();
        return (new SpendLedger(repo, new Pricing(), settings, new FixedTime(Now), TimeZoneInfo.Utc), repo, settings, paths);
    }

    [Fact]
    public async Task A_finished_run_appends_one_vm_runtime_row_priced_from_minutes_rate_and_disk()
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
            row.EndedUtc.ShouldBe(Now);
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

    [Theory]
    [InlineData(JobPhase.Running)]
    [InlineData(JobPhase.Provisioning)]
    [InlineData(JobPhase.Draft)]
    public async Task A_run_that_has_not_ended_writes_nothing(JobPhase phase)
    {
        var (ledger, repo, _, paths) = Make();
        using (paths)
        {
            await ledger.RecordRunAsync(Finished("r1", 1, phase: phase), CancellationToken.None);

            (await repo.GetAllAsync(CancellationToken.None)).ShouldBeEmpty();
        }
    }

    [Fact]
    public async Task A_run_with_no_machine_type_never_had_a_VM_so_writes_nothing()
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

            (await ledger.MonthToDateUsdAsync(CancellationToken.None)).ShouldBe(3.5, 1e-9);
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
