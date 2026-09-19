using DnaEntropyGraph.Cloud;
using DnaEntropyGraph.Core.Cloud;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Cloud.Tests;

public class FakeGcpTests
{
    private static VmSpec ValidSpec(string jobId) => new(
        InstallationId: "install-1",
        JobId: jobId,
        Model: "evo2_7b",
        AppVersion: "0.1.0",
        Lifecycle: "run",
        MachineType: "g2-standard-8",
        MaxRunDuration: TimeSpan.FromHours(4),
        TerminationAction: "DELETE");

    [Fact]
    public async Task Created_vm_is_found_by_name_and_zone()
    {
        var gcp = new FakeGcp();

        var created = await gcp.CreateVmAsync(ValidSpec("job-1"), CancellationToken.None);
        var found = await gcp.GetVmAsync(created.Name, created.Zone, CancellationToken.None);

        found.ShouldNotBeNull();
        found!.Name.ShouldBe(created.Name);
    }

    [Fact]
    public async Task Stopped_vm_is_reported_as_stopped_not_deleted()
    {
        var gcp = new FakeGcp();
        var created = await gcp.CreateVmAsync(ValidSpec("job-2"), CancellationToken.None);

        await gcp.StopVmAsync(created.Name, created.Zone, CancellationToken.None);
        var found = await gcp.GetVmAsync(created.Name, created.Zone, CancellationToken.None);

        found.ShouldNotBeNull();
        found!.Status.ShouldBe("STOPPED");
    }

    [Fact]
    public async Task Deleted_vm_is_no_longer_found()
    {
        var gcp = new FakeGcp();
        var created = await gcp.CreateVmAsync(ValidSpec("job-3"), CancellationToken.None);

        await gcp.DeleteVmAsync(created.Name, created.Zone, CancellationToken.None);
        var found = await gcp.GetVmAsync(created.Name, created.Zone, CancellationToken.None);

        found.ShouldBeNull();
    }

    [Fact]
    public async Task A_spec_missing_a_required_label_is_rejected_before_the_fake_creates_anything()
    {
        var gcp = new FakeGcp();
        var spec = ValidSpec("job-4") with { InstallationId = "" };

        await Should.ThrowAsync<InvalidOperationException>(() => gcp.CreateVmAsync(spec, CancellationToken.None));
    }

    [Fact]
    public async Task Reports_billing_and_compute_api_enabled_so_preflight_can_pass_in_dev_mode()
    {
        var gcp = new FakeGcp();

        (await gcp.IsBillingEnabledAsync("fake-project", CancellationToken.None)).ShouldBeTrue();
        (await gcp.IsComputeApiEnabledAsync("fake-project", CancellationToken.None)).ShouldBeTrue();
    }
}
