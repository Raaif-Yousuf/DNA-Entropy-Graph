using DnaEntropyGraph.Core.Cloud;

namespace DnaEntropyGraph.Cloud;

// The Cloud Billing Catalog (issue #214): IPricingCatalogGateway with scripted failures. A partial file, like the other wizard-era fakes,
// so the runner fake does not grow further. Nothing is invented: with no SKUs scripted the catalog is empty, which the app reads as "no match".
public sealed partial class FakeGcp : IPricingCatalogGateway
{
    private readonly object _pricingGate = new();
    private List<CatalogSku> _catalogSkus = [];
    private CloudErrorKind? _catalogFailure;
    private int _catalogReads;

    /// <summary>How many times the catalog was read (a daily cache that works shows here as one).</summary>
    public int CatalogReads
    {
        get
        {
            lock (_pricingGate)
            {
                return _catalogReads;
            }
        }
    }

    /// <summary>The SKUs <see cref="ListComputeSkusAsync"/> returns.</summary>
    public FakeGcp WithCatalogSkus(params CatalogSku[] skus)
    {
        lock (_pricingGate)
        {
            _catalogSkus = [.. skus];
        }

        return this;
    }

    /// <summary>Every catalog read throws a <see cref="CloudOperationException"/> of this kind (Permission: 403, ApiDisabled: Cloud Billing API off, Network: transport).</summary>
    public FakeGcp WithCatalogReadFailing(CloudErrorKind kind)
    {
        lock (_pricingGate)
        {
            _catalogFailure = kind;
        }

        return this;
    }

    public Task<IReadOnlyList<CatalogSku>> ListComputeSkusAsync(CancellationToken cancellationToken)
    {
        ThrowIfCatalogNotConnected();
        lock (_pricingGate)
        {
            _catalogReads++;
            if (_catalogFailure is { } kind)
            {
                throw kind switch
                {
                    CloudErrorKind.Permission => Build(kind, "PERMISSION_DENIED", 403, "The account may not read the price catalog."),
                    CloudErrorKind.ApiDisabled => Build(kind, "SERVICE_DISABLED", 403, "The Cloud Billing API is not enabled."),
                    CloudErrorKind.Network => Build(kind, null, null, "The price catalog could not be reached."),
                    _ => Build(kind, null, 500, "The price catalog read failed."),
                };
            }

            return Task.FromResult<IReadOnlyList<CatalogSku>>([.. _catalogSkus]);
        }
    }
}
