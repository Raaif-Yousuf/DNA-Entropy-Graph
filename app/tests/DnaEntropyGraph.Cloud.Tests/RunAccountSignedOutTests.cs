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
        row.ErrorDetail!.ShouldNotContain("(other)");
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
}
