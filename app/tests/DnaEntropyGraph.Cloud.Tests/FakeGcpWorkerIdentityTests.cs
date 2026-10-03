using DnaEntropyGraph.Core.Cloud;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Cloud.Tests;

/// <summary>Issue #54: FakeGcp implements IWorkerIdentityGateway with the same outcomes the real gateway has, and scripted failures.</summary>
public class FakeGcpWorkerIdentityTests
{
    private const string Project = "my-lab";
    private const string Bucket = "deg-fake-bucket";

    [Fact]
    public async Task The_worker_account_is_created_once_and_adopted_on_every_later_call()
    {
        var gcp = new FakeGcp();

        var first = await gcp.EnsureWorkerIdentityAsync(Project, Bucket, CancellationToken.None);
        var second = await gcp.EnsureWorkerIdentityAsync(Project, Bucket, CancellationToken.None);

        first.ServiceAccountEmail.ShouldBe(WorkerIdentityNames.ServiceAccountEmail(Project));
        first.NoteCode.ShouldBeNull();
        second.ShouldBe(first);
        gcp.WorkerIdentityEnsureCalls.ShouldBe(2);
        gcp.WorkerAccountsCreated.ShouldBe(1);
    }

    [Fact]
    public async Task An_org_policy_that_forbids_service_accounts_hands_back_the_default_compute_account_and_the_yellow_note()
    {
        var gcp = new FakeGcp().WithServiceAccountCreationBlockedByOrgPolicy();

        var identity = await gcp.EnsureWorkerIdentityAsync(Project, Bucket, CancellationToken.None);

        identity.ServiceAccountEmail.ShouldBe(WorkerIdentityNames.DefaultComputeAccountEmail(FakeGcp.ProjectNumberFor(Project)));
        identity.NoteCode.ShouldBe(SetupErrorCodes.WorkerDefaultAccount);
        identity.IsDefaultComputeAccount.ShouldBeTrue();
        gcp.WorkerAccountsCreated.ShouldBe(0);
    }

    [Fact]
    public async Task A_scripted_permission_refusal_throws_a_permission_error_that_names_the_setup_code()
    {
        var gcp = new FakeGcp().WithWorkerIdentityPermissionDenied();

        var failure = await Should.ThrowAsync<CloudOperationException>(() => gcp.EnsureWorkerIdentityAsync(Project, Bucket, CancellationToken.None));

        failure.Kind.ShouldBe(CloudErrorKind.Permission);
        failure.Error.Code.ShouldBe(SetupErrorCodes.Permission);
    }

    [Fact]
    public async Task A_scripted_actAs_refusal_throws_PERMISSION_ACTAS()
    {
        var gcp = new FakeGcp().WithWorkerIdentityActAsDenied();

        var failure = await Should.ThrowAsync<CloudOperationException>(() => gcp.EnsureWorkerIdentityAsync(Project, Bucket, CancellationToken.None));

        failure.Kind.ShouldBe(CloudErrorKind.Permission);
        failure.Error.Code.ShouldBe(SetupErrorCodes.PermissionActAs);
    }
}
