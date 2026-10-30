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

    /// <summary>The header that names the quota project of a request (see <see cref="SendAsync"/>).</summary>
    internal const string UserProjectHeader = "X-Goog-User-Project";

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
    public async Task<QuotaEligibility> GetQuotaIncreaseEligibilityAsync(CloudScope scope, string metric, CancellationToken cancellationToken)
    {
        using var _account = CallAccount.Use(scope);
        var projectId = scope.ProjectId;
        var quotaId = QuotaMetrics.QuotaIdFor(metric) ?? throw new ArgumentException($"'{metric}' is not a quota this app asks more of.", nameof(metric));
        var url = $"{QuotasHost}/v1/projects/{Uri.EscapeDataString(projectId)}/locations/global/services/{ComputeServiceName}/quotaInfos/{quotaId}";
        string body;
        try
        {
            body = await SendAsync("cloudquotas.quotaInfos.get", projectId, () => new HttpRequestMessage(HttpMethod.Get, url), cancellationToken).ConfigureAwait(false);
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
    /// Issue #99: asks for the quota, idempotently (see <see cref="IQuotaGateway.RequestQuotaIncreaseAsync"/>). MEASURED 2026-10-15 (docs.cloud.google.com/docs/quotas/reference/rest/v1/projects.locations.quotaPreferences):
    /// "There is only one QuotaPreference resource for a quota value targeting a unique set of dimensions", an <c>etag</c> that no longer matches is rejected ABORTED, the
    /// list <c>filter</c> covers state, type and time only (not service or quota id), and a preference cannot be deleted. So this lists the project's preferences (paged),
    /// matches one on service, quota id and dimensions whatever its id, and: leaves it when it already asks for enough; raises it with <c>PATCH</c> and its etag when it asks
    /// for less (one attempt: a replayed patch meets a changed etag); creates one under the caller's id when there is none (a replay of a create whose answer was lost meets
    /// that id and is an already-exists answer, never a second request). The body names the service, the quota id, the preferred value (a string: an int64 in JSON), the
    /// dimensions, the justification and the contact email.
    /// </summary>
    public async Task<QuotaIncreaseResult> RequestQuotaIncreaseAsync(CloudScope scope, QuotaIncreaseRequest request, CancellationToken cancellationToken)
    {
        using var _account = CallAccount.Use(scope);
        var projectId = scope.ProjectId;
        ArgumentNullException.ThrowIfNull(request);
        request.EnsureValid();
        var quotaId = QuotaMetrics.QuotaIdFor(request.Metric)!;
        var dimensions = QuotaMetrics.DimensionsFor(request.Metric, request.Region);
        var parent = $"{QuotasHost}/v1/projects/{Uri.EscapeDataString(projectId)}/locations/global/quotaPreferences";

        var existing = await FindPreferenceAsync(projectId, parent, quotaId, dimensions, cancellationToken).ConfigureAwait(false);
        if (existing is { } found && found.Preferred >= request.PreferredValue)
        {
            return new QuotaIncreaseResult(found.Name, found.Preferred, found.Granted, found.Reconciling, found.StateDetail, AlreadyExisted: true);
        }

        var preference = new JsonObject
        {
            ["service"] = ComputeServiceName,
            ["quotaId"] = quotaId,
            ["quotaConfig"] = new JsonObject { ["preferredValue"] = request.PreferredValue.ToString(CultureInfo.InvariantCulture) },
            ["justification"] = request.Justification,
        };
        if (dimensions.Count > 0)
        {
            var dims = new JsonObject();
            foreach (var (key, value) in dimensions)
            {
                dims[key] = value;
            }

            preference["dimensions"] = dims;
        }

        if (!string.IsNullOrWhiteSpace(request.ContactEmail))
        {
            preference["contactEmail"] = request.ContactEmail;
        }

        string body;
        if (existing is { } lower)
        {
            preference["name"] = lower.Name;
            if (!string.IsNullOrEmpty(lower.Etag))
            {
                preference["etag"] = lower.Etag;
            }

            var patchUrl = $"{QuotasHost}/v1/{lower.Name}";
            var patchPayload = preference.ToJsonString();
            body = await SendAsync(
                "cloudquotas.quotaPreferences.patch",
                projectId,
                () => new HttpRequestMessage(HttpMethod.Patch, patchUrl) { Content = new StringContent(patchPayload, Encoding.UTF8, "application/json") },
                cancellationToken,
                replayable: false).ConfigureAwait(false);
        }
        else
        {
            var url = $"{parent}?quotaPreferenceId={Uri.EscapeDataString(request.PreferenceId)}";
            var payload = preference.ToJsonString();
            body = await SendAsync(
                "cloudquotas.quotaPreferences.create",
                projectId,
                () => new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(payload, Encoding.UTF8, "application/json") },
                cancellationToken).ConfigureAwait(false);
        }

        using var document = ParseObject(body);
        return ToResult(document.RootElement, request.PreferenceId, request.PreferredValue);
    }

    private readonly record struct ExistingPreference(string Name, long Preferred, long? Granted, bool Reconciling, string? StateDetail, string? Etag);

    private static QuotaIncreaseResult ToResult(JsonElement root, string fallbackName, long fallbackPreferred)
    {
        var config = root.TryGetProperty("quotaConfig", out var c) && c.ValueKind == JsonValueKind.Object ? c : default;
        return new QuotaIncreaseResult(
            root.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String ? name.GetString()! : fallbackName,
            Int64Of(config, "preferredValue") ?? fallbackPreferred,
            Int64Of(config, "grantedValue"),
            root.TryGetProperty("reconciling", out var reconciling) && reconciling.ValueKind == JsonValueKind.True,
            config.ValueKind == JsonValueKind.Object && config.TryGetProperty("stateDetail", out var detail) && detail.ValueKind == JsonValueKind.String ? detail.GetString() : null);
    }

    /// <summary>The project's preference for this quota and these dimensions, under whatever id; null when there is none. Pages with a stuck-token guard (a short list would read as "none" and create a conflicting one).</summary>
    private async Task<ExistingPreference?> FindPreferenceAsync(string projectId, string parent, string quotaId, IReadOnlyDictionary<string, string> dimensions, CancellationToken cancellationToken)
    {
        var paging = new PageTokenGuard();
        string? pageToken = null;
        do
        {
            var url = pageToken is null ? parent : $"{parent}?pageToken={Uri.EscapeDataString(pageToken)}";
            var body = await SendAsync("cloudquotas.quotaPreferences.list", projectId, () => new HttpRequestMessage(HttpMethod.Get, url), cancellationToken).ConfigureAwait(false);
            using var document = ParseObject(body);
            if (document.RootElement.TryGetProperty("quotaPreferences", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in list.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Object
                        && item.TryGetProperty("service", out var service) && service.GetString() == ComputeServiceName
                        && item.TryGetProperty("quotaId", out var id) && id.GetString() == quotaId
                        && SameDimensions(item, dimensions)
                        && item.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
                    {
                        var parsed = ToResult(item, name.GetString()!, 0);
                        return new ExistingPreference(
                            parsed.PreferenceName,
                            parsed.PreferredValue,
                            parsed.GrantedValue,
                            parsed.Reconciling,
                            parsed.StateDetail,
                            item.TryGetProperty("etag", out var etag) && etag.ValueKind == JsonValueKind.String ? etag.GetString() : null);
                    }
                }
            }

            pageToken = paging.Advance(document.RootElement);
        }
        while (pageToken is not null);

        return null;
    }

    private static bool SameDimensions(JsonElement preference, IReadOnlyDictionary<string, string> wanted)
    {
        var actual = new Dictionary<string, string>(StringComparer.Ordinal);
        if (preference.TryGetProperty("dimensions", out var dims) && dims.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in dims.EnumerateObject())
            {
                actual[property.Name] = property.Value.GetString() ?? string.Empty;
            }
        }

        return actual.Count == wanted.Count && wanted.All(p => actual.TryGetValue(p.Key, out var value) && string.Equals(value, p.Value, StringComparison.Ordinal));
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

    /// <summary>
    /// One HTTP request through the pipeline (retry, token refresh, breaker); a non-success answer becomes the app's exception through the same error mapping as the other
    /// gateways. Every call names the selected project as its quota project (<c>X-Goog-User-Project</c>): MEASURED 2026-10-15 (docs.cloud.google.com/docs/quotas/set-quota-project)
    /// "a request to a client-based API can fail when the quota project cannot be identified", a user credential's fallback to a shared project is not guaranteed for every API,
    /// and for REST "the x-goog-user-project header" sets it. THEORY (unverified, ToTest #99): the docs do not say whether the Cloud Quotas API is client-based; sending it is
    /// harmless either way (it needs the caller to hold <c>serviceusage.services.use</c> on the project they chose, which a project owner does).
    /// </summary>
    private Task<string> SendAsync(string operation, string projectId, Func<HttpRequestMessage> build, CancellationToken cancellationToken, bool replayable = true)
        => _pipeline.ExecuteAsync(
            operation,
            async token =>
            {
                using var message = build();
                message.Headers.TryAddWithoutValidation(UserProjectHeader, projectId);
                using var response = await _quotas.SendAsync(message, token).ConfigureAwait(false);
                var text = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    throw GoogleApiErrors.ToException(GoogleApiErrors.FromHttpResponse((int)response.StatusCode, text));
                }

                return text;
            },
            cancellationToken,
            replayable: replayable,
            singleRequest: true);

    public async Task<int> GetGpuQuotaAsync(CloudScope scope, string region, string acceleratorType, CancellationToken cancellationToken)
    {
        using var _account = CallAccount.Use(scope);
        var projectId = scope.ProjectId;
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

    public async Task<QuotaReadResult> ReadQuotaAsync(CloudScope scope, CancellationToken cancellationToken)
    {
        using var _account = CallAccount.Use(scope);
        var projectId = scope.ProjectId;
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
        var paging = new PageTokenGuard();
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

            // A partial table would read as "this region has no quota", so a repeated token fails the whole read instead.
            pageToken = paging.Advance(page.NextPageToken);
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
