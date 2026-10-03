namespace DnaEntropyGraph.Core.Cloud;

/// <summary>
/// First-run wizard step 7 (issue #54): the identity the VM runs as. A VM that can stop or delete itself, and write its
/// results to the one bucket, and do nothing else. See IComputeGateway for the Hard Rule 7 boundary.
/// <para>
/// <see cref="EnsureWorkerIdentityAsync"/> is idempotent and adopts what is already there: it creates the service
/// account <c>dna-entropy-worker</c> (or adopts one that exists), creates (or repairs, or un-deletes) the custom role
/// <see cref="WorkerIdentityNames.RoleId"/>, binds that role to the account at the project with a condition that only
/// matches VMs named <c>deg-*</c>, and binds <c>roles/storage.objectAdmin</c> on the results bucket only. Bindings it
/// did not add are never touched. When an organization policy refuses service-account creation it does the same
/// bindings for the project's default Compute Engine account and says so in <see cref="WorkerIdentity.NoteCode"/>.
/// Service accounts and custom roles cannot carry labels, so Hard Rule 10's labels do not apply to them (docs/hard_rules.md).
/// </para>
/// </summary>
public interface IWorkerIdentityGateway
{
    Task<WorkerIdentity> EnsureWorkerIdentityAsync(string projectId, string bucket, CancellationToken cancellationToken);
}

/// <summary>
/// The identity a VM is created with. <see cref="NoteCode"/> is null for the app's own account and
/// <see cref="SetupErrorCodes.WorkerDefaultAccount"/> when the default Compute Engine account stands in: the wizard
/// shows that as a yellow note (the message is <c>SetupErrorCodes.ResourceKey(NoteCode)</c>), not as a failure.
/// </summary>
public sealed record WorkerIdentity(string ServiceAccountEmail, string? NoteCode)
{
    public bool IsDefaultComputeAccount => NoteCode == SetupErrorCodes.WorkerDefaultAccount;
}

/// <summary>The names and the exact access of the worker identity (one place, so the gateway, the fake, the docs and the tests agree).</summary>
public static class WorkerIdentityNames
{
    public const string ServiceAccountId = "dna-entropy-worker";

    /// <summary>The custom role's id: <c>projects/&lt;project&gt;/roles/dnaEntropyWorker</c>.</summary>
    public const string RoleId = "dnaEntropyWorker";

    public const string BucketRole = "roles/storage.objectAdmin";

    /// <summary>The VM name prefix <see cref="VmSpec.VmName"/> gives every VM (<c>deg-&lt;jobId&gt;</c>).</summary>
    public const string VmNamePrefix = "deg-";

    /// <summary>
    /// Exactly what the custom role grants. <c>worker/vm/startup.sh</c> calls only <c>instances.delete</c> and
    /// <c>instances.stop</c> on itself (both with the metadata token, nothing else on Compute); <c>get</c> and
    /// <c>zoneOperations.get</c> are what the issue adds so the worker may read its own state and the operation a stop
    /// or delete returns.
    /// </summary>
    public static IReadOnlyList<string> RolePermissions { get; } =
    [
        "compute.instances.delete",
        "compute.instances.get",
        "compute.instances.stop",
        "compute.zoneOperations.get",
    ];

    public const string ConditionTitle = "Only dna-entropy-graph VMs";

    /// <summary>
    /// The IAM condition on the project binding: a VM (an Instance resource) must have a <c>deg-</c> name, so the account
    /// cannot touch the user's other VMs. Any other resource type the role's permissions are checked against (a zone
    /// operation) passes. THEORY (unverified, no live project): that Compute evaluates this for instances.stop and
    /// instances.delete and that a zone operation read is not blocked by it; docs/ToTest.md carries the row.
    /// </summary>
    public static string ConditionExpression { get; } =
        $"resource.type != \"compute.googleapis.com/Instance\" || resource.name.contains(\"/instances/{VmNamePrefix}\")";

    public static string ServiceAccountEmail(string projectId) => $"{ServiceAccountId}@{projectId}.iam.gserviceaccount.com";

    public static string DefaultComputeAccountEmail(string projectNumber) => $"{projectNumber}-compute@developer.gserviceaccount.com";

    /// <summary>The member string an IAM binding uses.</summary>
    public static string Member(string email) => "serviceAccount:" + email;

    public static string ProjectRoleName(string projectId) => $"projects/{projectId}/roles/{RoleId}";
}
