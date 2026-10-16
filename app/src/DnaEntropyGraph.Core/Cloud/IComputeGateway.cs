namespace DnaEntropyGraph.Core.Cloud;

/// <summary>
/// Hard Rule 7: every Google Cloud call goes through an interface defined
/// HERE, in Core, never called directly from Presentation or App. Only
/// <c>DnaEntropyGraph.Cloud</c> may implement this against real
/// <c>Google.*</c> types; <c>FakeGcp</c> (also in Cloud) implements it
/// in-memory for every other test project. This project must never
/// reference <c>Google.*</c> itself.
///
/// <see cref="VmSpec"/> and <see cref="VmDescriptor"/> live in their own
/// file, <c>VmSpec.cs</c>, alongside this interface's namespace.
/// </summary>
public interface IComputeGateway
{
    /// <summary>
    /// Creates one VM in <paramref name="zone"/>. Takes the zone as its own
    /// parameter, not as part of <see cref="VmSpec"/>, because a zone is a
    /// per-attempt choice the caller (the zone ladder, issue #86) varies
    /// across retries for the *same* spec and the *same* VM name - the
    /// design this app relies on to try multiple zones under one job id
    /// (docs/cloud_design.md section 3). <see cref="VmSpec.EnsurePreconditions"/>
    /// runs before any request is built, real or fake, so a spec missing a
    /// label or a maxRunDuration - or one the real API would itself
    /// reject - never reaches a zone at all.
    /// </summary>
    Task<VmDescriptor> CreateVmAsync(VmSpec spec, string zone, CancellationToken cancellationToken);

    /// <summary>
    /// Issue #94: starts a STOPPED VM this installation left behind for <paramref name="spec"/>'s job: claims it FIRST by rewriting its labels to
    /// the spec's (so discovery by <c>job-id</c> finds it, Hard Rule 9), then replaces its scheduling limits and its metadata (the startup script
    /// and <c>deg-*</c> attributes) with the spec's, then starts it. The claim is first and fingerprinted, so of two jobs racing for one VM
    /// only one passes it, and a loser has written nothing (the scheduling write has no fingerprint of its own). A VM that is not stopped is
    /// refused (<c>VM_NOT_STOPPED</c>) before any change, and a VM someone else claimed first fails the label write (precondition failed, 412).
    /// Failures are classified like a create's: a stockout on the start is <see cref="CloudErrorKind.Stockout"/>. A failure AFTER the claim leaves
    /// the VM labelled for this job: the caller ends it. The call is NOT idempotent as a whole (a replay meets a VM that already started), so a
    /// resilience layer must not replay it; a <see cref="CloudErrorKind.Network"/> failure means the outcome is unknown and the caller looks by label.
    /// </summary>
    Task<VmDescriptor> ReuseVmAsync(VmSpec spec, string existingVmName, string zone, CancellationToken cancellationToken);

    /// <summary>
    /// Issue #670: the zones that offer <paramref name="acceleratorType"/> (<c>nvidia-l4</c>, see <see cref="ZoneCandidates.AcceleratorFor"/>),
    /// the zone universe the ladder builds its candidates from. A real gateway implements this with <c>acceleratorTypes.aggregatedList</c>
    /// filtered to that name. An empty list means "the gateway does not know", never "no zone has it": the caller then falls back to the
    /// zones the request names. A failure throws like any other read; the caller treats it the same as an empty answer.
    /// </summary>
    Task<IReadOnlyList<string>> ListAcceleratorZonesAsync(string acceleratorType, CancellationToken cancellationToken);

    /// <summary>
    /// Issue #105: starts a stopped VM AS IT IS (no label, scheduling or metadata rewrite: that is <see cref="ReuseVmAsync"/>). A VM that is gone is a
    /// <c>VM_NOT_FOUND</c> (404) error, never a silent success; a stockout on the start is <see cref="CloudErrorKind.Stockout"/>. Starting a VM that
    /// already runs is accepted by Compute and is a no-op. THEORY (unverified, ToTest #105): a VM stopped past its <c>maxRunDuration</c> is accepted.
    /// </summary>
    Task StartVmAsync(string vmName, string zone, CancellationToken cancellationToken);

    /// <summary>
    /// Issue #105: every persistent disk carrying <c>app=dna-entropy-graph</c> AND <c>installation-id=&lt;installationId&gt;</c>, across every zone, with size,
    /// status and the VMs holding it (a disk a deleted VM left behind is what keeps billing). Like the VM listings it fails when a zone cannot be read
    /// rather than return a partial list, and an id no label can hold answers an empty list without asking anyone. A real gateway implements this via
    /// <c>disks.aggregatedList</c> filtered on those two labels.
    /// </summary>
    Task<IReadOnlyList<DiskDescriptor>> ListDisksByInstallationAsync(string installationId, CancellationToken cancellationToken);

    /// <summary>Issue #105: deletes one disk. Already gone is a success; a disk still attached is an error. The caller reads back to verify (see <c>InstallationCleanup</c>).</summary>
    Task DeleteDiskAsync(string diskName, string zone, CancellationToken cancellationToken);

    Task<VmDescriptor?> GetVmAsync(string vmName, string zone, CancellationToken cancellationToken);

    Task StopVmAsync(string vmName, string zone, CancellationToken cancellationToken);

    Task DeleteVmAsync(string vmName, string zone, CancellationToken cancellationToken);

    /// <summary>
    /// Every VM labelled with this job id, across every zone - Hard Rule
    /// 9's "discovery is by label, never by a fixed name," and issue #257's
    /// retry-safety mechanism: a caller resuming after a crash calls this
    /// FIRST and adopts what it finds, rather than risking a second
    /// <see cref="CreateVmAsync"/> in a different zone succeeding
    /// independently (a VM name is unique only within a zone, not
    /// globally - see issue #389). A real gateway implements this via
    /// <c>instances.aggregatedList</c> filtered to <c>labels.job-id=&lt;jobId&gt;</c>
    /// and <c>labels.app=dna-entropy-graph</c>. More than one result means
    /// the exact leak #389 describes already happened.
    /// </summary>
    Task<IReadOnlyList<VmDescriptor>> FindByJobIdAsync(string jobId, CancellationToken cancellationToken);

    /// <summary>
    /// Every VM carrying <c>app=dna-entropy-graph</c> AND <c>installation-id=&lt;installationId&gt;</c>, across every zone (Hard Rule 9:
    /// by label, never by name; never another installation's VMs, because two users may share one Google account). Each descriptor
    /// carries its <see cref="VmDescriptor.Labels"/> and <see cref="VmDescriptor.StoppedAt"/>. A real gateway implements this via
    /// <c>instances.aggregatedList</c> filtered on those two labels. Used by the reconciler's idle-VM sweep (issue #530).
    /// </summary>
    Task<IReadOnlyList<VmDescriptor>> ListByInstallationAsync(string installationId, CancellationToken cancellationToken);

    /// <summary>
    /// Every VM carrying <c>app=dna-entropy-graph</c>, whichever installation or job it belongs to, across every zone. A real gateway
    /// implements this via <c>instances.aggregatedList</c> filtered on the app label alone and, like the other listings, fails when a
    /// zone cannot be read rather than return a partial list. For the CloudCli's inventory (issue #60) only: no run decides anything
    /// from another installation's VMs.
    /// </summary>
    Task<IReadOnlyList<VmDescriptor>> ListAppVmsAsync(CancellationToken cancellationToken);
}
