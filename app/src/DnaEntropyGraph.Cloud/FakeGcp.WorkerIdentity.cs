using DnaEntropyGraph.Core.Cloud;

namespace DnaEntropyGraph.Cloud;

// The worker identity (issue #54): IWorkerIdentityGateway with scripted failures. A partial file, like the project catalogue,
// so the wizard-setup scenarios do not grow the runner fake any further.
public sealed partial class FakeGcp : IWorkerIdentityGateway
{
    private readonly object _identityGate = new();
    private readonly HashSet<string> _workerAccounts = new(StringComparer.Ordinal);
    private int _workerIdentityEnsureCalls;
    private bool _serviceAccountCreationOrgBlocked;
    private bool _workerIdentityPermissionDenied;
    private bool _workerIdentityActAsDenied;

    /// <summary>How many times <see cref="EnsureWorkerIdentityAsync"/> reached Google (a wizard that runs the step twice shows here).</summary>
    public int WorkerIdentityEnsureCalls
    {
        get
        {
            lock (_identityGate)
            {
                return _workerIdentityEnsureCalls;
            }
        }
    }

    /// <summary>How many service accounts were actually created (a second call adopts the first, so it stays at one per project).</summary>
    public int WorkerAccountsCreated
    {
        get
        {
            lock (_identityGate)
            {
                return _workerAccounts.Count;
            }
        }
    }

    /// <summary>An organization policy refuses service-account creation: the identity falls back to the default Compute Engine account, with <see cref="SetupErrorCodes.WorkerDefaultAccount"/>.</summary>
    public FakeGcp WithServiceAccountCreationBlockedByOrgPolicy()
    {
        _serviceAccountCreationOrgBlocked = true;
        return this;
    }

    /// <summary>The signed-in account may not create service accounts or edit the project's IAM policy: a permission error naming <see cref="SetupErrorCodes.Permission"/>.</summary>
    public FakeGcp WithWorkerIdentityPermissionDenied()
    {
        _workerIdentityPermissionDenied = true;
        return this;
    }

    /// <summary>The signed-in account may not act as the worker account: a permission error naming <see cref="SetupErrorCodes.PermissionActAs"/>.</summary>
    public FakeGcp WithWorkerIdentityActAsDenied()
    {
        _workerIdentityActAsDenied = true;
        return this;
    }

    /// <summary>The project number this fake gives <paramref name="projectId"/>: twelve digits, stable per id (it names the default Compute Engine account).</summary>
    public static string ProjectNumberFor(string projectId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);

        var hash = 17UL;
        foreach (var c in projectId)
        {
            hash = (hash * 31) + c;
        }

        return (100_000_000_000UL + (hash % 900_000_000_000UL)).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    public Task<WorkerIdentity> EnsureWorkerIdentityAsync(string projectId, string bucket, CancellationToken cancellationToken)
    {
        ThrowIfScriptedTransient();
        ThrowIfCatalogNotConnected();
        lock (_identityGate)
        {
            _workerIdentityEnsureCalls++;
        }

        if (_workerIdentityPermissionDenied)
        {
            throw Build(CloudErrorKind.Permission, SetupErrorCodes.Permission, 403, "Permission 'iam.serviceAccounts.create' denied on resource (or it may not exist).");
        }

        if (_workerIdentityActAsDenied)
        {
            throw Build(CloudErrorKind.Permission, SetupErrorCodes.PermissionActAs, 403, "Required 'iam.serviceAccounts.actAs' permission for the worker service account.");
        }

        if (_serviceAccountCreationOrgBlocked)
        {
            return Task.FromResult(new WorkerIdentity(WorkerIdentityNames.DefaultComputeAccountEmail(ProjectNumberFor(projectId)), SetupErrorCodes.WorkerDefaultAccount));
        }

        lock (_identityGate)
        {
            // IWorkerIdentityGateway: an account that already exists is adopted, never created twice.
            _workerAccounts.Add(projectId);
        }

        return Task.FromResult(new WorkerIdentity(WorkerIdentityNames.ServiceAccountEmail(projectId), null));
    }
}
