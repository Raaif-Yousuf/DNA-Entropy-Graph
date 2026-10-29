namespace DnaEntropyGraph.Core.Cloud;

/// <summary>
/// Issue #938: one create request an attempt of a job ASKED Compute to make, recorded at SEND time (before the gateway is called) by the VM name and zone it names. It is what a
/// cancel and the late clean-up look up BY NAME, whatever became of the create task: the real gateway honours cancellation, so a cancelled create ends Canceled within
/// milliseconds although Compute may already have accepted the insert and made the VM. The task is kept only to tell a rejected create (no VM) from one whose outcome is unknown.
/// </summary>
internal sealed class SentCreate(string jobId, string name, string zone, long generation, Func<bool> isSuperseded)
{
    private int _claimed;

    public string JobId { get; } = jobId;

    public string Name { get; } = name;

    public string Zone { get; } = zone;

    /// <summary>The job's attempt this create belongs to (<see cref="VmProvisioner"/> counts one per <c>ProvisionAsync</c>).</summary>
    public long Generation { get; } = generation;

    /// <summary>The create call's task, set once the gateway returned it. Null while the gateway has not yet returned (the request may be on the wire).</summary>
    public Task? Create { get; set; }

    /// <summary>Set once a look by name saw the VM this create names (it exists or existed): nothing further can appear from this create.</summary>
    public volatile bool Landed;

    /// <summary>
    /// True once a LATER attempt of the same job has itself created, adopted or reused a VM under this name in this zone: that VM is the newer attempt's, and this create's clean-up
    /// leaves it alone. A VM under the same name in ANOTHER zone is not covered (the newer attempt did not claim it), so it is still cleaned up.
    /// </summary>
    public bool IsSuperseded => isSuperseded();

    /// <summary>True once a cancel owns the clean-up of this create: the terminal-state forget no longer drops it.</summary>
    public bool Claimed => Volatile.Read(ref _claimed) == 1;

    /// <summary>Takes ownership of the clean-up. True for exactly one caller.</summary>
    public bool TryClaim() => Interlocked.CompareExchange(ref _claimed, 1, 0) == 0;

    /// <summary>
    /// The create has ended with an answer that says no VM can come of it: it answered a VM, or Compute refused it (billing, API, quota, permission, org policy, already exists). A cancelled or
    /// timed-out create, a network failure, a stockout (the ladder's own comment: a create can fail after the server accepted it) and one still in flight are NOT definitive.
    /// </summary>
    public bool Definitive
    {
        get
        {
            var task = Create;
            if (task is null || !task.IsCompleted)
            {
                return false;
            }

            if (task.IsCompletedSuccessfully)
            {
                return true;
            }

            return task.Exception?.GetBaseException() is CloudOperationException
            {
                Kind: CloudErrorKind.Billing or CloudErrorKind.ApiDisabled or CloudErrorKind.Quota or CloudErrorKind.Permission or CloudErrorKind.OrgPolicy or CloudErrorKind.AlreadyExists,
            };
        }
    }
}
