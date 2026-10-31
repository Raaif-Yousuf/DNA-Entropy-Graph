namespace DnaEntropyGraph.Core.Cloud;

/// <summary>
/// Issue #938: one create request an attempt of a job ASKED Compute to make, recorded at SEND time (before the gateway is called) by the VM name and zone it names. It is what a
/// cancel and the late clean-up look up BY NAME, whatever became of the create task: the real gateway honours cancellation, so a cancelled create ends Canceled within
/// milliseconds although Compute may already have accepted the insert and made the VM. The task is kept only to tell a rejected create (no VM) from one whose outcome is unknown.
/// </summary>
internal sealed class SentCreate(CloudScope scope, string jobId, string name, string zone, long generation, string installationId, bool isReuse = false)
{
    private int _claimed;

    /// <summary>Issue #830: the project and account the create was sent in: every later look at, stop of and delete of this create's VM is made in the same scope, whatever is selected by then.</summary>
    public CloudScope Scope { get; } = scope;

    public string JobId { get; } = jobId;

    /// <summary>Round 4 M3: this is a REUSE (a start of another run's stopped VM, relabelled to this job), not a create. It is looked up by name like a create, but a VM it lands is STOPPED, never deleted: the VM is its first owner's.</summary>
    public bool IsReuse { get; } = isReuse;

    public string Name { get; } = name;

    public string Zone { get; } = zone;

    /// <summary>The installation the create was sent for: a VM under the same name that carries another installation's label is not this create's (Hard Rule 9).</summary>
    public string InstallationId { get; } = installationId;

    /// <summary>The job's attempt this create belongs to (<see cref="AttemptLedger"/> counts one per <c>ProvisionAsync</c>).</summary>
    public long Generation { get; } = generation;

    /// <summary>The create call's task, set once the gateway returned it. Null while the gateway has not yet returned (the request may be on the wire).</summary>
    public Task? Create { get; set; }

    /// <summary>Set once a look by name saw the VM this create names (it exists or existed), or a delete was sent for it: nothing further can appear from this create.</summary>
    public volatile bool Landed;

    /// <summary>
    /// Set once a look by name that STARTED after the create answered found no VM: the create's VM is gone (it deleted itself, or was deleted). An answer alone is not enough, because a VM
    /// the create made after the cancel's last look has never been looked at (round 3 M1).
    /// </summary>
    public volatile bool Settled;

    /// <summary>Guarded by <see cref="AttemptLedger"/>'s lock: a delete of this create's VM was begun, so the job's later attempts wait for it (see <see cref="AttemptLedger.TryBeginDelete"/>).</summary>
    internal bool DeleteBegun;

    /// <summary>Guarded by <see cref="AttemptLedger"/>'s lock: the delete this create began has ended.</summary>
    internal bool DeleteEnded;

    /// <summary>Round 8: a deferred reuse watch gave up WITHOUT a cloud write (a newer attempt of the job is pending and may own the VM): its donor and send stay recorded, <see cref="VmTerminator.ForgetDonor(SentCreate)"/> leaves them, and the age bound prunes them.</summary>
    internal volatile bool DonorKeptForNewerAttempt;

    /// <summary>True once a cancel owns the clean-up of this create: the terminal-state forget no longer drops it.</summary>
    public bool Claimed => Volatile.Read(ref _claimed) == 1;

    /// <summary>Takes ownership of the clean-up. True for exactly one caller.</summary>
    public bool TryClaim() => Interlocked.CompareExchange(ref _claimed, 1, 0) == 0;

    /// <summary>The create has ended with an answer: it names a VM (which may exist, may be gone, may have been made after the last look).</summary>
    public bool Answered => Create is { IsCompletedSuccessfully: true };

    /// <summary>
    /// Compute refused the create (billing, API, quota, stockout, permission, org policy, already exists): no VM of this create exists. A cancelled or timed-out create and a
    /// network or unclassified failure are NOT refused (the server may have accepted the insert), and neither is one still in flight.
    /// </summary>
    public bool Refused => Create is { IsFaulted: true } task && task.Exception?.GetBaseException() is CloudOperationException ex
        && (IsReuse
            ? ReuseNeverSent(ex)
            : ex.Kind is CloudErrorKind.Billing or CloudErrorKind.ApiDisabled or CloudErrorKind.Quota or CloudErrorKind.Stockout or CloudErrorKind.Permission or CloudErrorKind.OrgPolicy or CloudErrorKind.AlreadyExists);

    /// <summary>
    /// Round 6 L1. Code read 2026-10-30 (<c>GoogleComputeGateway.ReuseVmAsync</c>): these codes are thrown before any write (the project check, the instance read, the not-stopped check, the boot-disk parse), and a
    /// stockout can only be the answer of a FAILED start operation (the VM did not start). Every other class (permission, billing, API off, org policy, quota, a network failure) can also be the answer of an
    /// <c>operations.get</c> poll AFTER the start POST went out, so it says nothing about whether the VM runs: the reuse stays watched.
    /// </summary>
    internal static bool ReuseNeverSent(CloudOperationException ex)
        => ex.Kind == CloudErrorKind.Stockout || ex.Error.Code is "VM_NOT_FOUND" or "VM_NOT_STOPPED" or "VM_NO_BOOT_DISK" or "PROJECT_MISMATCH";
}
