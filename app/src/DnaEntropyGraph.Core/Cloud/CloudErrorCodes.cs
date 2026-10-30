namespace DnaEntropyGraph.Core.Cloud;

/// <summary>
/// Codes the Google gateways put in <see cref="CloudError.Code"/> that Core reads back by value. One constant, used at every gateway site and by every reader,
/// so a rename cannot leave a reader silently falling back to the gateway's kind.
/// </summary>
public static class CloudErrorCodes
{
    /// <summary>A listing returned the same page token twice, so the list may be incomplete. The gateways give it Network (the reconciler reads that as "no answer"); it is not a dropped connection.</summary>
    public const string ListPaginationStuck = "LIST_PAGINATION_STUCK";
}
