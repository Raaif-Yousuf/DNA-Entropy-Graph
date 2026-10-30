using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Cloud;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Cloud.Tests;

/// <summary>
/// Issue #938, round 6 (and #999). H1: a reuse whose task ended Canceled is watched like a create, with the grace. L1: only an error raised before the start POST is "never sent".
/// L2: a final look that meets a pending newer attempt keeps waiting. L3: a failing label listing in the read-back is an unreadable look, not an unexpected failure.
/// </summary>
public partial class LateCreateAfterCancelTests
{
    /// <summary>A gateway whose reuse runs the real fake and then fails (the start ran, the poll or the answer failed), or whose label listing fails on a chosen call.</summary>
    private sealed class ScriptedCompute(FakeGcp inner, Func<Task>? afterReuse = null, Func<int, bool>? failFind = null, Func<bool>? rejectStop = null, bool rejectStopWhileBooting = false) : IComputeGateway
    {
        private int _finds;
        private int _rejectedStops;

        /// <summary>How many stops this gateway refused (round 7: while the VM was booting, or while <c>rejectStop</c> said so).</summary>
        public int RejectedStops => Volatile.Read(ref _rejectedStops);

        public Task<VmDescriptor?> GetVmAsync(string vmName, string zone, CancellationToken cancellationToken) => inner.GetVmAsync(vmName, zone, cancellationToken);

        public Task<VmDescriptor> CreateVmAsync(VmSpec spec, string zone, CancellationToken cancellationToken) => inner.CreateVmAsync(spec, zone, cancellationToken);

        public async Task<VmDescriptor> ReuseVmAsync(VmSpec spec, string existingVmName, string zone, CancellationToken cancellationToken)
        {
            var started = await inner.ReuseVmAsync(spec, existingVmName, zone, cancellationToken);
            if (afterReuse is not null)
            {
                await afterReuse();
            }

            return started;
        }

        public async Task StopVmAsync(string vmName, string zone, CancellationToken cancellationToken)
        {
            var booting = rejectStopWhileBooting && await inner.GetVmAsync(vmName, zone, cancellationToken) is { } vm && VmFacts.IsBooting(vm);
            if (booting || rejectStop?.Invoke() == true)
            {
                Interlocked.Increment(ref _rejectedStops);
                throw new CloudOperationException(new CloudError("INVALID_STATE", 400, "The instance is not running."), CloudErrorKind.Other);
            }

            await inner.StopVmAsync(vmName, zone, cancellationToken);
        }

        public Task DeleteVmAsync(string vmName, string zone, CancellationToken cancellationToken) => inner.DeleteVmAsync(vmName, zone, cancellationToken);

        public Task<IReadOnlyList<VmDescriptor>> FindByJobIdAsync(string jobId, CancellationToken cancellationToken)
            => failFind is not null && failFind(Interlocked.Increment(ref _finds))
                ? throw new CloudOperationException(new CloudError("UNAVAILABLE", 503, "listing failed"), CloudErrorKind.Network)
                : inner.FindByJobIdAsync(jobId, cancellationToken);

        public Task<IReadOnlyList<VmDescriptor>> ListByInstallationAsync(string installationId, CancellationToken cancellationToken) => inner.ListByInstallationAsync(installationId, cancellationToken);

        public Task<IReadOnlyList<VmDescriptor>> ListAppVmsAsync(CancellationToken cancellationToken) => inner.ListAppVmsAsync(cancellationToken);

        public Task StartVmAsync(string vmName, string zone, CancellationToken cancellationToken) => inner.StartVmAsync(vmName, zone, cancellationToken);

        public Task<IReadOnlyList<DiskDescriptor>> ListDisksByInstallationAsync(string installationId, CancellationToken cancellationToken) => inner.ListDisksByInstallationAsync(installationId, cancellationToken);

        public Task DeleteDiskAsync(string diskName, string zone, CancellationToken cancellationToken) => inner.DeleteDiskAsync(diskName, zone, cancellationToken);

        public Task<IReadOnlyList<string>> ListAcceleratorZonesAsync(string acceleratorType, CancellationToken cancellationToken) => inner.ListAcceleratorZonesAsync(acceleratorType, cancellationToken);
    }

