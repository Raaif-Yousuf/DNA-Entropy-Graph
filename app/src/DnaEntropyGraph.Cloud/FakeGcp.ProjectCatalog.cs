using DnaEntropyGraph.Core.Cloud;

namespace DnaEntropyGraph.Cloud;

// The account's project catalogue (issue #50): IProjectCatalogGateway with scripted failures. A partial file so the
// wizard-setup scenarios (#50 to #52) do not grow the 1,200-line runner fake any further.
public sealed partial class FakeGcp : IProjectCatalogGateway
{
    private readonly object _catalogGate = new();
    private readonly Dictionary<string, ProjectSummary> _catalog = new(StringComparer.Ordinal);
    private bool _projectQuotaExhausted;
    private bool _projectCreationOrgBlocked;

    /// <summary>The signed-in account already has this project (not made by the app, so not labelled).</summary>
    public FakeGcp WithExistingProject(string projectId, string displayName)
    {
        lock (_catalogGate)
        {
            _catalog[projectId] = new ProjectSummary(projectId, displayName, ProjectLifecycleState.Active, IsAppProject: false);
        }

        return this;
    }

    /// <summary>Google refuses another project: the account is at its project limit. <see cref="CreateProjectAsync"/> throws <see cref="SetupErrorCodes.ProjectQuota"/>.</summary>
    public FakeGcp WithProjectCreationQuotaExhausted()
    {
        _projectQuotaExhausted = true;
        return this;
    }

    /// <summary>An organization policy refuses project creation. <see cref="CreateProjectAsync"/> throws <see cref="SetupErrorCodes.OrgPolicyBlock"/>.</summary>
    public FakeGcp WithProjectCreationBlockedByOrgPolicy()
    {
        _projectCreationOrgBlocked = true;
        return this;
    }

    public Task<IReadOnlyList<ProjectSummary>> ListActiveProjectsAsync(CancellationToken cancellationToken)
    {
        ThrowIfScriptedTransient();
        ThrowIfCatalogNotConnected();
        lock (_catalogGate)
        {
            return Task.FromResult(ProjectCatalogOrder.Prefer(_catalog.Values.Where(p => p.State == ProjectLifecycleState.Active)));
        }
    }

    public Task<ProjectSummary?> GetProjectAsync(string projectId, CancellationToken cancellationToken)
    {
        ThrowIfScriptedTransient();
        ThrowIfCatalogNotConnected();
        lock (_catalogGate)
        {
            return Task.FromResult(_catalog.GetValueOrDefault(projectId));
        }
    }

    public Task<ProjectSummary> CreateProjectAsync(string projectId, string displayName, string installationId, CancellationToken cancellationToken)
    {
        ThrowIfScriptedTransient();
        ThrowIfCatalogNotConnected();
        if (_projectQuotaExhausted)
        {
            throw Build(CloudErrorKind.Quota, SetupErrorCodes.ProjectQuota, 429, "Project creation quota exceeded.");
        }

        if (_projectCreationOrgBlocked)
        {
            throw Build(CloudErrorKind.OrgPolicy, SetupErrorCodes.OrgPolicyBlock, 400, "Operation denied by organization policy constraints/resourcemanager.projectCreation.");
        }

        lock (_catalogGate)
        {
            if (_catalog.TryGetValue(projectId, out var existing))
            {
                // IProjectCatalogGateway.CreateProjectAsync: a replay (the first attempt made it) returns the app's own project.
                if (existing is { IsAppProject: true, State: ProjectLifecycleState.Active })
                {
                    return Task.FromResult(existing);
                }

                throw Build(CloudErrorKind.AlreadyExists, "ALREADY_EXISTS", 409, "Requested entity already exists.");
            }

            var created = new ProjectSummary(projectId, displayName, ProjectLifecycleState.Active, IsAppProject: true);
            _catalog[projectId] = created;
            return Task.FromResult(created);
        }
    }

    private void ThrowIfCatalogNotConnected()
    {
        if (_notConnected)
        {
            throw Build(CloudErrorKind.Other, NotConnectedErrorCode, null, "No Google Cloud connection is built into this version.");
        }
    }
}
