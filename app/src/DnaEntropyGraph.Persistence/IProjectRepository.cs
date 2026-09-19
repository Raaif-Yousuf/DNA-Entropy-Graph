namespace DnaEntropyGraph.Persistence;

/// <summary>
/// The <c>Projects</c> table (Appendix A section 3): one row per Google
/// Cloud project the user has connected, including its billing/API/quota
/// preflight results and its own results bucket. See
/// <c>ICloudResourceRepository</c> for why this lives in
/// <c>DnaEntropyGraph.Persistence</c> rather than <c>Core.Abstractions</c>
/// tonight - the same reasoning applies here (no ViewModel consumes it yet).
/// </summary>
public interface IProjectRepository
{
    Task<IReadOnlyList<ProjectRecord>> GetAllAsync(CancellationToken cancellationToken);

    /// <summary>Inserts a new project, or replaces every column of an existing one keyed by <see cref="ProjectRecord.ProjectId"/>.</summary>
    Task UpsertAsync(ProjectRecord project, CancellationToken cancellationToken);
}

/// <summary>One row of the <c>Projects</c> table.</summary>
public sealed record ProjectRecord(
    string ProjectId,
    string? ProjectNumber = null,
    string? DisplayName = null,
    string? AccountSub = null,
    string? HomeRegionGroup = null,
    string? Bucket = null,
    string? WorkerSaEmail = null,
    bool? BillingEnabled = null,
    string? ApisEnabledJson = null,
    string? QuotaJson = null,
    DateTimeOffset? QuotaCheckedAt = null,
    string? LastGoodZone = null,
    DateTimeOffset? SetupCompletedAt = null,
    DateTimeOffset? SmokeTestPassedAt = null,
    bool? IsActive = null);
