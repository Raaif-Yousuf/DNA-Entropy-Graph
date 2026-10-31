using DnaEntropyGraph.Core.Abstractions;

namespace DnaEntropyGraph.Persistence;

/// <summary><see cref="IProjectSettingsStore"/> over the <c>Projects</c> and <c>AccountProjects</c> tables: each remembered value is its own one-column statement.</summary>
public sealed class ProjectSettingsStore(IProjectRepository projects) : IProjectSettingsStore
{
    public async Task<IReadOnlyList<ProjectSettings>> GetKnownAsync(string? accountSub, CancellationToken cancellationToken)
        => string.IsNullOrWhiteSpace(accountSub)
            ? []
            : (await projects.GetOfAccountAsync(accountSub, cancellationToken).ConfigureAwait(false))
                .Select(ToSettings)
                .OrderBy(p => p.ProjectId, StringComparer.Ordinal)
                .ToList();

    public async Task<ProjectSettings?> GetAsync(string projectId, CancellationToken cancellationToken)
        => (await projects.GetAllAsync(cancellationToken).ConfigureAwait(false)).Where(p => p.ProjectId == projectId).Select(ToSettings).FirstOrDefault();

    public Task EnsureAsync(string projectId, string? accountSub, CancellationToken cancellationToken)
        => string.IsNullOrWhiteSpace(accountSub) ? projects.EnsureAsync(projectId, cancellationToken) : projects.ClaimAsync(projectId, accountSub, cancellationToken);

    public Task RememberBucketAsync(string projectId, string bucket, CancellationToken cancellationToken) => projects.SetBucketAsync(projectId, bucket, cancellationToken);

    private static ProjectSettings ToSettings(ProjectRecord row) => new(row.ProjectId, row.DisplayName, row.Bucket);
}
