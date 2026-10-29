using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using DnaEntropyGraph.Core.Cloud;

namespace DnaEntropyGraph.Core.Cost;

/// <summary>Where the prices in use came from. Each is something the page can say in plain words.</summary>
public enum PricingOrigin
{
    /// <summary>The list that ships with the app (<c>Assets\pricing.json</c>).</summary>
    Shipped,

    /// <summary>Read from the Cloud Billing Catalog during this session.</summary>
    Catalog,

    /// <summary>Read from the catalog earlier today and kept on this PC.</summary>
    Cache,

    /// <summary>Read from the catalog on an earlier day; today's read failed, so the older catalog prices are still better than the shipped list.</summary>
    CacheStale,
}

/// <summary>Why today's catalog read did not give the prices in use. <see cref="None"/> when it did, or has not been tried.</summary>
public enum PricingFallbackReason
{
    None,
    SignedOut,
    PermissionDenied,
    ApiDisabled,
    Network,
    Other,

    /// <summary>The catalog was read but did not price the machines the app uses.</summary>
    NoMatch,
}

/// <summary>What the page needs to say "prices as of <see cref="AsOf"/>, from the catalog / from the list that came with the app, because ...".</summary>
/// <param name="SpotKeptFromShipped">Machines whose Spot price is the shipped list's because the catalog gave none; null when there are none (so the status line can say so).</param>
public sealed record PricingSnapshot(PricingOrigin Origin, DateOnly AsOf, PricingFallbackReason Reason, IReadOnlyList<string>? SpotKeptFromShipped = null);

/// <summary>The prices in use, and where they came from.</summary>
public interface IPricingStatus
{
    PricingSnapshot Current { get; }
}

/// <summary>A source that can ask for newer prices. Never throws except for cancellation: a failed read is <see cref="PricingFallbackReason"/> on <see cref="IPricingStatus.Current"/>.</summary>
public interface IPricingRefresh
{
    Task RefreshIfStaleAsync(CancellationToken cancellationToken);
}

/// <summary>Where the last catalog table is kept between launches (a file under <c>%LOCALAPPDATA%</c> in the app).</summary>
public interface IPricingCacheStore
{
    /// <summary>The last written text, or null when there is none or it cannot be read.</summary>
    string? Read();

    /// <summary>Best effort: a cache that cannot be written is a cache that is not there, never an error.</summary>
    void Write(string json);
}

public sealed class FilePricingCacheStore(string path) : IPricingCacheStore
{
    public string? Read()
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Write(string json)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".tmp";
            File.WriteAllText(temp, json, new System.Text.UTF8Encoding(false));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort, see IPricingCacheStore.
        }
    }
}

/// <summary>
/// The price list the app uses (issue #214): the Cloud Billing Catalog's prices when they can be had, cached for a day, else the list that
/// ships with the app. <see cref="Load"/> is instant and never reads the network: it serves what is known now (today's cache, an older
/// cache, or the shipped list). <see cref="RefreshIfStaleAsync"/> is the one place the catalog is asked, at most once a day, and after a
/// failure not again for an hour. Whatever it serves, <see cref="Current"/> says which it is and from when.
/// </summary>
public sealed class CatalogPricingSource : IPricingSource, IPricingStatus, IPricingRefresh
{
    /// <summary>A failed read is not repeated on every estimate.</summary>
    public static readonly TimeSpan RetryAfterFailure = TimeSpan.FromHours(1);

    /// <summary>
    /// The budget for the WHOLE paged read (several requests, each of which the pipeline may take up to its 30 second attempt cap over), so a slow link
    /// still gets a multi-page catalog. Nothing waits on it that long: <see cref="CostEstimateService"/> answers from what is known and lets the read finish.
    /// </summary>
    public static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(90);

    private readonly IPricingSource _shipped;
    private readonly IPricingCatalogGateway _gateway;
    private readonly IPricingCacheStore _cache;
    private readonly TimeProvider _time;
    private readonly TimeSpan _readTimeout;
    private readonly int _resolverVersion;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _state = new();

    private PricingLoadResult? _serving;
    private PricingSnapshot? _snapshot;
    private DateTimeOffset? _lastFailure;

    public CatalogPricingSource(IPricingSource shipped, IPricingCatalogGateway gateway, IPricingCacheStore cache, TimeProvider time, TimeSpan? readTimeout = null, int? resolverVersion = null)
    {
        _resolverVersion = resolverVersion ?? CatalogPriceResolver.Version;
        _readTimeout = readTimeout ?? ReadTimeout;
        _shipped = shipped;
        _gateway = gateway;
        _cache = cache;
        _time = time;
    }

    public PricingSnapshot Current
    {
        get
        {
            EnsureLoaded();
            lock (_state)
            {
                // Catalog prices read on an earlier day are stale however they got here (a session can cross midnight).
                return _snapshot!.Origin is PricingOrigin.Catalog or PricingOrigin.Cache && _snapshot.AsOf < Today()
                    ? _snapshot with { Origin = PricingOrigin.CacheStale }
                    : _snapshot;
            }
        }
    }

    public PricingLoadResult Load()
    {
        EnsureLoaded();
        lock (_state)
        {
            return _serving!;
        }
    }

    public async Task RefreshIfStaleAsync(CancellationToken cancellationToken)
    {
        EnsureLoaded();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!NeedsRefresh())
            {
                return;
            }

