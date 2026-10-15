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
    /// Issue #94: starts a STOPPED VM this installation left behind for <paramref name="spec"/>'s job: replaces its scheduling limits and its
    /// metadata (the startup script and <c>deg-*</c> attributes) with the spec's, then claims it by rewriting its labels to the spec's
    /// (so discovery by <c>job-id</c> finds it, Hard Rule 9), then starts it. The claim is the LAST edit before the start, so a failure
    /// before it leaves the VM labelled as its old job. A VM that is not stopped is refused (<c>VM_NOT_STOPPED</c>) before any change, and
    /// a VM someone else claimed first fails the label write (precondition failed). Failures are classified like a create's: a stockout on
    /// the start is <see cref="CloudErrorKind.Stockout"/>. A failure AFTER the claim leaves the VM labelled for this job: the caller ends it.
    /// </summary>
    Task<VmDescriptor> ReuseVmAsync(VmSpec spec, string existingVmName, string zone, CancellationToken cancellationToken);

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
