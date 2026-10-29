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
        var gcp = new FakeGcp().WithFlexStartGrantedAfter(TimeSpan.FromMilliseconds(50));
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
                var ledger = new SpendLedger(new MemoryLedger(), new Pricing(), new Settings(), TimeProvider.System, TimeZoneInfo.Utc, snapshot);
                seen.Add((await ledger.MonthToDateAsync(token)).InProgressUsd);
            }
        };

        var result = await Runner(gcp, repo).RunAsync(TestInputs.Request("job-l1", Spec("job-l1", flex: true)), CancellationToken.None);

        result.FinalPhase.ShouldBe(JobPhase.Completed, result.FailureMessage);
        seen.ShouldNotBeEmpty("the runner stored a Provisioning row that already carries the VM's start");
        seen.ShouldAllBe(usd => usd > 0, "a granted flex VM bills from the grant; its phase being Provisioning must not hide it from the hard stop");
    }

    [Fact]
    public async Task A_cancel_whose_create_is_still_unsettled_and_found_no_VM_books_no_span()
    {
        var gcp = new FakeGcp().WithBlockedCreate(ignoreCancellation: true);
        var repo = new InMemoryRunRepository();
        var runner = Runner(gcp, repo);
        using var cts = new CancellationTokenSource();
        var run = runner.RunAsync(TestInputs.Request("job-l2", Spec("job-l2")), cts.Token);
        await gcp.CreateVmEntered.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await cts.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(() => run);

        await runner.CancelAsync("job-l2", CancellationToken.None);

        var row = LastRow(repo, "job-l2");
        row.VmReadyAt.ShouldBeNull("the create never answered and the cancel's delete found no VM: a create that later fails must not leave a booked span");
        gcp.ReleaseCreate();
    }

    [Fact]
    public async Task The_cancels_own_delete_reports_a_VM_it_found()
    {
        var gcp = new FakeGcp();
        var spec = Spec("job-l3");
        await gcp.CreateVmAsync(spec, Zone, CancellationToken.None);
        var settings = new CloudRunSettings { LifecyclePollInterval = TimeSpan.FromMilliseconds(1), LifecycleTimeout = TimeSpan.FromSeconds(2) };
        var calls = new GatewayCalls(settings, gcp);
        var terminator = new VmTerminator(gcp, calls, settings);
        var found = false;

        var none = await terminator.DeleteOwnedVmsAndVerifyAsync("job-nobody", true, CancellationToken.None, () => found = true);
        none.ShouldBeNull();
        found.ShouldBeFalse("no VM carries that job id");

        var note = await terminator.DeleteOwnedVmsAndVerifyAsync("job-l3", true, CancellationToken.None, () => found = true);
        note.ShouldBeNull();
        found.ShouldBeTrue();
    }

    [Fact]
    public void An_unsettled_create_gives_a_start_only_when_the_cancel_found_a_VM()
    {
        var settings = new CloudRunSettings();
        var gcp = new FakeGcp();
        var calls = new GatewayCalls(settings, gcp);
        var provisioner = new VmProvisioner(gcp, calls, settings, new VmTerminator(gcp, calls, settings));

        provisioner.TakeVmStartForCancel("j", settled: false, vmFoundByCancel: false).ShouldBeNull();
        provisioner.TakeVmStartForCancel("j", settled: false, vmFoundByCancel: true).ShouldNotBeNull();
        provisioner.TakeVmStartForCancel("j", settled: true, vmFoundByCancel: false).ShouldBeNull("a settled create with no landing made no VM");
    }

    [Fact]
    public async Task A_create_that_lands_after_the_run_ended_leaves_no_start_for_a_later_cancel_to_adopt()
    {
        var landing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gcp = new FakeGcp().WithCreateDelay(TimeSpan.FromMilliseconds(55));
        gcp.CreateLandingGate = landing.Task;
        var repo = new InMemoryRunRepository();
        var runner = Runner(gcp, repo);

        var result = await runner.RunAsync(TestInputs.Request("job-l4", Spec("job-l4")), CancellationToken.None);
        result.FinalPhase.ShouldBe(JobPhase.Failed);
        landing.TrySetResult();
        await gcp.WhenDelayedCreateLandedAsync().WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        await Task.Delay(100, TestContext.Current.CancellationToken);

        runner.LandedCreateCount.ShouldBe(0, "the run ended terminal; an entry added afterwards would be adopted by anything later asking about this job");
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