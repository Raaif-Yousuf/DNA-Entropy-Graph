namespace DnaEntropyGraph.Core.Cloud;

/// <summary>
/// GPU quota is per-region and pre-filterable, distinct from a per-zone
/// stockout (docs/architecture.md Critical Pitfalls: "Quota is not
/// stockout"). See IComputeGateway for the Hard Rule 7 boundary.
/// </summary>
public interface IQuotaGateway
{
    Task<int> GetGpuQuotaAsync(string projectId, string region, string acceleratorType, CancellationToken cancellationToken);
}
