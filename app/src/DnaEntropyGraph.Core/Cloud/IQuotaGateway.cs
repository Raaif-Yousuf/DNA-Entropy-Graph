namespace DnaEntropyGraph.Core.Cloud;

/// <summary>
/// GPU quota is per-region and pre-filterable, distinct from a per-zone
/// stockout (docs/architecture.md Critical Pitfalls: "Quota is not
/// stockout"). See IComputeGateway for the Hard Rule 7 boundary.
/// </summary>
public interface IQuotaGateway
{
    /// <summary>
    /// GPUs of <paramref name="acceleratorType"/> still available in <paramref name="region"/>, with the project-wide GPUS_ALL_REGIONS
    /// cap applied as a ceiling. A quota that cannot be read throws (a <see cref="CloudOperationException"/>); it is never reported as 0.
    /// </summary>
    Task<int> GetGpuQuotaAsync(string projectId, string region, string acceleratorType, CancellationToken cancellationToken);

    /// <summary>
    /// The project's whole quota table (every region's metrics, and GPUS_ALL_REGIONS). A failed read is a value
    /// (<see cref="QuotaReadResult.Failures"/>, <see cref="QuotaState.Unknown"/>), so one failed half does not hide the other.
    /// Only cancellation throws.
    /// </summary>
    Task<QuotaReadResult> ReadQuotaAsync(string projectId, CancellationToken cancellationToken);

    /// <summary>
    /// Issue #99: whether the project may ask for more of <paramref name="metric"/> (<c>NVIDIA_L4_GPUS</c> or <c>GPUS_ALL_REGIONS</c>), read from the Cloud
    /// Quotas API's <c>quotaInfos</c> BEFORE any request. A metric with no known quota id is an <see cref="ArgumentException"/>; a quota id Google does not
    /// know, or an answer with no eligibility field, is <see cref="QuotaEligibility.IsEligible"/> null (unknown), never true.
    /// </summary>
    Task<QuotaEligibility> GetQuotaIncreaseEligibilityAsync(string projectId, string metric, CancellationToken cancellationToken);

    /// <summary>
    /// Issue #99: creates the quota preference (the Cloud Quotas API's <c>quotaPreferences.create</c>) described by <paramref name="request"/>. The preference id
    /// is the caller's, so a repeat is an <see cref="CloudErrorKind.AlreadyExists"/> failure the caller reads as "already asked". Read eligibility first
    /// (<see cref="GetQuotaIncreaseEligibilityAsync"/>); <see cref="GpuQuotaRequester"/> is the one caller that does.
    /// </summary>
    Task<QuotaIncreaseResult> RequestQuotaIncreaseAsync(string projectId, QuotaIncreaseRequest request, CancellationToken cancellationToken);
}