    [Fact]
    public async Task R6_H1_a_reuse_whose_start_has_not_landed_is_not_concluded_stopped_until_the_grace_after_its_task_ended()
    {
        // The donor is relabelled and stopped and the reuse task ended Canceled; the start lands only AFTER the watch's first looks (inside the grace).
        var gcp = new FakeGcp().WithReuseStartThatTheClientCancels();
        var rig = new Rig(gcp);
        rig.Settings.LateCreateGrace = TimeSpan.FromSeconds(10);
        var oldSpec = await SeedStoppedDonorAsync(gcp, rig, "job-oldr6h1b");
        await rig.SeedRowAsync("job-r6h1b");
        var spec = Spec("job-r6h1b");
        using var cts = new CancellationTokenSource();
        var ladder = rig.Provisioner.ProvisionAsync(TestInputs.Request("job-r6h1b", spec), spec, new RunProgress(), cts.Token);
        await gcp.ReuseParkedAfterClaim.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await cts.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(() => ladder);
        await rig.Canceller.CancelAsync("job-r6h1b", CancellationToken.None);

        var reap = rig.Canceller.WhenReapsEndedAsync();
        await Task.Delay(100, TestContext.Current.CancellationToken); // many watch rounds (1 ms apart) with the donor stopped and its task ended
        reap.IsCompleted.ShouldBeFalse("a stopped donor with an ended task says nothing until the grace has run: its start may still land");

        await gcp.LandAcceptedReuseStartsAsync();
        await reap.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        rig.Outcomes.Reader.TryRead(out var outcome).ShouldBeTrue();
        outcome!.State.ShouldBe(LateCreateState.ReuseStopped);
        (await gcp.GetVmAsync(oldSpec.VmName, ZoneA, CancellationToken.None))!.Status.ShouldBeOneOf("STOPPED", "TERMINATED");
        gcp.DeleteAttempts.ShouldBeEmpty();
    }

    [Fact]
    public async Task R6_H1_a_reuse_that_never_lands_ends_stopped_after_the_grace()
    {
        var gcp = new FakeGcp().WithReuseStartThatTheClientCancels();
        var rig = new Rig(gcp);
        var oldSpec = await SeedStoppedDonorAsync(gcp, rig, "job-oldr6h1c");
        await rig.SeedRowAsync("job-r6h1c");
        var spec = Spec("job-r6h1c");
        using var cts = new CancellationTokenSource();
        var ladder = rig.Provisioner.ProvisionAsync(TestInputs.Request("job-r6h1c", spec), spec, new RunProgress(), cts.Token);
        await gcp.ReuseParkedAfterClaim.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await cts.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(() => ladder);

        await rig.Canceller.CancelAsync("job-r6h1c", CancellationToken.None);
        var outcome = await rig.NextOutcomeAsync();

        outcome.State.ShouldBe(LateCreateState.ReuseStopped);
        (await gcp.GetVmAsync(oldSpec.VmName, ZoneA, CancellationToken.None))!.Status.ShouldBeOneOf("STOPPED", "TERMINATED");
    }

    [Theory]
    [InlineData("permission")]
    [InlineData("billing")]
    [InlineData("api_disabled")]
    [InlineData("org_policy")]
    public async Task R6_L1_an_error_that_could_come_from_the_poll_after_the_start_POST_is_not_never_sent_and_the_reuse_stays_held(string kind)
    {
        // The start ran (the VM is RUNNING), then operations.get answered an error of this class: the VM may be running, so a later cancel must still watch it.
        var gcp = new FakeGcp();
        var error = kind switch
        {
            "permission" => new CloudOperationException(new CloudError("PERMISSION_DENIED", 403, "denied"), CloudErrorKind.Permission),
            "billing" => new CloudOperationException(new CloudError("BILLING_DISABLED", 403, "billing"), CloudErrorKind.Billing),
            "api_disabled" => new CloudOperationException(new CloudError("SERVICE_DISABLED", 403, "api"), CloudErrorKind.ApiDisabled),
            _ => new CloudOperationException(new CloudError("ORG_POLICY", 412, "policy"), CloudErrorKind.OrgPolicy),
        };
        var compute = new ScriptedCompute(gcp, afterReuse: () => Task.FromException(error));
        var rig = new Rig(gcp, compute);
        await SeedStoppedDonorAsync(gcp, rig, "job-oldr6l1-" + kind.Replace('_', '-'));
        var jobId = "job-r6l1-" + kind.Replace('_', '-');
        await rig.SeedRowAsync(jobId);
        var spec = Spec(jobId);
        await rig.Provisioner.ProvisionAsync(TestInputs.Request(jobId, spec), spec, new RunProgress(), CancellationToken.None);

        var held = rig.Provisioner.ClaimSentCreates(jobId);

        held.ShouldContain(c => c.IsReuse, "the error may have come after the start POST: dropping the watch could leave a GPU VM running");
        held.Single(c => c.IsReuse).Refused.ShouldBeFalse("Refused means no VM exists; a reuse that raised this class cannot say so");
    }

