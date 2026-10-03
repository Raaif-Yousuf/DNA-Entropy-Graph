using DnaEntropyGraph.Core.Cloud;
using Google;
using Google.Apis.Compute.v1;
using ComputeData = Google.Apis.Compute.v1.Data;

namespace DnaEntropyGraph.Cloud.Rest;

/// <summary>
/// The real <see cref="IQuotaGateway"/> over Compute Engine v1 REST (issue #88, docs/cloud_design.md section 6). Two reads, each one small
/// HTTP request per page that goes through <see cref="CloudCallPipeline"/> on its own (<c>singleRequest: true</c>, so a hung connection is cut
/// per attempt and retried): <c>regions.list</c> for every region's quotas (NVIDIA_L4_GPUS and the other GPU metrics, CPUS, SSD_TOTAL_GB),
/// paged, and <c>projects.get</c> for the project-wide GPUS_ALL_REGIONS line. The two fail independently and a failure is a value in
/// <see cref="QuotaReadResult.Failures"/> (UNKNOWN), never a zero: a project that cannot be read is not a project with no quota.
/// Not wrapped in <see cref="ResilientQuotaGateway"/> (each HTTP call already goes through the pipeline). THEORY (unverified, no live
/// project): that <c>projects.get</c> lists GPUS_ALL_REGIONS for a project that has never raised it; if it is absent the cap is reported
/// as a failed read (<c>GPUS_ALL_REGIONS_MISSING</c>), so the answer is UNKNOWN rather than a guess. docs/ToTest.md carries the real-project row.
/// </summary>
internal sealed class GoogleQuotaGateway : IQuotaGateway
{
    private readonly ComputeService _service;
    private readonly CloudCallPipeline _pipeline;

    public GoogleQuotaGateway(ComputeService service, CloudCallPipeline pipeline)
    {
        _service = service;
        _pipeline = pipeline;
    }

    public async Task<int> GetGpuQuotaAsync(string projectId, string region, string acceleratorType, CancellationToken cancellationToken)
    {
        var metric = QuotaMetrics.ForAccelerator(acceleratorType)
            ?? throw new CloudOperationException(
                new CloudError("QUOTA_ACCELERATOR_UNKNOWN", null, $"No quota metric is known for the accelerator type '{acceleratorType}'."),
                CloudErrorKind.Other);

        var result = await ReadQuotaAsync(projectId, cancellationToken).ConfigureAwait(false);
        if (result.GpuRemaining(region, metric) is not { } remaining)
        {
            // Unknown is an error to the caller, never a 0 that a preflight would read as "no quota".
            throw result.Failures.FirstOrDefault() is { } failure
                ? new CloudOperationException(failure.Error, failure.Kind)
                : new CloudOperationException(new CloudError("QUOTA_REGION_UNKNOWN", null, $"The quota read did not include the region '{region}'."), CloudErrorKind.Other);
        }

        return (int)Math.Floor(remaining);
    }

    public async Task<QuotaReadResult> ReadQuotaAsync(string projectId, CancellationToken cancellationToken)
    {
        var failures = new List<QuotaReadFailure>();

        IReadOnlyDictionary<string, IReadOnlyList<QuotaMetric>>? regions = null;
        try
        {
            regions = await ListRegionsAsync(projectId, cancellationToken).ConfigureAwait(false);
        }
        catch (CloudOperationException ex)
        {
            failures.Add(new QuotaReadFailure("regions.list", ex.Error, ex.Kind));
        }

        QuotaMetric? allRegions = null;
        try
        {
            allRegions = await GetAllRegionsGpusAsync(projectId, cancellationToken).ConfigureAwait(false);
            if (allRegions is null)
            {
                failures.Add(new QuotaReadFailure(
                    "projects.get",
                    new CloudError("GPUS_ALL_REGIONS_MISSING", null, "Compute Engine did not report the project-wide GPU limit (GPUS_ALL_REGIONS)."),
                    CloudErrorKind.Other));
            }
        }
        catch (CloudOperationException ex)
        {
            failures.Add(new QuotaReadFailure("projects.get", ex.Error, ex.Kind));
        }

        return new QuotaReadResult(regions, allRegions, failures);
    }

    private async Task<IReadOnlyDictionary<string, IReadOnlyList<QuotaMetric>>> ListRegionsAsync(string projectId, CancellationToken cancellationToken)
    {
        var table = new Dictionary<string, IReadOnlyList<QuotaMetric>>(StringComparer.OrdinalIgnoreCase);
        string? pageToken = null;
        do
        {
            var requestedToken = pageToken;
            var page = await CallAsync(
                "compute.regions.list",
                token =>
                {
                    var request = _service.Regions.List(projectId);
                    request.PageToken = requestedToken;
                    return request.ExecuteAsync(token);
                },
                cancellationToken).ConfigureAwait(false);

            foreach (var region in page.Items ?? [])
            {
                if (!string.IsNullOrEmpty(region.Name))
                {
                    table[region.Name] = ToMetrics(region.Quotas);
                }
            }

            var next = string.IsNullOrEmpty(page.NextPageToken) ? null : page.NextPageToken;
            if (next is not null && next == pageToken)
            {
                // A partial table would read as "this region has no quota", so the whole read fails instead.
                throw new CloudOperationException(
                    new CloudError("LIST_PAGINATION_STUCK", null, "Compute Engine returned the same page token twice, so the region quota list may be incomplete."),
                    CloudErrorKind.Network);
            }

            pageToken = next;
        }
        while (pageToken is not null);

        return table;
    }

    private async Task<QuotaMetric?> GetAllRegionsGpusAsync(string projectId, CancellationToken cancellationToken)
    {
        var project = await CallAsync("compute.projects.get", token => _service.Projects.Get(projectId).ExecuteAsync(token), cancellationToken).ConfigureAwait(false);
        return ToMetrics(project.Quotas).FirstOrDefault(m => m.Metric == QuotaMetrics.GpusAllRegions);
    }

    private static List<QuotaMetric> ToMetrics(IList<ComputeData.Quota>? quotas)
        => (quotas ?? [])
            .Where(q => !string.IsNullOrEmpty(q.Metric))
            .Select(q => new QuotaMetric(q.Metric, q.Limit, q.Usage ?? 0))
            .ToList();

    private Task<T> CallAsync<T>(string operation, Func<CancellationToken, Task<T>> request, CancellationToken cancellationToken)
        => _pipeline.ExecuteAsync(
            operation,
            async token =>
            {
                try
                {
                    return await request(token).ConfigureAwait(false);
                }
                catch (GoogleApiException ex)
                {
                    throw GoogleApiErrors.ToException(GoogleApiErrors.FromApiException(ex));
                }
            },
            cancellationToken,
            singleRequest: true);
}
