using DnaEntropyGraph.Core.Cloud;
using Google;
using Google.Apis.Cloudbilling.v1;
using BillingData = Google.Apis.Cloudbilling.v1.Data;

namespace DnaEntropyGraph.Cloud.Rest;

/// <summary>
/// The real <see cref="IPricingCatalogGateway"/> over Cloud Billing v1 <c>services.skus.list</c> for the Compute Engine service (issue #214,
/// docs/cloud_design.md section 10). Read-only; every page is one request through <see cref="CloudCallPipeline"/> (<c>singleRequest: true</c>).
/// Only the first pricing tier is kept (the one that starts at zero) and only dollar prices: a SKU with no usable price is left out, never
/// priced at zero. THEORY (unverified, no live read): <c>6F81-5844-456A</c> is the Compute Engine service id and a signed-in user's token may
/// list it; if not, the read fails with a classified error and the app keeps the list that ships with it. docs/ToTest.md carries the real-catalog row.
/// </summary>
internal sealed class GooglePricingCatalogGateway : IPricingCatalogGateway
{
    internal const string ComputeEngineService = "services/6F81-5844-456A";

    private const int PageSize = 5000;

    private readonly CloudbillingService _service;
    private readonly CloudCallPipeline _pipeline;
    private readonly TimeProvider _time;

    public GooglePricingCatalogGateway(CloudbillingService service, CloudCallPipeline pipeline, TimeProvider? time = null)
    {
        _service = service;
        _pipeline = pipeline;
        _time = time ?? TimeProvider.System;
    }

    public async Task<IReadOnlyList<CatalogSku>> ListComputeSkusAsync(CancellationToken cancellationToken)
    {
        var skus = new List<CatalogSku>();
        string? pageToken = null;
        var paging = new PageTokenGuard();
        do
        {
            var requested = pageToken;
            var page = await CallAsync(
                token =>
                {
                    var request = _service.Services.Skus.List(ComputeEngineService);
                    request.CurrencyCode = "USD";
                    request.PageSize = PageSize;
                    request.PageToken = requested;
                    return request.ExecuteAsync(token);
                },
                cancellationToken).ConfigureAwait(false);

            skus.AddRange((page.Skus ?? []).Select(s => ToSku(s, _time.GetUtcNow())).OfType<CatalogSku>());

            // A partial catalog would read as "no such SKU", so a repeated token (any seen earlier in the walk) fails the whole read, as a stuck listing and not as a network problem.
            pageToken = paging.Advance(page.NextPageToken);
        }
        while (pageToken is not null);

        return skus;
    }

    /// <summary>
    /// A SKU can list several pricing entries (a price that changes on a date): the one in force now is the latest whose effective time has
    /// come; when every entry is still in the future, the earliest of them is the next best answer.
    /// </summary>
    internal static BillingData.PricingInfo? CurrentPricing(IList<BillingData.PricingInfo>? entries, DateTimeOffset now)
    {
        var dated = (entries ?? []).Select(e => (Entry: e, At: e.EffectiveTimeDateTimeOffset ?? DateTimeOffset.MinValue)).ToList();
        var inForce = dated.Where(d => d.At <= now).OrderByDescending(d => d.At).FirstOrDefault();
        return inForce.Entry ?? dated.OrderBy(d => d.At).FirstOrDefault().Entry;
    }

    private static CatalogSku? ToSku(BillingData.Sku sku, DateTimeOffset now)
    {
        var expression = CurrentPricing(sku.PricingInfo, now)?.PricingExpression;
        var tier = expression?.TieredRates?.FirstOrDefault(r => (r.StartUsageAmount ?? 0) == 0);
        if (expression is null || tier?.UnitPrice is not { } price || price.CurrencyCode != "USD" || string.IsNullOrEmpty(sku.SkuId))
        {
            return null;
        }

        var usd = (price.Units ?? 0) + ((price.Nanos ?? 0) / 1_000_000_000.0);
        return new CatalogSku(
            sku.SkuId,
            sku.Description ?? string.Empty,
            sku.Category?.ResourceFamily ?? string.Empty,
            sku.Category?.ResourceGroup ?? string.Empty,
            sku.Category?.UsageType ?? string.Empty,
            sku.ServiceRegions?.ToList() ?? [],
            usd,
            expression.UsageUnit ?? string.Empty);
    }

    private Task<T> CallAsync<T>(Func<CancellationToken, Task<T>> request, CancellationToken cancellationToken)
        => _pipeline.ExecuteAsync(
            "cloudbilling.services.skus.list",
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
            singleRequest: true,
            // The price list is optional: its failures must not open the breaker that guards creating and polling a run (review M4).
            bypassBreaker: true);
}
