using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Cloud;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Cloud.Tests;

/// <summary>Hard Rule 11's end-and-verify, alone: what a failed run does to its VM, and what counts as confirmed.</summary>
public class VmTerminatorTests
{
    private const string Project = "my-project";
    private const string Zone = "us-central1-a";

    private static VmSpec Spec(string jobId) => new(
        ProjectId: Project,
        InstallationId: "install-1",
        JobId: jobId,
        Model: "evo2_7b",
        AppVersion: "0.1.0",
        Lifecycle: "stop",
        MachineType: "g2-standard-8",
        MaxRunDuration: TimeSpan.FromHours(4),
        TerminationAction: "DELETE");

    private static VmTerminator Terminator(FakeGcp gcp)
    {
        var settings = new CloudRunSettings { LifecyclePollInterval = TimeSpan.FromMilliseconds(1), LifecycleTimeout = TimeSpan.FromMilliseconds(50) };
        return new VmTerminator(gcp, new GatewayCalls(settings, gcp), settings);
    }

    [Theory]
    [InlineData(AfterTaskAction.Stop, AfterKeepAliveAction.Stop, AfterTaskAction.Stop)]
    [InlineData(AfterTaskAction.Delete, AfterKeepAliveAction.Stop, AfterTaskAction.Delete)]
    [InlineData(AfterTaskAction.KeepAlive, AfterKeepAliveAction.Delete, AfterTaskAction.Delete)]
    [InlineData(AfterTaskAction.KeepAlive, AfterKeepAliveAction.Stop, AfterTaskAction.Stop)]
    public void A_failed_run_deletes_only_when_the_user_chose_delete_somewhere(AfterTaskAction after, AfterKeepAliveAction keepAlive, AfterTaskAction expected)
    {
        var request = TestInputs.Request("job-t1", Spec("job-t1"), afterTask: after);
        request = request with { Options = request.Options with { AfterKeepAlive = keepAlive } };

        VmTerminator.FailureEndAction(request).ShouldBe(expected);
    }

    [Fact]
    public async Task A_VM_found_by_label_in_any_zone_is_ended_and_confirmed()
    {
        var gcp = new FakeGcp();
        var spec = Spec("job-t2");
        await gcp.CreateVmAsync(spec, "us-east1-b", CancellationToken.None);
        var request = TestInputs.Request("job-t2", spec);

        var note = await Terminator(gcp).EndVmAfterFailureAsync(request, null, forceDelete: true);

        note.ShouldBeNull();
        (await gcp.FindByJobIdAsync("job-t2", CancellationToken.None)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_run_with_no_VM_is_confirmed_ended()
    {
        var gcp = new FakeGcp();
        var request = TestInputs.Request("job-t3", Spec("job-t3"));

        (await Terminator(gcp).EndVmAfterFailureAsync(request, Zone)).ShouldBeNull();
    }
}
