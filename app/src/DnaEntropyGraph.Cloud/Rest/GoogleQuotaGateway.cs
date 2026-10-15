using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
    private const string QuotasHost = "https://cloudquotas.googleapis.com";
    private const string ComputeServiceName = "compute.googleapis.com";

    private readonly ComputeService _service;
    private readonly CloudCallPipeline _pipeline;
    private readonly HttpClient _quotas;

    public GoogleQuotaGateway(ComputeService service, CloudCallPipeline pipeline, HttpClient quotas)
    {
        _service = service;
        _pipeline = pipeline;
        _quotas = quotas;
    }

    /// <summary>
    /// Issue #99: <c>GET quotaInfos/{quotaId}</c> of the Cloud Quotas API and its <c>quotaIncreaseEligibility</c>. No Google package for this API exists on NuGet
    /// (only the gRPC Google.Cloud.CloudQuotas.V1, which would break the injectable HTTP stack every other gateway has), so this is the documented REST call over the
    /// same bearer-token HTTP stack. A 404 (a quota id Google does not know) and a missing eligibility field are both an unknown answer, never "eligible".
    /// THEORY (unverified, no live project): the two quota ids in <see cref="QuotaMetrics.QuotaIdFor"/>, and the field names. docs/ToTest.md carries the row.
    /// </summary>
    public async Task<QuotaEligibility> GetQuotaIncreaseEligibilityAsync(string projectId, string metric, CancellationToken cancellationToken)
    {
        var quotaId = QuotaMetrics.QuotaIdFor(metric) ?? throw new ArgumentException($"'{metric}' is not a quota this app asks more of.", nameof(metric));
        var url = $"{QuotasHost}/v1/projects/{Uri.EscapeDataString(projectId)}/locations/global/services/{ComputeServiceName}/quotaInfos/{quotaId}";
        string body;
        try
        {
            body = await SendAsync("cloudquotas.quotaInfos.get", () => new HttpRequestMessage(HttpMethod.Get, url), cancellationToken).ConfigureAwait(false);
        }
        catch (CloudOperationException ex) when (ex.Error.HttpStatus == 404)
        {
            return new QuotaEligibility(metric, null, QuotaIneligibilityReason.None, "QUOTA_INFO_NOT_FOUND");
        }

        using var document = ParseObject(body);
        if (!document.RootElement.TryGetProperty("quotaIncreaseEligibility", out var eligibility)
            || eligibility.ValueKind != JsonValueKind.Object
            || !eligibility.TryGetProperty("isEligible", out var isEligible)
            || isEligible.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return new QuotaEligibility(metric, null, QuotaIneligibilityReason.None, "QUOTA_ELIGIBILITY_MISSING");
        }

        if (isEligible.GetBoolean())
        {
            return new QuotaEligibility(metric, true);
        }

        var reason = eligibility.TryGetProperty("ineligibilityReason", out var wire) && wire.ValueKind == JsonValueKind.String ? wire.GetString() : null;
        return new QuotaEligibility(metric, false, reason switch
        {
            "NO_VALID_BILLING_ACCOUNT" => QuotaIneligibilityReason.NoValidBillingAccount,
            "NOT_SUPPORTED" => QuotaIneligibilityReason.NotSupported,
            "NOT_ENOUGH_USAGE_HISTORY" => QuotaIneligibilityReason.NotEnoughUsageHistory,
            _ => QuotaIneligibilityReason.Other,
        });
    }

    /// <summary>
    /// Issue #99: <c>POST quotaPreferences?quotaPreferenceId=...</c>. One request per call; the caller-chosen preference id makes a replay after a lost answer an
    /// already-exists answer, never a second request. The body names the service, the quota id, the preferred value (a string: it is an int64 in JSON), the
    /// region dimension for a regional quota, the justification and the optional contact email.
    /// </summary>
    public async Task<QuotaIncreaseResult> RequestQuotaIncreaseAsync(string projectId, QuotaIncreaseRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.EnsureValid();
        var preference = new JsonObject
        {
            ["service"] = ComputeServiceName,
            ["quotaId"] = QuotaMetrics.QuotaIdFor(request.Metric),
            ["quotaConfig"] = new JsonObject { ["preferredValue"] = request.PreferredValue.ToString(CultureInfo.InvariantCulture) },
            ["justification"] = request.Justification,
        };
        if (!string.IsNullOrWhiteSpace(request.Region))
        {
            preference["dimensions"] = new JsonObject { ["region"] = request.Region };
        }

        if (!string.IsNullOrWhiteSpace(request.ContactEmail))
        {
            preference["contactEmail"] = request.ContactEmail;
        }

        var url = $"{QuotasHost}/v1/projects/{Uri.EscapeDataString(projectId)}/locations/global/quotaPreferences?quotaPreferenceId={Uri.EscapeDataString(request.PreferenceId)}";
        var payload = preference.ToJsonString();
        var body = await SendAsync(
            "cloudquotas.quotaPreferences.create",
            () => new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(payload, Encoding.UTF8, "application/json") },
            cancellationToken).ConfigureAwait(false);

        using var document = ParseObject(body);
        var root = document.RootElement;
        var config = root.TryGetProperty("quotaConfig", out var c) && c.ValueKind == JsonValueKind.Object ? c : default;
        return new QuotaIncreaseResult(
            root.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String ? name.GetString()! : request.PreferenceId,
            Int64Of(config, "preferredValue") ?? request.PreferredValue,
            Int64Of(config, "grantedValue"),
            root.TryGetProperty("reconciling", out var reconciling) && reconciling.ValueKind == JsonValueKind.True,
            config.ValueKind == JsonValueKind.Object && config.TryGetProperty("stateDetail", out var detail) && detail.ValueKind == JsonValueKind.String ? detail.GetString() : null);
    }

    /// <summary>An int64 field as Google sends it (a JSON string) or a plain number; null when absent or not a number.</summary>
    private static long? Int64Of(JsonElement parent, string name)
    {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String when long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            JsonValueKind.Number when value.TryGetInt64(out var number) => number,
            _ => null,
        };
    }

    private static JsonDocument ParseObject(string body)
    {
        try
        {
            var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                return document;
            }

            document.Dispose();
        }
        catch (JsonException)
        {
            // Falls through to the same named error as a non-object answer.
        }

        throw new CloudOperationException(new CloudError("QUOTA_RESPONSE_UNREADABLE", null, "The Cloud Quotas API answered with something this app cannot read."), CloudErrorKind.Other);
    }

    /// <summary>One HTTP request through the pipeline (retry, token refresh, breaker); a non-success answer becomes the app's exception through the same error mapping as the other gateways.</summary>
    private Task<string> SendAsync(string operation, Func<HttpRequestMessage> build, CancellationToken cancellationToken)
        => _pipeline.ExecuteAsync(
            operation,
            async token =>
            {
                using var message = build();
                using var response = await _quotas.SendAsync(message, token).ConfigureAwait(false);
                var text = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    throw GoogleApiErrors.ToException(GoogleApiErrors.FromHttpResponse((int)response.StatusCode, text));
                }

                return text;
            },
            cancellationToken,
            singleRequest: true);

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
