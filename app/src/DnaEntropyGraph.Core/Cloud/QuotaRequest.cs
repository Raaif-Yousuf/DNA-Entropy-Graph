using System.Globalization;
using System.Text.RegularExpressions;

namespace DnaEntropyGraph.Core.Cloud;

/// <summary>Why Google says a project cannot ask for more of a quota (the Cloud Quotas API's <c>ineligibilityReason</c>).</summary>
public enum QuotaIneligibilityReason
{
    None,
    NoValidBillingAccount,
    NotSupported,
    NotEnoughUsageHistory,
    Other,
}

/// <summary>
/// Whether the project may ask for more of one quota. <see cref="IsEligible"/> is null when Google did not say (an unknown quota id,
/// or an answer with no eligibility field): unknown is never read as "yes".
/// </summary>
public sealed record QuotaEligibility(string Metric, bool? IsEligible, QuotaIneligibilityReason Reason = QuotaIneligibilityReason.None, string? Detail = null);

/// <summary>
/// One request for more quota. <paramref name="Region"/> is set for a regional metric (<c>NVIDIA_L4_GPUS</c>) and null for the project-wide one
/// (<c>GPUS_ALL_REGIONS</c>). <paramref name="PreferenceId"/> is the id of the quota preference (a resource id: lowercase letters, digits, hyphens),
/// chosen by the caller so a repeat of the same request is an already-exists answer and not a second request.
/// </summary>
public sealed record QuotaIncreaseRequest(string Metric, string? Region, long PreferredValue, string PreferenceId, string Justification, string? ContactEmail = null)
{
    /// <summary>
    /// Throws <see cref="ArgumentException"/> before any request exists: a metric with no known quota id, a regional metric without a region (or the project-wide one
    /// with one), a value below 1, or a preference id that is not a resource id (lowercase letters, digits and hyphens, starting with a letter, at most 63).
    /// </summary>
    public void EnsureValid()
    {
        if (QuotaMetrics.QuotaIdFor(Metric) is null)
        {
            throw new ArgumentException($"'{Metric}' is not a quota this app asks more of.", nameof(Metric));
        }

        if ((Metric == QuotaMetrics.NvidiaL4Gpus) != !string.IsNullOrWhiteSpace(Region))
        {
            throw new ArgumentException("The L4 quota is asked per region and the project-wide GPU cap has no region.", nameof(Region));
        }

        if (PreferredValue < 1)
        {
            throw new ArgumentException("The preferred value must be at least 1.", nameof(PreferredValue));
        }

        if (!Regex.IsMatch(PreferenceId ?? string.Empty, @"\A[a-z][a-z0-9-]{0,62}\z"))
        {
            throw new ArgumentException("The preference id must be lowercase letters, digits and hyphens, starting with a letter.", nameof(PreferenceId));
        }
    }
}

/// <summary>What Google answered to a request: the preference's name, what was asked, what is granted now (null while pending), and whether it is still being reviewed.</summary>
public sealed record QuotaIncreaseResult(string PreferenceName, long PreferredValue, long? GrantedValue, bool Reconciling, string? StateDetail)
{
    /// <summary>True when the granted value already meets the request (Google approved it at once).</summary>
    public bool Granted => GrantedValue is { } granted && granted >= PreferredValue;
}

/// <summary>What became of one quota in <see cref="GpuQuotaRequester"/>.</summary>
public enum QuotaRequestOutcome
{
    /// <summary>The quota is already at least one GPU: nothing was asked.</summary>
    AlreadyAvailable,

    /// <summary>Google approved the request at once.</summary>
    Granted,

    /// <summary>The request is with Google (review can take a while).</summary>
    Requested,

    /// <summary>The same request was already made today.</summary>
    AlreadyRequested,

    /// <summary>The project may not ask: see <see cref="QuotaRequestItem.Ineligibility"/>.</summary>
    NotEligible,

    /// <summary>The eligibility read or the request failed: see <see cref="QuotaRequestItem.ErrorKind"/> and <see cref="QuotaRequestItem.Detail"/>.</summary>
    Failed,
}

/// <summary>One quota's outcome. <see cref="Detail"/> is a machine-readable code for the caller to turn into words.</summary>
public sealed record QuotaRequestItem(
    string Metric,
    string? Region,
    QuotaRequestOutcome Outcome,
    QuotaIneligibilityReason Ineligibility = QuotaIneligibilityReason.None,
    string? Detail = null,
    CloudErrorKind? ErrorKind = null,
    QuotaIncreaseResult? Result = null);

/// <summary>The outcomes of one <see cref="GpuQuotaRequester.RequestAsync"/>, regional L4 first, then the project-wide cap.</summary>
public sealed record GpuQuotaRequestReport(IReadOnlyList<QuotaRequestItem> Items)
{
    /// <summary>True when every quota is available now (already there, or granted at once).</summary>
    public bool Sufficient => Items.All(i => i.Outcome is QuotaRequestOutcome.AlreadyAvailable or QuotaRequestOutcome.Granted);

    /// <summary>True when at least one request is with Google (made now, or earlier today).</summary>
    public bool AnyRequested => Items.Any(i => i.Outcome is QuotaRequestOutcome.Requested or QuotaRequestOutcome.AlreadyRequested or QuotaRequestOutcome.Granted);
}

