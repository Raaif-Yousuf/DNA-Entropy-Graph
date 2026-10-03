namespace DnaEntropyGraph.Core.Cloud;

/// <summary>
/// First-run wizard step 3 (issue #50): find the Google Cloud project the app will use, or make one. Kept apart from
/// <see cref="IProjectSetupGateway"/> (the preflight chain a run walks) because listing and creating projects is
/// the account's catalogue, not one project's health. See IComputeGateway for the Hard Rule 7 boundary.
/// A failure is a <see cref="CloudOperationException"/>: <see cref="SetupErrorCodes.ProjectQuota"/> when Google's
/// project limit is hit, <see cref="SetupErrorCodes.OrgPolicyBlock"/> when an organization policy refuses.
/// </summary>
public interface IProjectCatalogGateway
{
    /// <summary>
    /// Every project the signed-in account can see whose state is ACTIVE, the ones this app made (labelled
    /// <c>app=dna-entropy-graph</c>) first (<see cref="ProjectCatalogOrder"/>). Empty for an account with no projects.
    /// </summary>
    Task<IReadOnlyList<ProjectSummary>> ListActiveProjectsAsync(CancellationToken cancellationToken);

    /// <summary>One project, or null when it cannot be described at all (wrong id, no access, deleted past recovery).</summary>
    Task<ProjectSummary?> GetProjectAsync(string projectId, CancellationToken cancellationToken);

    /// <summary>
    /// Creates a project with the <c>app=dna-entropy-graph</c> and <c>installation-id</c> labels and waits for Google's
    /// long-running operation to finish; the real error (project limit, organization policy) is in the polled
    /// operation, not the first response. Safe to call again with the same <paramref name="projectId"/> after a
    /// dropped connection: a project of this app that already exists is returned, never duplicated.
    /// </summary>
    Task<ProjectSummary> CreateProjectAsync(string projectId, string displayName, string installationId, CancellationToken cancellationToken);
}

/// <summary>A project as the wizard lists it. <paramref name="IsAppProject"/> is true when it carries the label <c>app=dna-entropy-graph</c>.</summary>
public sealed record ProjectSummary(string ProjectId, string DisplayName, ProjectLifecycleState State, bool IsAppProject);

/// <summary>"Prefer projects labelled app=dna-entropy-graph": the one ordering rule the real gateway and the fake share.</summary>
public static class ProjectCatalogOrder
{
    public static IReadOnlyList<ProjectSummary> Prefer(IEnumerable<ProjectSummary> projects)
    {
        ArgumentNullException.ThrowIfNull(projects);
        return projects
            .OrderByDescending(p => p.IsAppProject)
            .ThenBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.ProjectId, StringComparer.Ordinal)
            .ToList();
    }
}