    [Fact]
    public async Task R6_L1_a_stockout_from_a_failed_start_operation_is_never_sent_and_the_reuse_is_not_held()
    {
        var gcp = new FakeGcp().WithReuseStartFailure(CloudErrorKind.Stockout);
        var rig = new Rig(gcp);
        await SeedStoppedDonorAsync(gcp, rig, "job-oldr6l1s");
        await rig.SeedRowAsync("job-r6l1s");
        var spec = Spec("job-r6l1s");
        await rig.Provisioner.ProvisionAsync(TestInputs.Request("job-r6l1s", spec), spec, new RunProgress(), CancellationToken.None);

        rig.Provisioner.ClaimSentCreates("job-r6l1s").ShouldNotContain(c => c.IsReuse);
    }

    [Fact]
    public async Task R6_L2_a_final_look_that_meets_a_pending_newer_attempt_keeps_waiting_and_deletes_when_that_attempt_fails_elsewhere()
    {
        var gcp = new FakeGcp();
        var spec = Spec("job-r6l2");
        await gcp.CreateVmAsync(spec, ZoneA, CancellationToken.None);
        SentCreate? sent = null;
        Rig? rigRef = null;
        long newer = 0;
        var calls = 0;
        var compute = new HookedDeleteCompute(
            gcp,
            async () =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                {
                    // Accepted, does nothing yet; a newer attempt of the job starts provisioning (its delete span ended).
                    rigRef!.Terminator.Ledger.EndDelete(sent!);
                    newer = await rigRef.Terminator.Ledger.BeginAttemptAsync("job-r6l2", CancellationToken.None);
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(100);
                        rigRef.Terminator.Ledger.EndAttempt("job-r6l2", newer); // it failed in another zone and owns nothing
                    });
                    return;
                }

                await gcp.DeleteVmAsync("deg-job-r6l2", ZoneA, CancellationToken.None);
            });
        var rig = new Rig(gcp, compute);
        rigRef = rig;
        rig.Settings.LifecycleTimeout = TimeSpan.Zero; // the final look runs in the same round as the first delete
        var generation = await rig.Terminator.Ledger.BeginAttemptAsync("job-r6l2", CancellationToken.None);
        rig.Terminator.Ledger.EndAttempt("job-r6l2", generation);
        sent = new SentCreate("job-r6l2", spec.VmName, ZoneA, generation, spec.InstallationId) { Create = Task.CompletedTask };

        var outcomes = await rig.Terminator.ReapLateCreatesAsync([sent]);

        outcomes[0].State.ShouldBe(LateCreateState.Deleted, "a pending newer attempt is not a hand-over: it never adopted this VM, so the late VM must still be deleted");
        (await gcp.GetVmAsync("deg-job-r6l2", ZoneA, CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task R6_L3_a_failing_label_listing_in_the_read_back_is_an_unreadable_look_not_an_unexpected_failure()
    {
        var gcp = new FakeGcp();
        var spec = Spec("job-r6l3");
        await gcp.CreateVmAsync(spec, ZoneA, CancellationToken.None);
        var compute = new ScriptedCompute(gcp, failFind: n => n == 2); // the first listing finds the VM; the read-back's first listing fails
        var rig = new Rig(gcp, compute);
        var generation = await rig.Terminator.Ledger.BeginAttemptAsync("job-r6l3", CancellationToken.None);
        rig.Terminator.Ledger.EndAttempt("job-r6l3", generation);
        var sent = new SentCreate("job-r6l3", spec.VmName, ZoneA, generation, spec.InstallationId) { Create = Task.CompletedTask };

        var result = await rig.Terminator.DeleteOwnedVmsAndVerifyAsync("job-r6l3", CreateSettleState.Settled, CancellationToken.None, [sent]);

        result.ShouldBeNull("the listing failed once, the next one read the VM gone: the loop keeps looking instead of throwing");
        (await gcp.GetVmAsync("deg-job-r6l3", ZoneA, CancellationToken.None)).ShouldBeNull();
    }
}
