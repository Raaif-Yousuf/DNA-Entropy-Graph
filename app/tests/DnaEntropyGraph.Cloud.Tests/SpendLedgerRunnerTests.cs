using DnaEntropyGraph.Cloud;
using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Cloud;
using DnaEntropyGraph.Core.Cost;
using DnaEntropyGraph.Core.Runs;
using DnaEntropyGraph.Persistence;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Cloud.Tests;

/// <summary>Issue #109 round 3: what the runner hands the spend ledger, observed through the real runner on <see cref="FakeGcp"/> and the real <see cref="SpendLedger"/>.</summary>
public class SpendLedgerRunnerTests
{
    private const string Project = "my-project";
    private const string Zone = "us-central1-a";

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
        public string? GetString(string key) => null;

        public void SetString(string key, string value)
        {
        }
    }

    private sealed class MemoryLedger : ICostLedgerRepository
    {
        private readonly List<CostLedgerEntry> _rows = [];

        public Task<IReadOnlyList<CostLedgerEntry>> GetAllAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<CostLedgerEntry>>(_rows.ToList());

        public Task<CostLedgerEntry?> TryAppendAsync(CostLedgerEntry entry, CancellationToken cancellationToken)
        {
            var stored = entry with { Id = _rows.Count + 1 };
            _rows.Add(stored);
            return Task.FromResult<CostLedgerEntry?>(stored);
        }

        public Task<CostLedgerEntry> AppendAsync(CostLedgerEntry entry, CancellationToken cancellationToken)
        {
            _rows.Add(entry);
            return Task.FromResult(entry);
        }
    }

    private static VmSpec Spec(string jobId, bool flex = false) => new(
        ProjectId: Project,
        InstallationId: "install-1",
        JobId: jobId,
        Model: "evo2_7b",
        AppVersion: "0.1.0",
        Lifecycle: "stop",
        MachineType: "g2-standard-8",
        MaxRunDuration: TimeSpan.FromHours(4),
        TerminationAction: "DELETE")
    {
        FlexStart = flex,
        FlexStartWait = flex ? TimeSpan.FromHours(2) : null,
    };

    private static CloudJobRunner Runner(FakeGcp gcp, InMemoryRunRepository repo)
        => new(gcp, gcp, gcp, gcp, gcp, repo, null)
        {
            ResultPollInterval = TimeSpan.FromMilliseconds(1),
            ResultTimeout = TimeSpan.FromSeconds(5),
            CallTimeout = TimeSpan.FromSeconds(1),
            EnsureBucketTimeout = TimeSpan.FromSeconds(1),
            CreateTimeout = TimeSpan.FromSeconds(1),
            CreateSettleTimeout = TimeSpan.FromMilliseconds(80),
            UploadTimeout = TimeSpan.FromSeconds(2),
            BootTimeout = TimeSpan.FromSeconds(2),
            LifecycleTimeout = TimeSpan.FromSeconds(2),
            LifecyclePollInterval = TimeSpan.FromMilliseconds(1),
        };

    private static RunRecord LastRow(InMemoryRunRepository repo, string jobId) => repo.AllRecordedInOrder.Last(r => r.JobId == jobId);

    [Fact]
    public async Task A_granted_flex_VM_whose_run_is_still_Provisioning_is_counted_toward_the_hard_stop()
    {
        // Both clocks pinned: the platform stamps the VM at T0, the ledger reads "now" ten minutes later, so a positive figure never depends on which of two real clocks was read first.
        var platformClock = new ManualTimeProvider(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero));
        var ledgerClock = new ManualTimeProvider(platformClock.GetUtcNow().AddMinutes(10));
        var gcp = new FakeGcp(platformClock).WithFlexStartGrantedAfter(TimeSpan.FromMilliseconds(50));
        var repo = new InMemoryRunRepository();
        var flexOptions = RunOptionsJson.Serialize(new RunOptions { ModelId = "evo2_7b", RunTarget = "Cloud", WaitForGpu = true });
        await repo.UpsertAsync(new RunRecord("job-l1", JobPhase.Uploading, DateTimeOffset.UtcNow, Target: "cloud", MachineType: "g2-standard-8", OptionsJson: flexOptions), CancellationToken.None);
        var seen = new List<double>();
        repo.BeforeUpsert = async (next, token) =>
        {
            if (next.JobId == "job-l1" && next.Phase == JobPhase.Provisioning && next.VmReadyAt is not null)
            {
                // The row as it is about to be stored: the create was answered (a start is recorded), the phase has not left Provisioning.
                var snapshot = new InMemoryRunRepository();
                foreach (var row in repo.AllRecordedInOrder)
                {
                    await snapshot.UpsertAsync(row, token);
                }

                await snapshot.UpsertAsync(next, token);
                var ledger = new SpendLedger(new MemoryLedger(), new Pricing(), new Settings(), ledgerClock, TimeZoneInfo.Utc, snapshot);
                seen.Add((await ledger.MonthToDateAsync(token)).InProgressUsd);
            }
        };

        var result = await Runner(gcp, repo).RunAsync(TestInputs.Request("job-l1", Spec("job-l1", flex: true)), CancellationToken.None);

        result.FinalPhase.ShouldBe(JobPhase.Completed, result.FailureMessage);
        seen.ShouldNotBeEmpty("the runner stored a Provisioning row that already carries the VM's start");
        seen.ShouldAllBe(usd => usd > 0, "a granted flex VM bills from the grant; its phase being Provisioning must not hide it from the hard stop");
    }

    private static async Task<(FakeGcp Gcp, ManualTimeProvider Time, InMemoryRunRepository Repo)> CancelRigAsync(string jobId, TimeSpan vmRanFor, bool stopped)
    {
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow.AddHours(-10));
        var gcp = new FakeGcp(time);
        var repo = new InMemoryRunRepository();
        await gcp.CreateVmAsync(Spec(jobId), Zone, CancellationToken.None);
        time.Advance(vmRanFor);
        if (stopped)
        {
            await gcp.StopVmAsync($"deg-{jobId}", Zone, CancellationToken.None);
        }

        time.Advance(TimeSpan.FromHours(1)); // the app was closed for a while after that
        await repo.UpsertAsync(new RunRecord(jobId, JobPhase.Cancelling, DateTimeOffset.UtcNow.AddHours(-10)), CancellationToken.None);
        return (gcp, time, repo);
    }

    [Fact]
    public async Task A_cancel_in_a_fresh_process_books_from_the_VMs_own_start_it_found()
    {
        // M3: a Cancelling row resumed after a restart has no create of this process in flight, so nothing remembers a landing; the VM the delete found says when it started.
        var (gcp, time, repo) = await CancelRigAsync("job-m3", TimeSpan.FromMinutes(30), stopped: false);
        var vmStart = time.GetUtcNow() - TimeSpan.FromMinutes(90);

        await Runner(gcp, repo).CancelAsync("job-m3", CancellationToken.None);

        var row = LastRow(repo, "job-m3");
        row.Phase.ShouldBe(JobPhase.Cancelled);
        row.VmReadyAt.ShouldBe(vmStart, "the VM billed from its platform start, not from the moment the cancel looked");
        row.FinishedAt.ShouldNotBeNull();
        (DateTimeOffset.UtcNow - row.FinishedAt!.Value).ShouldBeLessThan(TimeSpan.FromMinutes(1), "a VM still running when the cancel found it billed until the delete");
    }

    [Fact]
    public async Task A_cancel_that_finds_an_already_stopped_VM_books_until_it_stopped()
    {
        var (gcp, time, repo) = await CancelRigAsync("job-m3b", TimeSpan.FromMinutes(30), stopped: true);
        var vmStart = time.GetUtcNow() - TimeSpan.FromMinutes(90);

        await Runner(gcp, repo).CancelAsync("job-m3b", CancellationToken.None);

        var row = LastRow(repo, "job-m3b");
        row.VmReadyAt.ShouldBe(vmStart);
        row.FinishedAt.ShouldBe(vmStart + TimeSpan.FromMinutes(30), "the VM stopped at +30 min; the cancel an hour later must not book that hour");
    }

    private static async Task<(FakeGcp Gcp, InMemoryRunRepository Repo, DateTimeOffset T0)> SelfDeletedAfterResultAsync(string jobId, DateTimeOffset? heartbeat, string? resultFinishedAt)
    {
        var t0 = DateTimeOffset.UtcNow.AddHours(-10);
        var time = new ManualTimeProvider(t0);
        var gcp = new FakeGcp(time);
        var repo = new InMemoryRunRepository();
        await gcp.CreateVmAsync(Spec(jobId), Zone, CancellationToken.None);
        time.Advance(TimeSpan.FromMinutes(50));
        await gcp.DeleteVmAsync($"deg-{jobId}", Zone, CancellationToken.None); // the VM deleted itself after the worker finished
        var track = FakeGcp.TrackBytes("seq");
        var bucket = FakeGcp.BucketName(Project);
        gcp.PutObject(bucket, $"jobs/{jobId}/output/seq/seq.bedgraph", track);
        var finished = resultFinishedAt is null ? string.Empty : $",\"finishedAt\":\"{resultFinishedAt}\"";
        gcp.PutObject(bucket, $"jobs/{jobId}/result.json", System.Text.Encoding.UTF8.GetBytes(
            $$"""{"schema":1,"jobId":"{{jobId}}","status":"done"{{finished}},"inputs":[{"id":"in1","status":"done","outputs":["output/seq/seq.bedgraph"],"files":[{"path":"output/seq/seq.bedgraph","sha256":"{{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(track))}}","bytes":{{track.Length}}}],"notices":[],"stats":null,"error":null}],"error":null}"""));
        await repo.UpsertAsync(new RunRecord(jobId, JobPhase.Running, t0, Target: "cloud", VmReadyAt: t0, LastHeartbeatAt: heartbeat), CancellationToken.None);
        return (gcp, repo, t0);
    }

    [Fact]
    public async Task A_VM_that_deleted_itself_is_booked_until_the_last_time_the_app_knew_it_was_alive_not_until_the_app_came_back()
    {
        // M1: the app was closed for ten hours; the VM was gone when it looked.
        var t0 = DateTimeOffset.UtcNow.AddHours(-10);
        var (gcp, repo, _) = await SelfDeletedAfterResultAsync("job-m1", t0.AddMinutes(40), null);

        var result = await Runner(gcp, repo).RunAsync(TestInputs.Request("job-m1", Spec("job-m1")), CancellationToken.None);

        result.FinalPhase.ShouldBe(JobPhase.Completed, result.FailureMessage);
        var row = LastRow(repo, "job-m1");
        row.FinishedAt.ShouldNotBeNull();
        (row.FinishedAt!.Value - row.VmReadyAt!.Value).TotalMinutes.ShouldBe(40, 0.01, "booked until the last heartbeat the row knew, not the ten hours the app was closed");
    }

    [Fact]
    public async Task A_VM_that_deleted_itself_is_booked_until_the_results_own_finish_time_when_that_is_later_than_the_last_heartbeat()
    {
        var t0 = DateTimeOffset.UtcNow.AddHours(-10);
        var (gcp, repo, _) = await SelfDeletedAfterResultAsync("job-m1b", t0.AddMinutes(40), t0.AddMinutes(48).ToString("O"));

        var result = await Runner(gcp, repo).RunAsync(TestInputs.Request("job-m1b", Spec("job-m1b")), CancellationToken.None);

        result.FinalPhase.ShouldBe(JobPhase.Completed, result.FailureMessage);
        var row = LastRow(repo, "job-m1b");
        (row.FinishedAt!.Value - row.VmReadyAt!.Value).TotalMinutes.ShouldBe(48, 0.01);
    }

    [Fact]
    public async Task A_VM_that_deleted_itself_with_no_sign_of_life_at_all_is_booked_no_longer_than_its_own_run_limit()
    {
        // The app was closed for ten hours; no heartbeat, no result finish time, no status.json. The platform cannot have run the VM past its maxRunDuration (4 h in Spec).
        var (gcp, repo, t0) = await SelfDeletedAfterResultAsync("job-n1", null, null);

        var result = await Runner(gcp, repo).RunAsync(TestInputs.Request("job-n1", Spec("job-n1")), CancellationToken.None);

        result.FinalPhase.ShouldBe(JobPhase.Completed, result.FailureMessage);
        LastRow(repo, "job-n1").FinishedAt.ShouldBe(t0 + TimeSpan.FromHours(4), "no evidence: capped at start + the run's own limit, never the ten hours the app was closed");
    }

    [Fact]
    public async Task A_create_that_lands_after_the_run_ended_books_nothing_but_the_VM_is_found_by_its_labels()
    {
        // L1 (recorded, no booking change): the run ended before the create landed, so the row never learned a start. The VM is not lost: it carries the
        // standard labels, which is how the left-running scan (ListByInstallationAsync) and the Cloud page discover it.
        var landing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gcp = new FakeGcp().WithCreateDelay(TimeSpan.FromMilliseconds(55));
        gcp.CreateLandingGate = landing.Task;
        var repo = new InMemoryRunRepository();
        var runner = Runner(gcp, repo);

        var result = await runner.RunAsync(TestInputs.Request("job-l4", Spec("job-l4")), CancellationToken.None);
        result.FinalPhase.ShouldBe(JobPhase.Failed);
        landing.TrySetResult();
        await gcp.WhenDelayedCreateLandedAsync().WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);

        LastRow(repo, "job-l4").VmReadyAt.ShouldBeNull();
        var listed = await gcp.ListByInstallationAsync("install-1", CancellationToken.None);
        listed.ShouldContain(v => v.Name == "deg-job-l4" && v.Labels != null && v.Labels["job-id"] == "job-l4");
    }

    [Fact]
    public async Task A_resume_that_adopts_a_VM_which_already_stopped_books_until_it_stopped_not_until_the_app_came_back()
    {
        var t0 = DateTimeOffset.UtcNow.AddHours(-10);
        var time = new ManualTimeProvider(t0);
        var gcp = new FakeGcp(time);
        var repo = new InMemoryRunRepository();
        var request = TestInputs.Request("job-l5", Spec("job-l5"));
        await gcp.CreateVmAsync(request.Spec, Zone, CancellationToken.None);
        time.Advance(TimeSpan.FromMinutes(20));
        await gcp.StopVmAsync("deg-job-l5", Zone, CancellationToken.None);
        await repo.UpsertAsync(new RunRecord("job-l5", JobPhase.Provisioning, DateTimeOffset.UtcNow.AddHours(-10)), CancellationToken.None);

        await Runner(gcp, repo).RunAsync(request, CancellationToken.None);

        var row = LastRow(repo, "job-l5");
        row.VmReadyAt.ShouldBe(t0);
        (row.FinishedAt!.Value - row.VmReadyAt!.Value).TotalMinutes.ShouldBe(20, 0.01, "the VM stopped at +20 min; the run ended hours later, but nothing billed in between");
    }
}
