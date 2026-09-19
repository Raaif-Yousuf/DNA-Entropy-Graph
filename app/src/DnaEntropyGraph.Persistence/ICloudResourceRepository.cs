namespace DnaEntropyGraph.Persistence;

/// <summary>
/// The <c>CloudResources</c> table (Appendix A section 3): every VM, disk,
/// bucket, service account and image this installation has ever created,
/// discovered by label rather than by a fixed name (Hard Rule 9) and this
/// is where that discovery result is cached locally for the Cloud page
/// (issue #101/#141's sibling work).
///
/// Lives in <c>DnaEntropyGraph.Persistence</c> rather than
/// <c>DnaEntropyGraph.Core.Abstractions</c> for now: this lane (#67) does
/// not own Core/Abstractions beyond <c>IRunRepository</c>/<c>ISettingsStore</c>
/// (WAVE_BRIEF.md section on path ownership), and no ViewModel consumes this
/// yet, so registering it in the DI container tonight would itself be a
/// wired-to-nothing service (registered, never resolved). Move this
/// interface (unchanged) into Core.Abstractions the moment a Cloud
/// Resources ViewModel needs to depend on it.
/// </summary>
public interface ICloudResourceRepository
{
    Task<IReadOnlyList<CloudResourceRecord>> GetAllAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Inserts a new resource, or replaces every column of an existing one
    /// keyed by the table's own <c>UNIQUE(Kind, ProjectId, Name)</c>
    /// constraint. <see cref="CloudResourceRecord.CreatedAt"/> and
    /// <see cref="CloudResourceRecord.CreatedByInstall"/> are preserved
    /// across an update.
    /// </summary>
    Task UpsertAsync(CloudResourceRecord resource, CancellationToken cancellationToken);
}

/// <summary>One row of the <c>CloudResources</c> table.</summary>
public sealed record CloudResourceRecord(
    string Kind,
    string Name,
    string? ProjectId,
    DateTimeOffset CreatedAt,
    string? Location = null,
    string? CreatedByInstall = null,
    string? LastKnownState = null,
    DateTimeOffset? LastCheckedAt = null,
    string? MachineType = null,
    string? GpuType = null,
    bool? IsSpot = null,
    int? DiskGb = null,
    double? HourlyRateUsd = null,
    long RunningSecondsAccum = 0,
    DateTimeOffset? LastStartedAt = null,
    string? CurrentJobId = null,
    string? LifecyclePolicy = null,
    DateTimeOffset? KeepUntilUtc = null,
    DateTimeOffset? DeletedAt = null,
    string? LabelsJson = null);
