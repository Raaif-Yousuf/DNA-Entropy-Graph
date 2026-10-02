using DnaEntropyGraph.Cloud;
using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Cloud;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Cloud.Tests;

/// <summary>
/// Regressions from the cold review of #428/#258: a timeout that is not the
/// caller cancelling, stockouts that arrive as 503, cleanup behind an open
/// breaker, and a Network error that must not be re-read as a stockout.
/// Every run must end in a recorded terminal state (Hard Rule 11).
/// </summary>
public class ResilienceRegressionTests
{
    private const string Project = "my-project";

    private sealed class Rig
    {
        public Rig(FakeGcp gcp, CloudRetryOptions options)
        {
            Gcp = gcp;
            Log = new CloudRetryLog();
            Pipeline = new CloudCallPipeline(options, gcp, Log);
            Repo = new InMemoryRunRepository();
            Runner = new CloudJobRunner(
                new ResilientComputeGateway(gcp, Pipeline),
                new ResilientStorageGateway(gcp, Pipeline),
                new ResilientProjectSetupGateway(gcp, Pipeline),
                new ResilientQuotaGateway(gcp, Pipeline),
                Repo);
        }

        public FakeGcp Gcp { get; }

        public CloudRetryLog Log { get; }

        public CloudCallPipeline Pipeline { get; }

        public InMemoryRunRepository Repo { get; }

        public CloudJobRunner Runner { get; }
    }

    private static CloudRetryOptions Fast(int retries = 4) => new() { MaxRetryAttempts = retries, BaseDelay = TimeSpan.Zero, MaxDelay = TimeSpan.Zero };

    private static VmSpec Spec(string jobId) => new(
        Project, "install-1", jobId, "evo2_7b", "0.1.0", "stop", "g2-standard-8", TimeSpan.FromHours(1), "DELETE");

    private static CloudJobRequest Request(string jobId, params string[] zones) => new(
        jobId, Spec(jobId), zones.Length == 0 ? ["us-central1-a"] : zones, [], [], AfterTaskAction.Stop);

    // ---- 1. a timeout is not the caller cancelling -------------------------------------

    [Fact]
    public async Task A_timeout_while_the_caller_token_is_live_ends_the_run_Failed_with_a_network_code()
    {
        var gcp = new FakeGcp().WithCallTimeouts(1);
        var repo = new InMemoryRunRepository();
        var runner = new CloudJobRunner(gcp, gcp, gcp, gcp, repo);

        var result = await runner.RunAsync(Request("job-t1"), CancellationToken.None);

        result.FinalPhase.ShouldBe(JobPhase.Failed);
        result.FailureKind.ShouldBe(CloudErrorKind.Network);
        result.FailureCode.ShouldBe("network");
        var row = repo.AllRecordedInOrder.Last(r => r.JobId == "job-t1");
        row.Phase.ShouldBe(JobPhase.Failed);
        row.ErrorCode.ShouldBe("network");
    }

    [Fact]
    public async Task The_pipeline_retries_a_timeout_and_the_run_completes()
    {
        var rig = new Rig(new FakeGcp().WithCallTimeouts(2), Fast());

        var result = await rig.Runner.RunAsync(Request("job-t2"), CancellationToken.None);

        result.FinalPhase.ShouldBe(JobPhase.Completed);
        rig.Log.Retries.Count.ShouldBe(2);
    }

