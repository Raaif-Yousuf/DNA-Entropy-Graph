using System.Text.Json;
using DnaEntropyGraph.Core.Cloud;

namespace DnaEntropyGraph.Cloud.Rest;

/// <summary>
/// Issue #933: the one place a Google list loop decides whether there is a next page. Every <c>nextPageToken</c> read in <c>Rest/</c> goes through
/// <see cref="Advance(string?)"/> (a Guards test fails on any other read). It remembers EVERY token the walk has been given, not only the last one,
/// so a cycle (A, B, A) fails as surely as the same token twice; a short or looping list must never read as a complete one (an empty VM list reads as
/// "no VM", an empty quota table as "no quota"). One walk, one guard: make a new one per listing.
/// <para>
/// DECISION (agent-made, reversible): the failure is <see cref="CloudErrorKind.Other"/>, not Network. A server that repeats a token will repeat it
/// again, so it is not "no answer": the pipeline must not retry it and <c>JobReconciler</c> must not park it as Deferred for the reconnect probe to
/// retry for ever. See docs/cloud_design.md, "Paging".
/// </para>
/// </summary>
internal sealed class PageTokenGuard
{
    /// <summary>The error code a walk that repeats a page token fails with.</summary>
    public const string StuckCode = "LIST_PAGINATION_STUCK";

    private readonly string _listing;
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);

    /// <param name="listing">What is being listed, in plain words ("the VM list"): the error says that list may be incomplete.</param>
    public PageTokenGuard(string listing)
    {
        _listing = listing;
    }

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
                new CloudError(StuckCode, null, $"Google returned a page token it had already returned, so {_listing} may be incomplete."),
                CloudErrorKind.Other);
        }

        return nextPageToken;
    }

    /// <summary>The same for a page read as raw JSON (the Cloud Quotas client has no typed response): the root's <c>nextPageToken</c>, when it is a string.</summary>
    public string? Advance(JsonElement page)
        => Advance(page.ValueKind == JsonValueKind.Object && page.TryGetProperty("nextPageToken", out var token) && token.ValueKind == JsonValueKind.String ? token.GetString() : null);
}
