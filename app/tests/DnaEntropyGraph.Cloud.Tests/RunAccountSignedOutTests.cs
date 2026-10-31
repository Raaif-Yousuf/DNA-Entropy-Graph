using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Cloud;
using DnaEntropyGraph.Core.Contract;
using DnaEntropyGraph.Core.Runs;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Cloud.Tests;

/// <summary>
/// Issue #830 review M6: a run whose Google account is signed out says so by name. A cancel, a re-download and a delete of the cloud copy used to end as
/// "other" / "Failed" with the Cloud page as the action, although the Cloud page lists the SELECTED account; each now ends in a sign-in outcome naming that action.
/// </summary>
public sealed class RunAccountSignedOutTests
{
    private const string Project = "my-project";
    private static readonly CloudScope ScopeA = new(Project, "sub-a");

    private static FakeGcp GcpWithSignedOutAccountA()
        => new FakeGcp().WithAccount("sub-a", "a@example.org", Project).WithAccountSignedOut("sub-a");

    [Fact]
    public async Task A_cancel_of_a_run_whose_account_is_signed_out_is_recorded_as_needing_that_sign_in_not_as_other()
    {
        var gcp = GcpWithSignedOutAccountA();
        var repo = new InMemoryRunRepository();
        await repo.UpsertAsync(new RunRecord("job-so", JobPhase.Running, DateTimeOffset.UtcNow, Target: "cloud", ProjectId: Project, AccountSub: "sub-a"), CancellationToken.None);
        var runner = new CloudJobRunner(gcp, gcp, gcp, gcp, gcp, repo);

        await runner.CancelAsync(ScopeA, "job-so", CancellationToken.None);

        var row = repo.AllRecordedInOrder.Last(r => r.JobId == "job-so");
        row.Phase.ShouldBe(JobPhase.Failed);
        row.ErrorCode.ShouldBe(RunErrorCodes.AccountSignedOut, row.ErrorDetail);
        row.ErrorDetail.ShouldNotBeNull();
        row.ErrorDetail.ShouldNotContain("(other)");
        row.ErrorDetail.ShouldContain("Sign in");
        row.ErrorDetail.ShouldNotContain("Cloud page");
    }

    [Fact]
    public void The_sign_in_code_has_an_action_a_resw_message_and_is_in_the_roster()
    {
        RunErrorCodes.All.ShouldContain(RunErrorCodes.AccountSignedOut);
        RunErrorCodes.ResourceKey(RunErrorCodes.AccountSignedOut).ShouldBe("RunError_account_signed_out");
        RunErrorActions.TryGet(RunErrorCodes.AccountSignedOut, out var action).ShouldBeTrue();
        action.ShouldBe(RunErrorAction.SignIn);
    }

    private static RunRecord CloudCopyRow() => new(
        "job-so",
        JobPhase.Completed,
        DateTimeOffset.UtcNow,
        Target: "cloud",
        ProjectId: Project,
        AccountSub: "sub-a",
        Bucket: "deg-bucket",
        JobPrefix: WorkerManifestBuilder.JobPrefix("job-so"),
        OutputDir: Path.Combine(Path.GetTempPath(), "deg-signed-out-" + Guid.NewGuid().ToString("N")));

    private static RunCloudResults Results(FakeGcp gcp, InMemoryRunRepository repo)
        => new(gcp, new SignedOutDeleter(), repo, () => Path.GetTempPath(), TimeProvider.System);

    private sealed class SignedOutDeleter : IJobObjectDeleter
    {
        public bool IsAvailable => true;

        public Task DeleteJobObjectsAsync(CloudScope scope, string bucket, string jobPrefix, CancellationToken cancellationToken)
            => Task.FromException(new AccountAuthException(AuthErrorCodes.SigninExpired, "signed out", accountSub: scope.AccountSub));
    }

    [Fact]
    public async Task A_redownload_for_a_run_whose_account_is_signed_out_is_a_sign_in_status_not_a_generic_failure()
    {
        var gcp = GcpWithSignedOutAccountA();
        var results = Results(gcp, new InMemoryRunRepository());

        var outcome = await results.RedownloadAsync(CloudCopyRow(), CancellationToken.None);

        outcome.Status.ShouldBe(CloudResultsStatus.SignInNeeded);
    }