    [Fact]
    public async Task A_cancelled_caller_token_still_propagates_instead_of_being_recorded_as_a_failure()
    {
        var gcp = new FakeGcp();
        var runner = new CloudJobRunner(gcp, gcp, gcp, gcp, new InMemoryRunRepository());
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => runner.RunAsync(Request("job-t3"), cts.Token));
    }

    [Fact]
    public async Task Cancelling_during_the_backoff_wait_stops_the_call_promptly()
    {
        // A one hour backoff: the only way this test finishes in time is the
        // wait honouring the caller's token (an already-cancelled token would
        // pass even if it did not).
        var options = new CloudRetryOptions { MaxRetryAttempts = 3, BaseDelay = TimeSpan.FromHours(1), MaxDelay = TimeSpan.FromHours(1) };
        var rig = new Rig(new FakeGcp().WithTransientFailures(503, 100), options);
        using var cts = new CancellationTokenSource();

        var call = new ResilientComputeGateway(rig.Gcp, rig.Pipeline).GetVmAsync("v", "us-central1-a", cts.Token);
        SpinWait.SpinUntil(() => rig.Log.Retries.Count == 1, TimeSpan.FromSeconds(5)).ShouldBeTrue("the first attempt should have failed and started waiting");
        await cts.CancelAsync();

        var ex = await Record.ExceptionAsync(async () => await call.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None));
        ex.ShouldBeAssignableTo<OperationCanceledException>();
    }

    // ---- 2. stockout, quota and the like are not "the network is down" -----------------

    [Fact]
    public async Task A_four_zone_ladder_of_stockout_shaped_503s_is_a_stockout_not_an_offline_breaker()
    {
        var gcp = new FakeGcp().WithCreateFailure(
            new CloudError("ZONE_RESOURCE_POOL_EXHAUSTED", 503, "The zone does not have enough resources available to fulfill the request."),
            100);
        var options = Fast() with { BreakerMinimumThroughput = 6, BreakerFailureRatio = 0.8 };
        var rig = new Rig(gcp, options);

        var result = await rig.Runner.RunAsync(Request("job-s1", "us-central1-a", "us-central1-b", "us-central1-c", "us-central1-f"), CancellationToken.None);

        result.FinalPhase.ShouldBe(JobPhase.Failed);
        result.FailureKind.ShouldBe(CloudErrorKind.Stockout);
        rig.Pipeline.IsOffline.ShouldBeFalse();
        rig.Log.Retries.ShouldBeEmpty();
        gcp.CreateAttempts.ShouldBe(4);
        // The breaker is closed: the next call still goes through.
        (await rig.Runner.GetPhaseAsync("job-s1", CancellationToken.None)).ShouldBe(JobPhase.Failed);
        (await new ResilientComputeGateway(gcp, rig.Pipeline).FindByJobIdAsync("job-s1", CancellationToken.None)).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(503, "QUOTA_EXCEEDED", "Quota exceeded for quota metric")]
    [InlineData(503, "BILLING_DISABLED", "The billing account for the owning project is disabled.")]
    [InlineData(500, "IAM_PERMISSION_DENIED", "Required permission denied")]
    [InlineData(503, "CONDITION_NOT_MET", "Operation denied by org policy on constraints/compute.requireOsLogin")]
    public async Task A_5xx_that_classifies_as_a_permanent_kind_is_not_retried(int status, string code, string message)
    {
        var rig = new Rig(new FakeGcp().WithTransientFailures(status, 5, message, code), Fast());

        await Should.ThrowAsync<CloudOperationException>(
            () => new ResilientComputeGateway(rig.Gcp, rig.Pipeline).GetVmAsync("v", "us-central1-a", CancellationToken.None));

        rig.Log.Retries.ShouldBeEmpty();
        rig.Pipeline.IsOffline.ShouldBeFalse();
    }

    // ---- 3. cleanup must not sit behind the breaker -------------------------------------

    private static async Task<Rig> RigWithOpenBreakerAndALiveVmAsync(string jobId)
    {
        var options = Fast(retries: 1) with
        {
            BreakerMinimumThroughput = 2,
            BreakerFailureRatio = 0.5,
            BreakDuration = TimeSpan.FromMinutes(10),
            BreakerSamplingDuration = TimeSpan.FromSeconds(30),
        };
        var gcp = new FakeGcp();
        var rig = new Rig(gcp, options);
        await gcp.CreateVmAsync(Spec(jobId), "us-central1-a", CancellationToken.None);
        await rig.Repo.UpsertAsync(new RunRecord(jobId, JobPhase.Running, DateTimeOffset.UtcNow), CancellationToken.None);

        gcp.WithTransientFailures(503, 2);
        await Should.ThrowAsync<CloudOperationException>(
            () => new ResilientComputeGateway(gcp, rig.Pipeline).GetVmAsync("x", "us-central1-a", CancellationToken.None));
        rig.Pipeline.IsOffline.ShouldBeTrue();
        return rig;
    }

    [Fact]
    public async Task Cancel_deletes_the_VM_and_records_Cancelled_even_while_the_breaker_is_open()
    {
        var rig = await RigWithOpenBreakerAndALiveVmAsync("job-c1");

        await rig.Runner.CancelAsync("job-c1", CancellationToken.None);

        (await rig.Runner.GetPhaseAsync("job-c1", CancellationToken.None)).ShouldBe(JobPhase.Cancelled);
        (await rig.Gcp.FindByJobIdAsync("job-c1", CancellationToken.None)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Stop_and_delete_by_job_id_also_bypass_the_open_breaker()
    {
        var rig = await RigWithOpenBreakerAndALiveVmAsync("job-c2");

        await rig.Runner.StopVmAsync("job-c2", CancellationToken.None);
        (await rig.Gcp.FindByJobIdAsync("job-c2", CancellationToken.None)).Single().Status.ShouldBe("STOPPED");

        await rig.Runner.DeleteVmAsync("job-c2", CancellationToken.None);
        (await rig.Gcp.FindByJobIdAsync("job-c2", CancellationToken.None)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_cancel_whose_cleanup_fails_still_ends_in_a_recorded_Failed_state()
    {
        var gcp = new FakeGcp();
        var repo = new InMemoryRunRepository();
        var runner = new CloudJobRunner(gcp, gcp, gcp, gcp, repo);
        await gcp.CreateVmAsync(Spec("job-c3"), "us-central1-a", CancellationToken.None);
        await repo.UpsertAsync(new RunRecord("job-c3", JobPhase.Running, DateTimeOffset.UtcNow), CancellationToken.None);
        gcp.WithTransientFailures(400, 1, "Bad request");

        await runner.CancelAsync("job-c3", CancellationToken.None);

        var row = repo.AllRecordedInOrder.Last(r => r.JobId == "job-c3");
        row.Phase.ShouldBe(JobPhase.Failed);
        row.ErrorCode.ShouldBe("cancel_failed");
    }

    // ---- 4. the pipeline's Network kind is kept, and aborts the ladder ------------------

    [Fact]
    public async Task Exhausted_retries_abort_the_ladder_as_Network_instead_of_trying_more_zones_and_reporting_Stockout()
    {
        var gcp = new FakeGcp().WithCreateFailure(new CloudError(null, 503, "The service is currently unavailable."), 100);
        var rig = new Rig(gcp, Fast(retries: 1));

        var result = await rig.Runner.RunAsync(Request("job-n1", "us-central1-a", "us-central1-b", "us-central1-c"), CancellationToken.None);

        result.FinalPhase.ShouldBe(JobPhase.Failed);
        result.FailureKind.ShouldBe(CloudErrorKind.Network);
        result.FailureCode.ShouldBe("network");
        gcp.CreateAttempts.ShouldBe(2, "one try and one retry in the first zone, then the ladder stops");
    }

    [Fact]
    public async Task An_open_breaker_during_provisioning_is_reported_as_Network()
    {
        var options = Fast(retries: 0) with { BreakerMinimumThroughput = 2, BreakerFailureRatio = 0.5, BreakDuration = TimeSpan.FromMinutes(10) };
        var gcp = new FakeGcp().WithCreateFailure(new CloudError(null, 503, "The service is currently unavailable."), 100);
        var rig = new Rig(gcp, options);

        var result = await rig.Runner.RunAsync(Request("job-n2", "us-central1-a", "us-central1-b", "us-central1-c"), CancellationToken.None);

        result.FailureKind.ShouldBe(CloudErrorKind.Network);
    }

    // ---- 5. failures are recorded as codes, with the raw text only as detail -----------

    [Fact]
    public async Task A_preflight_failure_records_a_code_and_keeps_the_raw_text_as_detail()
    {
        var gcp = new FakeGcp().WithBillingOff(Project);
        var repo = new InMemoryRunRepository();
        var runner = new CloudJobRunner(gcp, gcp, gcp, gcp, repo);

        var result = await runner.RunAsync(Request("job-e1"), CancellationToken.None);

        result.FailureCode.ShouldBe("billing");
        var row = repo.AllRecordedInOrder.Last(r => r.JobId == "job-e1");
        row.ErrorCode.ShouldBe("billing");
        row.ErrorDetail.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task An_unexpected_exception_is_recorded_as_the_generic_code_with_the_raw_text_only_as_detail()
    {
        var gcp = new FakeGcp();
        var repo = new InMemoryRunRepository();
        var runner = new CloudJobRunner(gcp, gcp, gcp, gcp, repo);
        var bad = Request("job-e2") with { Spec = Spec("job-e2") with { InstallationId = "Not A Label" } };

        var result = await runner.RunAsync(bad, CancellationToken.None);

        result.FailureCode.ShouldBe("other");
        var row = repo.AllRecordedInOrder.Last(r => r.JobId == "job-e2");
        row.ErrorCode.ShouldBe("other");
        row.ErrorDetail!.ShouldContain("installation-id");
    }
}
