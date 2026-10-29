using System.Text.Json;
using DnaEntropyGraph.Core.Cloud;

namespace DnaEntropyGraph.Cloud.Rest;

/// <summary>
/// Issue #933: the one place a Google list loop decides whether there is a next page. Every <c>nextPageToken</c> read in <c>Rest/</c> goes through
/// <see cref="Advance(string?)"/> (a Guards test fails on any other read). It remembers EVERY token the walk has been given, not only the last one,
/// so a cycle (A, B, A) fails as surely as the same token twice; a short or looping list must never read as a complete one (an empty VM list reads as
/// "no VM", an empty quota table as "no quota"). One walk, one guard: build it before the loop, one per listing.
/// <para>
/// DECISION (agent-made, reversible): the failure is <see cref="CloudErrorKind.Network"/> (what compute, quota and storage already used). The
/// reconciler must DEFER on it: kind Other makes its look "Unknown", hands the run to the runner, whose own lookup fails the same way, ends the row
/// Failed and cannot find the VM to end (a live, billing VM orphaned). See docs/cloud_design.md, "Paging".
/// </para>
/// <para>
/// The message names no list on purpose: <c>CloudCallPipeline.IsTransient</c> also reads <c>CloudErrorClassifier.Classify</c> over the text, and
/// "the billing account list" or "the quota list" classify as Billing or Quota (permanent: never replayed). Keep it free of classifier keywords.
/// </para>
/// </summary>
internal sealed class PageTokenGuard
{
    /// <summary>The error code a walk that repeats a page token fails with.</summary>
    public const string StuckCode = "LIST_PAGINATION_STUCK";

    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);

    /// <summary>The token to ask the next page with, or null when this was the last page. Throws on a token already seen in this walk.</summary>
    public string? Advance(string? nextPageToken)
    {
        if (string.IsNullOrEmpty(nextPageToken))
        {
            return null;
        }

        if (!_seen.Add(nextPageToken))
        {
            throw new CloudOperationException(
                new CloudError(StuckCode, null, "Google returned a page token it had already returned, so the list may be incomplete."),
                CloudErrorKind.Network);
        }

        return nextPageToken;
    }

    /// <summary>The same for a page read as raw JSON (the Cloud Quotas client has no typed response): the root's <c>nextPageToken</c>, when it is a string.</summary>
    public string? Advance(JsonElement page)
        => Advance(page.ValueKind == JsonValueKind.Object && page.TryGetProperty("nextPageToken", out var token) && token.ValueKind == JsonValueKind.String ? token.GetString() : null);
}
