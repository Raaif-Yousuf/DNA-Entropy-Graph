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
    Task<int> GetGpuQuotaAsync(CloudScope scope, string region, string acceleratorType, CancellationToken cancellationToken);

    /// <summary>
    /// The project's whole quota table (every region's metrics, and GPUS_ALL_REGIONS). A failed read is a value
    /// (<see cref="QuotaReadResult.Failures"/>, <see cref="QuotaState.Unknown"/>), so one failed half does not hide the other.
    /// Only cancellation throws.
    /// </summary>
    Task<QuotaReadResult> ReadQuotaAsync(CloudScope scope, CancellationToken cancellationToken);

    /// <summary>
    /// Issue #99: whether the project may ask for more of <paramref name="metric"/> (<c>NVIDIA_L4_GPUS</c> or <c>GPUS_ALL_REGIONS</c>), read from the Cloud
    /// Quotas API's <c>quotaInfos</c> BEFORE any request. A metric with no known quota id is an <see cref="ArgumentException"/>; a quota id Google does not
    /// know, or an answer with no eligibility field, is <see cref="QuotaEligibility.IsEligible"/> null (unknown), never true.
    /// </summary>
    Task<QuotaEligibility> GetQuotaIncreaseEligibilityAsync(CloudScope scope, string metric, CancellationToken cancellationToken);

    /// <summary>
    /// Issue #99: asks for the quota <paramref name="request"/> describes, idempotently. The API allows one preference per quota and dimensions, so this first looks for
    /// an existing one (any id, matched on service, quota id and dimensions): one that already asks for at least the value is returned as it is
    /// (<see cref="QuotaIncreaseResult.AlreadyExisted"/>, nothing written); a lower one is raised in place (<c>PATCH</c> with its etag); none is created under the
    /// caller's preference id (a create that meets an existing id is an <see cref="CloudErrorKind.AlreadyExists"/> failure the caller reads as "already asked").
    /// Read eligibility first (<see cref="GetQuotaIncreaseEligibilityAsync"/>); <see cref="GpuQuotaRequester"/> is the one caller that does.
    /// </summary>
    Task<QuotaIncreaseResult> RequestQuotaIncreaseAsync(CloudScope scope, QuotaIncreaseRequest request, CancellationToken cancellationToken);
}
