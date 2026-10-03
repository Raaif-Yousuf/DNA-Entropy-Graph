using DnaEntropyGraph.Core.Cloud;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Cloud.Tests;

/// <summary>Issue #52: FakeGcp implements service enablement, including the delay between "the call returned" and "the service reads ENABLED".</summary>
public class FakeGcpServiceEnablementTests
{
    private const string Project = "my-lab";

    [Fact]
    public async Task Services_are_on_by_default_and_a_disabled_one_is_turned_on_by_enabling_it()
    {
        var gcp = new FakeGcp().WithServiceDisabled(Project, RequiredServices.Storage);

        (await gcp.IsServiceEnabledAsync(Project, RequiredServices.Storage, CancellationToken.None)).ShouldBeFalse();
        (await gcp.IsServiceEnabledAsync(Project, RequiredServices.Compute, CancellationToken.None)).ShouldBeTrue();

        await gcp.EnableServicesAsync(Project, RequiredServices.Ids, CancellationToken.None);

        (await gcp.IsServiceEnabledAsync(Project, RequiredServices.Storage, CancellationToken.None)).ShouldBeTrue();
    }

    [Fact]
    public async Task The_compute_api_switch_and_the_service_check_agree()
    {
        var gcp = new FakeGcp().WithComputeApiOff(Project);

        (await gcp.IsServiceEnabledAsync(Project, RequiredServices.Compute, CancellationToken.None)).ShouldBeFalse();
        (await gcp.IsComputeApiEnabledAsync(Project, CancellationToken.None)).ShouldBeFalse();

        await gcp.EnableServicesAsync(Project, [RequiredServices.Compute], CancellationToken.None);

        (await gcp.IsComputeApiEnabledAsync(Project, CancellationToken.None)).ShouldBeTrue();
    }

    [Fact]
    public async Task An_enablement_delay_is_waited_out_inside_the_call_so_the_service_is_on_when_it_returns()
    {
        // IServiceEnablementGateway: EnableServicesAsync returns only once every service is ENABLED.
        var gcp = new FakeGcp().WithServiceDisabled(Project, RequiredServices.Compute).WithServiceEnablementDelay(polls: 2);

        await gcp.EnableServicesAsync(Project, [RequiredServices.Compute], CancellationToken.None);

        (await gcp.IsServiceEnabledAsync(Project, RequiredServices.Compute, CancellationToken.None)).ShouldBeTrue();
        gcp.ServiceEnablementPollsWaited.ShouldBe(2);
    }

    [Fact]
    public async Task Enabling_a_service_that_is_already_on_waits_for_nothing()
    {
        var gcp = new FakeGcp().WithServiceEnablementDelay(polls: 2);

        await gcp.EnableServicesAsync(Project, [RequiredServices.Compute], CancellationToken.None);

        gcp.ServiceEnablementPollsWaited.ShouldBe(0);
    }

    [Fact]
    public async Task A_member_who_is_not_the_owner_gets_NOT_PROJECT_OWNER_and_nothing_is_enabled()
    {
        var gcp = new FakeGcp().WithServiceDisabled(Project, RequiredServices.Compute).WithNotProjectOwner(Project);

        var ex = await Should.ThrowAsync<CloudOperationException>(() => gcp.EnableServicesAsync(Project, RequiredServices.Ids, CancellationToken.None));

        ex.Error.Code.ShouldBe(SetupErrorCodes.NotProjectOwner);
        ex.Kind.ShouldBe(CloudErrorKind.Permission);
        (await gcp.IsServiceEnabledAsync(Project, RequiredServices.Compute, CancellationToken.None)).ShouldBeFalse();
    }
}
