using DnaEntropyGraph.Core.Abstractions;
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

/// <summary>What Google answered to a request: the preference's name, what was asked, what is granted now (null while pending), and whether it is still being reviewed. <paramref name="AlreadyExisted"/> is true when a preference for the same quota and dimensions was already there (under any id) and already asked for at least this much: nothing was created.</summary>
public sealed record QuotaIncreaseResult(string PreferenceName, long PreferredValue, long? GrantedValue, bool Reconciling, string? StateDetail, bool AlreadyExisted = false)
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
/// Issue #99, the setup wizard's GPU step: asks Google for one more L4 GPU in the chosen region and one more GPU project-wide, only when needed. Per quota, in
/// this order: skip it when the quota read already shows a GPU still free (limit minus usage, at least one); stop when the quota read failed (its limit is unknown,
/// and a request built on an unknown limit could ask for LESS than the project has: the user retries); read whether the project may ask (an unknown answer is a
/// failure, never permission); and only then ask for <c>max(1, limit + 1)</c>, one more than the project has now. The gateway looks for an existing preference for the same
/// quota and dimensions (under any id) and raises it instead of creating a second one, so the same ask on another day is <see cref="QuotaRequestOutcome.AlreadyRequested"/>,
/// not a conflict. A failure on one quota never stops the other. Quota preferences are not billable and carry no labels. No Spot (preemptible) request is made: MEASURED
/// 2026-10-15 (docs.cloud.google.com/compute/resource-usage) Spot VMs "consume standard quota" until a preemptible quota is requested, and "after a request, the applicable
/// resources can only consume preemptible quota", so asking for the standard L4 is what a Spot run needs and asking for the preemptible one would move the project off it.
/// </summary>
public sealed class GpuQuotaRequester
{
    /// <summary>The justification sent to Google with every request: a reviewer reads it.</summary>
    public const string Justification = "Running the Evo 2 genomic language model (DNA Entropy Graph, a lab research tool) on one GPU per job.";

    /// <summary>The <see cref="QuotaRequestItem.Detail"/> of a quota left alone because its current limit could not be read.</summary>
    public const string QuotaReadFailedCode = "QUOTA_READ_FAILED";

    private readonly IQuotaGateway _quota;
    private readonly IGcpAccount _account;

    public GpuQuotaRequester(IQuotaGateway quota, IGcpAccount account)
    {
        _quota = quota;
        _account = account;
    }

    private static readonly Regex RegionName = new(@"\A[a-z]+-[a-z]+[0-9]+\z", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public async Task<GpuQuotaRequestReport> RequestAsync(string projectId, string region, string? contactEmail, CancellationToken cancellationToken)
    {
        if (region is null || !RegionName.IsMatch(region))
        {
            throw new ArgumentException("The region is not a Google Cloud region name such as us-central1.", nameof(region));
        }

        // Issue #830: the wizard's selection path: the project it was asked about, as the account of the selection (ONE SelectedScope read for the whole request, never a separate read of the current account).
        var scope = new CloudScope(projectId, _account.SelectedScope()?.AccountSub);
        var read = await _quota.ReadQuotaAsync(scope, cancellationToken).ConfigureAwait(false);
        var readFailure = read.Failures.FirstOrDefault();
        var items = new List<QuotaRequestItem>
        {
            await RequestOneAsync(
                scope,
                QuotaMetrics.NvidiaL4Gpus,
                region,
                $"deg-{Slug(QuotaMetrics.NvidiaL4Gpus)}-{region}",
                RegionalLine(read, region),
                readFailure,
                contactEmail,
                cancellationToken).ConfigureAwait(false),
            await RequestOneAsync(
                scope,
                QuotaMetrics.GpusAllRegions,
                null,
                $"deg-{Slug(QuotaMetrics.GpusAllRegions)}",
                read.AllRegionsGpus,
                readFailure,
                contactEmail,
                cancellationToken).ConfigureAwait(false),
        };
        return new GpuQuotaRequestReport(items);
    }

    private static string Slug(string metric) => metric.ToLowerInvariant().Replace('_', '-');

    /// <summary>The L4 line of the region: null when the read does not say, a zero line for a region present without the metric.</summary>
    private static QuotaMetric? RegionalLine(QuotaReadResult read, string region)
    {
        var match = read.Regions?.FirstOrDefault(r => string.Equals(r.Key, region, StringComparison.OrdinalIgnoreCase));
        if (match?.Value is null)
        {
            return null;
        }

        return match.Value.Value.FirstOrDefault(m => string.Equals(m.Metric, QuotaMetrics.NvidiaL4Gpus, StringComparison.Ordinal))
            ?? new QuotaMetric(QuotaMetrics.NvidiaL4Gpus, 0, 0);
    }

    private async Task<QuotaRequestItem> RequestOneAsync(
        CloudScope scope,
        string metric,
        string? region,
        string preferenceId,
        QuotaMetric? line,
        QuotaReadFailure? readFailure,
        string? contactEmail,
        CancellationToken cancellationToken)
    {
        if (line?.Remaining is >= 1)
        {
            return new QuotaRequestItem(metric, region, QuotaRequestOutcome.AlreadyAvailable);
        }

        if (line?.Limit is not { } limit)
        {
            // The limit is unknown: asking for "1" could be asking for less than the project has. Nothing is created; the user retries.
            return new QuotaRequestItem(metric, region, QuotaRequestOutcome.Failed, Detail: QuotaReadFailedCode, ErrorKind: readFailure?.Kind ?? CloudErrorKind.Other);
        }

        QuotaEligibility eligibility;
        try
        {
            eligibility = await _quota.GetQuotaIncreaseEligibilityAsync(scope, metric, cancellationToken).ConfigureAwait(false);
        }
        catch (CloudOperationException ex)
        {
            return new QuotaRequestItem(metric, region, QuotaRequestOutcome.Failed, Detail: ex.Error.Code, ErrorKind: ex.Kind);
        }

        if (eligibility.IsEligible is null)
        {
            return new QuotaRequestItem(metric, region, QuotaRequestOutcome.Failed, Detail: "QUOTA_ELIGIBILITY_UNKNOWN", ErrorKind: CloudErrorKind.Other);
        }

        if (eligibility.IsEligible is false)
        {
            return new QuotaRequestItem(metric, region, QuotaRequestOutcome.NotEligible, eligibility.Reason, eligibility.Detail);
        }

        // One more than the project has now (at least one): the smallest ask that frees a GPU.
        var request = new QuotaIncreaseRequest(metric, region, Math.Max(1, (long)Math.Floor(limit) + 1), preferenceId, Justification, contactEmail);
        try
        {
            var result = await _quota.RequestQuotaIncreaseAsync(scope, request, cancellationToken).ConfigureAwait(false);
            var outcome = result.Granted ? QuotaRequestOutcome.Granted : result.AlreadyExisted ? QuotaRequestOutcome.AlreadyRequested : QuotaRequestOutcome.Requested;
            return new QuotaRequestItem(metric, region, outcome, Result: result);
        }
        catch (CloudOperationException ex) when (ex.Kind == CloudErrorKind.AlreadyExists)
        {
            return new QuotaRequestItem(metric, region, QuotaRequestOutcome.AlreadyRequested);
        }
        catch (CloudOperationException ex)
        {
            return new QuotaRequestItem(metric, region, QuotaRequestOutcome.Failed, Detail: ex.Error.Code, ErrorKind: ex.Kind);
        }
    }
}