    [Fact]
    public async Task A_cloud_copy_delete_for_a_run_whose_account_is_signed_out_is_a_sign_in_status_and_the_row_is_unchanged()
    {
        var gcp = GcpWithSignedOutAccountA();
        var repo = new InMemoryRunRepository();
        var results = Results(gcp, repo);

        var outcome = await results.DeleteAsync(CloudCopyRow(), CancellationToken.None);

        outcome.Status.ShouldBe(CloudResultsStatus.SignInNeeded);
        repo.AllRecordedInOrder.ShouldBeEmpty("nothing was deleted, so the row must not say it was");
    }

    [Fact]
    public async Task A_failure_that_is_not_a_sign_in_is_still_the_generic_failure()
    {
        var gcp = new FakeGcp().WithAccount("sub-a", "a@example.org", Project).WithAccountFailure("sub-a", AuthErrorCodes.NetworkUnavailable);
        var results = Results(gcp, new InMemoryRunRepository());

        var outcome = await results.RedownloadAsync(CloudCopyRow(), CancellationToken.None);

        outcome.Status.ShouldBe(CloudResultsStatus.Failed);
    }

    // ---- Issue #830 review F5: a typed outcome picks the code, and a signed-out account's sentence names the sign-in action ----

    private static VmTerminator TerminatorOn(FakeGcp gcp)
    {
        var settings = new CloudRunSettings { LifecycleTimeout = TimeSpan.FromMilliseconds(200), LifecyclePollInterval = TimeSpan.FromMilliseconds(10) };
        return new VmTerminator(gcp, new GatewayCalls(settings, gcp), settings);
    }

    [Fact]
    public async Task A_waiting_request_that_cannot_be_deleted_because_the_account_is_signed_out_names_the_sign_in_not_the_Cloud_page()
    {
        var failure = await TerminatorOn(GcpWithSignedOutAccountA()).DeleteQueuedRequestsAsync(ScopeA, [("deg-job-q", "us-central1-a")], CancellationToken.None);

        failure.ShouldNotBeNull();
        failure.AccountSignedOut.ShouldBeTrue();
        failure.Message.ShouldContain("Sign in");
        failure.Message.ShouldNotContain("Cloud page");
    }

    [Fact]
    public async Task An_owned_VM_that_cannot_be_looked_at_because_the_account_is_signed_out_names_the_sign_in_not_the_Cloud_page()
    {
        var sent = new SentCreate(ScopeA, "job-so", "deg-job-so", "us-central1-a", 1, "installation") { Create = Task.CompletedTask };

        var failure = await TerminatorOn(GcpWithSignedOutAccountA()).DeleteOwnedVmsAndVerifyAsync(ScopeA, "job-so", CreateSettleState.Settled, CancellationToken.None, [sent]);

        failure.ShouldNotBeNull();
        failure.AccountSignedOut.ShouldBeTrue(failure.Message);
        failure.Message.ShouldContain("Sign in");
        failure.Message.ShouldNotContain("Cloud page");
    }

    [Fact]
    public void Every_sentence_built_for_a_failure_names_the_sign_in_for_a_signed_out_account_and_the_Cloud_page_action_otherwise()
    {
        var signedOut = EndFailure.Of("VM 'x' could not be stopped", RunErrorCodes.AccountSignedOut, "Stop it from the Cloud page.");
        var quota = EndFailure.Of("VM 'x' could not be stopped", "permission", "Stop it from the Cloud page.");

        signedOut.AccountSignedOut.ShouldBeTrue();
        signedOut.Message.ShouldBe("VM 'x' could not be stopped (account_signed_out). Sign in as the Google account this run was made under, then try again.");
        quota.AccountSignedOut.ShouldBeFalse();
        quota.Message.ShouldBe("VM 'x' could not be stopped (permission). Stop it from the Cloud page.");
    }

    [Fact]
    public async Task A_cancel_failure_that_only_mentions_the_signed_out_code_in_its_words_is_still_cancel_failed()
    {
        // The row's code is chosen from the typed class, not by searching the sentence: this sentence contains the old marker but its class is "other".
        var repo = new InMemoryRunRepository();
        await repo.UpsertAsync(new RunRecord("job-w", JobPhase.Cancelling, DateTimeOffset.UtcNow, Target: "cloud", ProjectId: Project), CancellationToken.None);
        var gcp = new FakeGcp();
        var runner = new CloudJobRunner(gcp, gcp, gcp, gcp, gcp, repo);
        var canceller = runner.CancellerForTests;

        await canceller.RecordCancelOutcomeAsync("job-w", EndFailure.Other("The note says (account_signed_out) but the class is other."));

        repo.AllRecordedInOrder.Last(r => r.JobId == "job-w").ErrorCode.ShouldBe(RunErrorCodes.CancelFailed);
    }
}