            var shipped = _shipped.Load();
            if (shipped.Table is not { } shippedTable)
            {
                // Without the shipped list there is nothing to fill the machines the catalog does not price from; not a retry storm either.
                Failed(PricingFallbackReason.Other);
                return;
            }

            IReadOnlyList<CatalogSku> skus;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(_readTimeout);
                skus = await _gateway.ListComputeSkusAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                Failed(PricingFallbackReason.Network);
                return;
            }
            catch (CloudOperationException ex)
            {
                Failed(ex.Kind switch
                {
                    CloudErrorKind.Permission or CloudErrorKind.OrgPolicy => PricingFallbackReason.PermissionDenied,
                    CloudErrorKind.ApiDisabled => PricingFallbackReason.ApiDisabled,
                    CloudErrorKind.Network => PricingFallbackReason.Network,
                    _ => PricingFallbackReason.Other,
                });
                return;
            }
            catch (AccountAuthException ex)
            {
                // Only "there is no sign-in" is SignedOut (which is retried at once after signing in). A network problem backs off like any other
                // network failure, and any other auth failure is Other: neither may hammer the catalog on every estimate.
                Failed(ex.Code switch
                {
                    AuthErrorCodes.SigninExpired or AuthErrorCodes.AccountNotFound => PricingFallbackReason.SignedOut,
                    AuthErrorCodes.NetworkUnavailable => PricingFallbackReason.Network,
                    _ => PricingFallbackReason.Other,
                });
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Failed(PricingFallbackReason.Other);
                return;
            }

            var today = Today();
            var resolved = CatalogPriceResolver.Resolve(skus, shippedTable, today);
            if (resolved.Table is not { } table)
            {
                Failed(PricingFallbackReason.NoMatch);
                return;
            }

            _cache.Write(WithMetadata(table.ToJson(), Fingerprint(shippedTable), resolved.SpotKept));
            lock (_state)
            {
                _serving = new PricingLoadResult(table, null, null);
                _snapshot = new PricingSnapshot(PricingOrigin.Catalog, today, PricingFallbackReason.None, resolved.SpotKept);
                _lastFailure = null;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Which shipped list a cache was made beside: a hash of its own JSON form, so any change to it (not only its date) invalidates the cache.</summary>
    private string Fingerprint(PricingTable? shipped)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes((shipped?.ToJson() ?? "none") + "|resolver-v" + _resolverVersion)))[..16];

    private static string WithMetadata(string tableJson, string fingerprint, IReadOnlyList<string>? spotKept)
    {
        var document = JsonNode.Parse(tableJson)!.AsObject();
        document["shippedFingerprint"] = fingerprint;
        if (spotKept is { Count: > 0 })
        {
            document["spotKeptFromShipped"] = new JsonArray(spotKept.Select(m => (JsonNode?)JsonValue.Create(m)).ToArray());
        }

        return document.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }).Replace("\r\n", "\n") + "\n";
    }

    private static IReadOnlyList<string>? ReadSpotKept(string json)
    {
        var names = (JsonNode.Parse(json) as JsonObject)?["spotKeptFromShipped"] as JsonArray;
        var kept = names?.Select(n => (string?)n).OfType<string>().ToList();
        return kept is { Count: > 0 } ? kept : null;
    }

    private bool FingerprintMatches(string json, PricingTable? shipped)
    {
        try
        {
            return JsonNode.Parse(json) is JsonObject document && (string?)document["shippedFingerprint"] == Fingerprint(shipped);
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }

    private DateOnly Today() => DateOnly.FromDateTime(_time.GetUtcNow().UtcDateTime);

    private bool NeedsRefresh()
    {
        lock (_state)
        {
            if (_snapshot!.Origin is PricingOrigin.Catalog or PricingOrigin.Cache && _snapshot.AsOf >= Today())
            {
                return false;
            }

            return _lastFailure is not { } failed || _time.GetUtcNow() - failed >= RetryAfterFailure;
        }
    }

    private void Failed(PricingFallbackReason reason)
    {
        lock (_state)
        {
            // Signed out is not a reason to wait an hour: the next estimate after signing in must try at once, and a refused token costs nothing.
            _lastFailure = reason == PricingFallbackReason.SignedOut ? null : _time.GetUtcNow();
            _snapshot = _snapshot! with { Reason = reason };
        }
    }

    private void EnsureLoaded()
    {
        lock (_state)
        {
            if (_serving is not null)
            {
                return;
            }

            var shipped = _shipped.Load();

            // A cache written beside another shipped list (the app was updated since) can hold rows kept from that older list, so it is not used.
            // A cache dated tomorrow or later than one day ahead did not come from a clock this one agrees with: one day of slack covers a time zone
            // or a clock a little ahead; anything further is not trusted (it would otherwise read as fresh for a long time and never refresh).
            if (_cache.Read() is { } json && FingerprintMatches(json, shipped.Table) && PricingTable.Parse(json) is { Table: { } cached } && cached.AsOf <= Today().AddDays(1))
            {
                var fresh = cached.AsOf >= Today();
                _serving = new PricingLoadResult(cached, null, null);
                _snapshot = new PricingSnapshot(fresh ? PricingOrigin.Cache : PricingOrigin.CacheStale, cached.AsOf, PricingFallbackReason.None, ReadSpotKept(json));
                return;
            }

            _serving = shipped;
            _snapshot = new PricingSnapshot(PricingOrigin.Shipped, shipped.Table?.AsOf ?? default, PricingFallbackReason.None);
        }
    }
}
