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

    /// <summary>
    /// Makes sure a row exists for <paramref name="projectId"/>, inserting a bare one (id only) when it
    /// does not and leaving an existing row untouched (unlike <see cref="UpsertAsync"/>, which replaces
    /// every column). <c>Runs.ProjectId</c> references this table with foreign keys on (issue #532), so
    /// a run can only be written for a project that has a row.
    /// </summary>
    Task EnsureAsync(string projectId, CancellationToken cancellationToken);

    /// <summary>
    /// Makes sure a row exists for <paramref name="projectId"/> and that <paramref name="accountSub"/> has it in its own list (issue #260). Several accounts may
    /// choose one project. The row's own <c>AccountSub</c> is filled only while empty and never rewritten. The account's row is inserted first with only its
    /// <c>Sub</c> (no email is stored here).
    /// </summary>
    Task ClaimAsync(string projectId, string accountSub, CancellationToken cancellationToken);

    /// <summary>The projects <paramref name="accountSub"/> has chosen (see <see cref="ClaimAsync"/>).</summary>
    Task<IReadOnlyList<ProjectRecord>> GetOfAccountAsync(string accountSub, CancellationToken cancellationToken);

    /// <summary>Sets the <c>Bucket</c> column of <paramref name="projectId"/> (inserting a bare row when none exists) and no other column, so it cannot overwrite a concurrent change to another one.</summary>
    Task SetBucketAsync(string projectId, string bucket, CancellationToken cancellationToken);
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
