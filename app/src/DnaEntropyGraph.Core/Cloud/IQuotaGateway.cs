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
}
