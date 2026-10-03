using DnaEntropyGraph.Core.Cloud;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Cloud.Tests;

/// <summary>Issue #50: FakeGcp implements the project catalog with the same scripted failures the real gateway maps to.</summary>
public class FakeGcpProjectCatalogTests
{
    [Fact]
    public async Task A_created_project_is_listed_active_and_labelled_and_the_app_ones_come_first()
    {
        var gcp = new FakeGcp().WithExistingProject("zeta-lab", "Zeta Lab");

        var created = await gcp.CreateProjectAsync("dna-entropy-abcd1234", "DNA Entropy Graph", "inst-1", CancellationToken.None);
        var listed = await gcp.ListActiveProjectsAsync(CancellationToken.None);

        created.IsAppProject.ShouldBeTrue();
        listed.Select(p => p.ProjectId).ShouldBe(["dna-entropy-abcd1234", "zeta-lab"]);
        (await gcp.GetProjectAsync("dna-entropy-abcd1234", CancellationToken.None))!.State.ShouldBe(ProjectLifecycleState.Active);
        (await gcp.GetProjectAsync("nope", CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task The_project_limit_throws_PROJECT_QUOTA_as_a_quota_error()
    {
        var gcp = new FakeGcp().WithProjectCreationQuotaExhausted();

        var ex = await Should.ThrowAsync<CloudOperationException>(() => gcp.CreateProjectAsync("dna-entropy-abcd1234", "DNA Entropy Graph", "inst-1", CancellationToken.None));

        ex.Error.Code.ShouldBe(SetupErrorCodes.ProjectQuota);
        ex.Kind.ShouldBe(CloudErrorKind.Quota);
        (await gcp.ListActiveProjectsAsync(CancellationToken.None)).ShouldBeEmpty();
    }

    [Fact]
    public async Task An_org_policy_block_throws_ORG_POLICY_BLOCK_and_creates_nothing()
    {
        var gcp = new FakeGcp().WithProjectCreationBlockedByOrgPolicy();

        var ex = await Should.ThrowAsync<CloudOperationException>(() => gcp.CreateProjectAsync("dna-entropy-abcd1234", "DNA Entropy Graph", "inst-1", CancellationToken.None));

        ex.Error.Code.ShouldBe(SetupErrorCodes.OrgPolicyBlock);
        ex.Kind.ShouldBe(CloudErrorKind.OrgPolicy);
        (await gcp.ListActiveProjectsAsync(CancellationToken.None)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Creating_the_same_id_twice_is_already_exists()
    {
        var gcp = new FakeGcp();
        await gcp.CreateProjectAsync("dna-entropy-abcd1234", "DNA Entropy Graph", "inst-1", CancellationToken.None);

        var ex = await Should.ThrowAsync<CloudOperationException>(() => gcp.CreateProjectAsync("dna-entropy-abcd1234", "DNA Entropy Graph", "inst-1", CancellationToken.None));

        ex.Kind.ShouldBe(CloudErrorKind.AlreadyExists);
    }
}