/// <summary>
/// Issue #99, the setup wizard's GPU step: asks Google for one L4 GPU in the chosen region and one GPU project-wide. Per quota, in this order: skip it when
/// the quota read already shows a GPU (a failed read is unknown and does not count as present), read whether the project may ask (an unknown answer is a
/// failure, never permission), and only then create the request. A failure on one quota never stops the other. A request made twice in a day reuses the same
/// preference id, so the second is <see cref="QuotaRequestOutcome.AlreadyRequested"/> and not an error. Quota preferences are not billable and carry no labels.
/// </summary>
public sealed partial class GpuQuotaRequester
{
    /// <summary>The justification sent to Google with every request: a reviewer reads it.</summary>
    public const string Justification = "Running the Evo 2 genomic language model (DNA Entropy Graph, a lab research tool) on one GPU per job.";

    private const long WantedGpus = 1;

    private readonly IQuotaGateway _quota;
    private readonly TimeProvider _time;

    public GpuQuotaRequester(IQuotaGateway quota, TimeProvider? time = null)
    {
        _quota = quota;
        _time = time ?? TimeProvider.System;
    }

    [GeneratedRegex(@"\A[a-z]+-[a-z]+[0-9]+\z")]
    private static partial Regex RegionName();

    public async Task<GpuQuotaRequestReport> RequestAsync(string projectId, string region, string? contactEmail, CancellationToken cancellationToken)
    {
        if (region is null || !RegionName().IsMatch(region))
        {
            throw new ArgumentException("The region is not a Google Cloud region name such as us-central1.", nameof(region));
        }

        var read = await _quota.ReadQuotaAsync(projectId, cancellationToken).ConfigureAwait(false);
        var day = _time.GetUtcNow().ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var items = new List<QuotaRequestItem>
        {
            await RequestOneAsync(
                projectId,
                new QuotaIncreaseRequest(QuotaMetrics.NvidiaL4Gpus, region, WantedGpus, $"deg-{Slug(QuotaMetrics.NvidiaL4Gpus)}-{region}-{day}", Justification, contactEmail),
                RegionalRemaining(read, region),
                cancellationToken).ConfigureAwait(false),
            await RequestOneAsync(
                projectId,
                new QuotaIncreaseRequest(QuotaMetrics.GpusAllRegions, null, WantedGpus, $"deg-{Slug(QuotaMetrics.GpusAllRegions)}-{day}", Justification, contactEmail),
                read.AllRegionsGpus?.Remaining,
                cancellationToken).ConfigureAwait(false),
        };
        return new GpuQuotaRequestReport(items);
    }

    private static string Slug(string metric) => metric.ToLowerInvariant().Replace('_', '-');

    /// <summary>The L4 GPUs this region still offers: null when the read does not say, 0 for a region present without the metric.</summary>
    private static double? RegionalRemaining(QuotaReadResult read, string region)
    {
        var match = read.Regions?.FirstOrDefault(r => string.Equals(r.Key, region, StringComparison.OrdinalIgnoreCase));
        if (match?.Value is null)
        {
            return null;
        }

        var line = match.Value.Value.FirstOrDefault(m => string.Equals(m.Metric, QuotaMetrics.NvidiaL4Gpus, StringComparison.Ordinal));
        return line is null ? 0 : line.Remaining;
    }

    private async Task<QuotaRequestItem> RequestOneAsync(string projectId, QuotaIncreaseRequest request, double? remaining, CancellationToken cancellationToken)
    {
        if (remaining is >= 1)
        {
            return new QuotaRequestItem(request.Metric, request.Region, QuotaRequestOutcome.AlreadyAvailable);
        }

        QuotaEligibility eligibility;
        try
        {
            eligibility = await _quota.GetQuotaIncreaseEligibilityAsync(projectId, request.Metric, cancellationToken).ConfigureAwait(false);
        }
        catch (CloudOperationException ex)
        {
            return new QuotaRequestItem(request.Metric, request.Region, QuotaRequestOutcome.Failed, Detail: ex.Error.Code, ErrorKind: ex.Kind);
        }

        if (eligibility.IsEligible is null)
        {
            return new QuotaRequestItem(request.Metric, request.Region, QuotaRequestOutcome.Failed, Detail: "QUOTA_ELIGIBILITY_UNKNOWN", ErrorKind: CloudErrorKind.Other);
        }

        if (eligibility.IsEligible is false)
        {
            return new QuotaRequestItem(request.Metric, request.Region, QuotaRequestOutcome.NotEligible, eligibility.Reason, eligibility.Detail);
        }

        try
        {
            var result = await _quota.RequestQuotaIncreaseAsync(projectId, request, cancellationToken).ConfigureAwait(false);
            return new QuotaRequestItem(request.Metric, request.Region, result.Granted ? QuotaRequestOutcome.Granted : QuotaRequestOutcome.Requested, Result: result);
        }
        catch (CloudOperationException ex) when (ex.Kind == CloudErrorKind.AlreadyExists)
        {
            return new QuotaRequestItem(request.Metric, request.Region, QuotaRequestOutcome.AlreadyRequested);
        }
        catch (CloudOperationException ex)
        {
            return new QuotaRequestItem(request.Metric, request.Region, QuotaRequestOutcome.Failed, Detail: ex.Error.Code, ErrorKind: ex.Kind);
        }
    }
}
