using DnaEntropyGraph.Core.Cloud;

namespace DnaEntropyGraph.Cloud;

// Issue #99: the Cloud Quotas side of IQuotaGateway on the fake: the eligibility read and the quota preference create, with scripted
// eligibility, scripted failures, and an auto-grant that really moves the quota table the preflight reads.
public sealed partial class FakeGcp
{
    private readonly object _quotaRequestGate = new();
    private readonly List<string> _quotaCalls = [];
    private readonly List<QuotaIncreaseRequest> _quotaRequests = [];
    private readonly Dictionary<string, (bool? Eligible, QuotaIneligibilityReason Reason)> _quotaEligibility = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CloudErrorKind> _quotaRequestFailures = new(StringComparer.Ordinal);
    private readonly HashSet<string> _quotaPreferenceIds = new(StringComparer.Ordinal);
    private CloudErrorKind? _quotaEligibilityFailure;
    private bool _quotaAutoGrant;

    /// <summary>Every eligibility read and request, in order, as <c>eligibility:&lt;metric&gt;</c> / <c>request:&lt;metric&gt;</c>: the observable for "eligibility was read first".</summary>
    public IReadOnlyList<string> QuotaCalls
    {
        get
        {
            lock (_quotaRequestGate)
            {
                return _quotaCalls.ToList();
            }
        }
    }

    /// <summary>Every quota increase the fake was asked to create (a repeat of an id included).</summary>
    public IReadOnlyList<QuotaIncreaseRequest> QuotaRequests
    {
        get
        {
            lock (_quotaRequestGate)
            {
                return _quotaRequests.ToList();
            }
        }
    }

    /// <summary>What <see cref="GetQuotaIncreaseEligibilityAsync"/> answers for <paramref name="metric"/>: eligible, ineligible with a reason, or (null) not said. Unscripted metrics are eligible.</summary>
    public FakeGcp WithQuotaEligibility(string metric, bool? eligible, QuotaIneligibilityReason reason = QuotaIneligibilityReason.None)
    {
        _quotaEligibility[metric] = (eligible, reason);
        return this;
    }

    /// <summary>Every eligibility read fails with <paramref name="kind"/> (the Cloud Quotas API off, a missing permission).</summary>
    public FakeGcp WithQuotaEligibilityFailure(CloudErrorKind kind)
    {
        _quotaEligibilityFailure = kind;
        return this;
    }

    /// <summary>The create for <paramref name="metric"/> fails with <paramref name="kind"/>.</summary>
    public FakeGcp WithQuotaRequestFailure(string metric, CloudErrorKind kind)
    {
        _quotaRequestFailures[metric] = kind;
        return this;
    }

    /// <summary>
    /// Every request is approved at once and the quota table the preflight reads moves with it (a regional L4 line, or the GPUS_ALL_REGIONS cap), so a test
    /// can see a grant change what <see cref="GetGpuQuotaAsync"/> answers. Without it a request stays pending and the table does not change.
    /// </summary>
    public FakeGcp WithQuotaAutoGrant()
    {
        _quotaAutoGrant = true;
        return this;
    }

    public Task<QuotaEligibility> GetQuotaIncreaseEligibilityAsync(string projectId, string metric, CancellationToken cancellationToken)
    {
        ThrowIfScriptedTransient();
        if (QuotaMetrics.QuotaIdFor(metric) is null)
        {
            throw new ArgumentException($"'{metric}' is not a quota this app asks more of.", nameof(metric));
        }

        lock (_quotaRequestGate)
        {
            _quotaCalls.Add("eligibility:" + metric);
        }

        if (_quotaEligibilityFailure is { } kind)
        {
            throw Build(kind, "QUOTA_ELIGIBILITY_FAILED", kind == CloudErrorKind.Permission ? 403 : null, "The quota eligibility could not be read.");
        }

        var (eligible, reason) = _quotaEligibility.TryGetValue(metric, out var scripted) ? scripted : (true, QuotaIneligibilityReason.None);
        return Task.FromResult(new QuotaEligibility(metric, eligible, reason));
    }

    public Task<QuotaIncreaseResult> RequestQuotaIncreaseAsync(string projectId, QuotaIncreaseRequest request, CancellationToken cancellationToken)
    {
        ThrowIfScriptedTransient();
        ArgumentNullException.ThrowIfNull(request);
        request.EnsureValid();
        lock (_quotaRequestGate)
        {
            _quotaCalls.Add("request:" + request.Metric);
            _quotaRequests.Add(request);
        }

        if (_quotaRequestFailures.TryGetValue(request.Metric, out var kind))
        {
            throw Build(kind, "QUOTA_REQUEST_FAILED", kind == CloudErrorKind.Permission ? 403 : null, "The quota request failed.");
        }

        lock (_quotaRequestGate)
        {
            if (!_quotaPreferenceIds.Add(request.PreferenceId))
            {
                throw Build(CloudErrorKind.AlreadyExists, "ALREADY_EXISTS", 409, "Requested entity already exists");
            }
        }

        var name = $"projects/{projectId}/locations/global/quotaPreferences/{request.PreferenceId}";
        if (!_quotaAutoGrant)
        {
            return Task.FromResult(new QuotaIncreaseResult(name, request.PreferredValue, null, true, "Pending review"));
        }

        if (request.Metric == QuotaMetrics.NvidiaL4Gpus)
        {
            var key = (request.Region!.ToLowerInvariant(), "nvidia-l4");
            _regionalQuota[key] = Math.Max(_regionalQuota.GetValueOrDefault(key), (int)request.PreferredValue);
        }
        else
        {
            _allRegionsGpuCap = Math.Max(_allRegionsGpuCap ?? 0, (int)request.PreferredValue);
        }

        return Task.FromResult(new QuotaIncreaseResult(name, request.PreferredValue, request.PreferredValue, false, "Granted"));
    }
}
