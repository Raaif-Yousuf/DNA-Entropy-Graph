namespace DnaEntropyGraph.Core.Cloud;

/// <summary>
/// One line of the Cloud Billing Catalog for Compute Engine, reduced to what the price resolver reads (issue #214). <see cref="UsdPerUnit"/>
/// is the dollar price of one <see cref="UsageUnit"/> in the first tier (the one that starts at zero): <c>h</c> for a core or a GPU,
/// <c>GBy.h</c> for memory, <c>GBy.mo</c> for a disk.
/// </summary>
public sealed record CatalogSku(
    string SkuId,
    string Description,
    string ResourceFamily,
    string ResourceGroup,
    string UsageType,
    IReadOnlyList<string> Regions,
    double UsdPerUnit,
    string UsageUnit);

/// <summary>
/// The Cloud Billing Catalog API, as far as the app needs it (issue #214, docs/cloud_design.md section 10). Hard Rule 7: Core only sees this
/// interface; <c>DnaEntropyGraph.Cloud</c> implements it over Google and <c>FakeGcp</c> with scripted failures.
/// </summary>
public interface IPricingCatalogGateway
{
    /// <summary>
    /// Every Compute Engine SKU that has a dollar price (all pages). A failure throws a <see cref="CloudOperationException"/>; a catalog that
    /// could only be half read is a failure, never a shorter list.
    /// </summary>
    Task<IReadOnlyList<CatalogSku>> ListComputeSkusAsync(CancellationToken cancellationToken);
}
