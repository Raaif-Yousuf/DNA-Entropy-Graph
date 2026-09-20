using DnaEntropyGraph.Cloud;
using DnaEntropyGraph.Core.Cloud;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Cloud.Tests;

public class FakeGcpTests
{
    private const string Zone = "us-central1-a";

    private static VmSpec ValidSpec(string jobId) => new(
        ProjectId: "fake-project",
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

        var created = await gcp.CreateVmAsync(ValidSpec("job-1"), Zone, CancellationToken.None);
        var found = await gcp.GetVmAsync(created.Name, created.Zone, CancellationToken.None);

        found.ShouldNotBeNull();
        found!.Name.ShouldBe(created.Name);
    }

    [Fact]
    public async Task Stopped_vm_is_reported_as_stopped_not_deleted()
    {
        var gcp = new FakeGcp();
        var created = await gcp.CreateVmAsync(ValidSpec("job-2"), Zone, CancellationToken.None);

        await gcp.StopVmAsync(created.Name, created.Zone, CancellationToken.None);
        var found = await gcp.GetVmAsync(created.Name, created.Zone, CancellationToken.None);

        found.ShouldNotBeNull();
        found!.Status.ShouldBe("STOPPED");
    }

    [Fact]
    public async Task Deleted_vm_is_no_longer_found()
    {
        var gcp = new FakeGcp();
        var created = await gcp.CreateVmAsync(ValidSpec("job-3"), Zone, CancellationToken.None);

        await gcp.DeleteVmAsync(created.Name, created.Zone, CancellationToken.None);
        var found = await gcp.GetVmAsync(created.Name, created.Zone, CancellationToken.None);

        found.ShouldBeNull();
    }

    [Fact]
    public async Task A_spec_missing_a_required_label_is_rejected_before_the_fake_creates_anything()
    {
        var gcp = new FakeGcp();
        var spec = ValidSpec("job-4") with { InstallationId = "" };

        await Should.ThrowAsync<InvalidOperationException>(() => gcp.CreateVmAsync(spec, Zone, CancellationToken.None));

        (await gcp.GetVmAsync(spec.VmName, Zone, CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task Reports_billing_and_compute_api_enabled_so_preflight_can_pass_in_dev_mode()
    {
        var gcp = new FakeGcp();

        (await gcp.IsBillingEnabledAsync("fake-project", CancellationToken.None)).ShouldBeTrue();
        (await gcp.IsComputeApiEnabledAsync("fake-project", CancellationToken.None)).ShouldBeTrue();
    }

    [Fact]
    public async Task Default_gpu_quota_is_one_so_the_unscripted_baseline_still_succeeds()
    {
        var gcp = new FakeGcp();

        (await gcp.GetGpuQuotaAsync("fake-project", "us-central1", "nvidia-l4", CancellationToken.None)).ShouldBe(1);
    }

    [Fact]
    public void Signed_out_account_reports_no_selected_project()
    {
        var gcp = new FakeGcp().WithSignedOut();

        gcp.IsSignedIn.ShouldBeFalse();
        gcp.SelectedProjectId.ShouldBeNull();
    }

    [Fact]
    public void A_fresh_fake_is_not_signed_in_with_no_selected_project()
    {
        // Issue #424: a fresh profile is not signed in to any Google
        // account. The always-signed-in-to-"fake-project" default this
        // fake used to carry contradicted that - a ShellViewModel built
        // against an unscripted FakeGcp would show a signed-in status pill
        // on what is supposed to be a fresh profile.
        var gcp = new FakeGcp();

        gcp.IsSignedIn.ShouldBeFalse();
        gcp.SelectedProjectId.ShouldBeNull();
    }

    [Fact]
    public async Task SignInAsync_flips_a_fresh_fake_to_signed_in()
    {
        // Issue #424's own Tests line: "a new FakeGcp instance is not
        // signed in until SignInAsync is called; SignInAsync flips
        // IsSignedIn to true." WizardViewModel.SignInAsync reads
        // IGcpAccount.IsSignedIn right after awaiting this call to decide
        // whether to navigate on - a no-op SignInAsync would leave that
        // flow wired to nothing against this fake.
        var gcp = new FakeGcp();

        await gcp.SignInAsync(CancellationToken.None);

        gcp.IsSignedIn.ShouldBeTrue();
        gcp.SelectedProjectId.ShouldNotBeNull();
    }

    [Fact]
    public void WithSelectedProject_arms_the_fake_as_signed_in_to_that_project()
    {
        var gcp = new FakeGcp().WithSelectedProject("my-project");

        gcp.IsSignedIn.ShouldBeTrue();
        gcp.SelectedProjectId.ShouldBe("my-project");
    }
}
