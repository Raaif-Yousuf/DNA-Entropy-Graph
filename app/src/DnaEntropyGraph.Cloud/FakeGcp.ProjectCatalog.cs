using DnaEntropyGraph.Core.Cloud;

namespace DnaEntropyGraph.Cloud;

// The account's project catalogue (issue #50): IProjectCatalogGateway with scripted failures. A partial file so the
// wizard-setup scenarios (#50 to #52) do not grow the 1,200-line runner fake any further.
public sealed partial class FakeGcp : IProjectCatalogGateway, IBillingGateway, IServiceEnablementGateway
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

    // ----------------------------------------------------------------
    // IBillingGateway (issue #51). Billing state is the same set WithBillingOff fills, so a link flips
    // IsBillingEnabledAsync (the preflight step) and GetBillingStatusAsync (the wizard step) together.
    // ----------------------------------------------------------------

    private readonly List<BillingAccountSummary> _billingAccounts = [];
    private readonly Dictionary<string, string> _linkedAccounts = new(StringComparer.Ordinal);
    private int _billingLinkCalls;
    private bool _billingLinkDenied;
    private bool _billingLinkDoesNotEnable;

    /// <summary>How many times <see cref="LinkProjectAsync"/> reached Google (a wizard that links twice, or links when billing was already on, shows here).</summary>
    public int BillingLinkCalls => _billingLinkCalls;

    /// <summary>The signed-in user can see this open billing account.</summary>
    public FakeGcp WithBillingAccount(string accountId, string displayName)
    {
        lock (_catalogGate)
        {
            _billingAccounts.Add(new BillingAccountSummary(accountId, displayName));
        }

        return this;
    }

    /// <summary>Linking throws <see cref="SetupErrorCodes.BillingNoPermission"/>: the user may use the account but not link projects to it.</summary>
    public FakeGcp WithBillingLinkPermissionDenied()
    {
        _billingLinkDenied = true;
        return this;
    }

    /// <summary>Google accepts the link but billing stays off (an account in a state that cannot bill).</summary>
    public FakeGcp WithBillingLinkThatDoesNotEnable()
    {
        _billingLinkDoesNotEnable = true;
        return this;
    }

    public Task<BillingStatus> GetBillingStatusAsync(string projectId, CancellationToken cancellationToken)
    {
        ThrowIfScriptedTransient();
        ThrowIfCatalogNotConnected();
        lock (_catalogGate)
        {
            var enabled = !_billingOffProjects.Contains(projectId);
            return Task.FromResult(new BillingStatus(enabled, _linkedAccounts.GetValueOrDefault(projectId)));
        }
    }

    public Task<IReadOnlyList<BillingAccountSummary>> ListOpenBillingAccountsAsync(CancellationToken cancellationToken)
    {
        ThrowIfScriptedTransient();
        ThrowIfCatalogNotConnected();
        lock (_catalogGate)
        {
            return Task.FromResult<IReadOnlyList<BillingAccountSummary>>(_billingAccounts.ToList());
        }
    }

    public Task LinkProjectAsync(string projectId, string billingAccountId, CancellationToken cancellationToken)
    {
        ThrowIfScriptedTransient();
        ThrowIfCatalogNotConnected();
        Interlocked.Increment(ref _billingLinkCalls);
        if (_billingLinkDenied)
        {
            throw Build(CloudErrorKind.Permission, SetupErrorCodes.BillingNoPermission, 403, "The caller does not have permission to link this billing account.");
        }

        lock (_catalogGate)
        {
            _linkedAccounts[projectId] = billingAccountId;
            if (!_billingLinkDoesNotEnable)
            {
                _billingOffProjects.Remove(projectId);
            }
        }

        return Task.CompletedTask;
    }

    // ----------------------------------------------------------------
    // IServiceEnablementGateway (issue #52). Compute shares its switch with WithComputeApiOff and
    // IsComputeApiEnabledAsync, so the wizard step and the preflight step cannot disagree.
    // ----------------------------------------------------------------

    private readonly HashSet<(string Project, string Service)> _disabledServices = [];
    private readonly Dictionary<(string Project, string Service), int> _enablingPollsLeft = [];
    private readonly HashSet<string> _notOwnerProjects = new(StringComparer.Ordinal);
    private int _enablementDelayPolls;

    /// <summary>The project has this Google service switched off (Compute is the same switch as <see cref="WithComputeApiOff"/>).</summary>
    public FakeGcp WithServiceDisabled(string projectId, string serviceId)
    {
        lock (_catalogGate)
        {
            if (serviceId == RequiredServices.Compute)
            {
                _apiDisabledProjects.Add(projectId);
            }
            else
            {
                _disabledServices.Add((projectId, serviceId));
            }
        }

        return this;
    }

    /// <summary>
    /// After <see cref="EnableServicesAsync"/> returns, a service reads off for the next <paramref name="polls"/>
    /// <see cref="IsServiceEnabledAsync"/> checks: the gap a real operation leaves between "done" and "ENABLED".
    /// The real gateway waits that gap out; this is for a caller that checks by itself.
    /// </summary>
    public FakeGcp WithServiceEnablementDelay(int polls)
    {
        _enablementDelayPolls = polls;
        return this;
    }

    /// <summary>The user is a member of the project but not its Owner: enabling throws <see cref="SetupErrorCodes.NotProjectOwner"/>.</summary>
    public FakeGcp WithNotProjectOwner(string projectId)
    {
        _notOwnerProjects.Add(projectId);
        return this;
    }

    public Task<bool> IsServiceEnabledAsync(string projectId, string serviceId, CancellationToken cancellationToken)
    {
        ThrowIfScriptedTransient();
        ThrowIfCatalogNotConnected();
        lock (_catalogGate)
        {
            if (_enablingPollsLeft.TryGetValue((projectId, serviceId), out var left) && left > 0)
            {
                _enablingPollsLeft[(projectId, serviceId)] = left - 1;
                return Task.FromResult(false);
            }

            var off = serviceId == RequiredServices.Compute ? _apiDisabledProjects.Contains(projectId) : _disabledServices.Contains((projectId, serviceId));
            return Task.FromResult(!off);
        }
    }

    public Task EnableServicesAsync(string projectId, IReadOnlyList<string> serviceIds, CancellationToken cancellationToken)
    {
        ThrowIfScriptedTransient();
        ThrowIfCatalogNotConnected();
        lock (_catalogGate)
        {
            if (_notOwnerProjects.Contains(projectId))
            {
                throw Build(CloudErrorKind.Permission, SetupErrorCodes.NotProjectOwner, 403, "Permission denied to enable service.");
            }

            foreach (var serviceId in serviceIds)
            {
                var wasOff = serviceId == RequiredServices.Compute ? _apiDisabledProjects.Remove(projectId) : _disabledServices.Remove((projectId, serviceId));
                if (wasOff && _enablementDelayPolls > 0)
                {
                    _enablingPollsLeft[(projectId, serviceId)] = _enablementDelayPolls;
                }
            }
        }

        return Task.CompletedTask;
    }
}
