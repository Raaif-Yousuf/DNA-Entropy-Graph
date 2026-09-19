namespace DnaEntropyGraph.Core.Cloud;

/// <summary>
/// A Google Cloud operation/API error, reduced to the fields
/// <see cref="CloudErrorClassifier"/> needs. Hard Rule 7: Core never
/// references <c>Google.*</c>, so <c>DnaEntropyGraph.Cloud</c>'s real
/// gateways are responsible for mapping a real
/// <c>Google.Cloud.Compute.V1.Operation.Error</c> / <c>RpcException</c> /
/// <c>GoogleApiException</c> into this shape before anything in Core ever
/// sees it. <see cref="DnaEntropyGraph.Cloud.FakeGcp"/> constructs this
/// directly when scripting a failure, matching the same fields a real
/// mapping would produce, so a test exercising the fake and a test
/// exercising the real classifier's fixtures are asserting the same
/// contract.
/// </summary>
/// <param name="Code">
/// The structured error code where GCP provides one, e.g.
/// <c>ZONE_RESOURCE_POOL_EXHAUSTED</c>, <c>QUOTA_EXCEEDED</c>,
/// <c>BILLING_DISABLED</c>, <c>CONDITION_NOT_MET</c>. Null when only an
/// HTTP status and message are available.
/// </param>
/// <param name="HttpStatus">The HTTP status code, e.g. 403, 409, 412. Null for a transport-level failure (network).</param>
/// <param name="Message">
/// The human-readable detail. <see cref="CloudErrorClassifier"/> falls
/// back to a case-insensitive substring match against this field only
/// when neither <paramref name="Code"/> nor <paramref name="HttpStatus"/>
/// carries a signal it recognizes - see
/// <c>tests/contract-fixtures/cloud_error_classification.json</c> for the
/// exact substrings and their fixed evaluation order.
/// </param>
public sealed record CloudError(string? Code, int? HttpStatus, string Message);

/// <summary>
/// The nine buckets a <see cref="CloudError"/> is classified into
/// (docs/cloud_design.md section 5). <c>Billing</c>, <c>ApiDisabled</c>,
/// <c>Permission</c> and <c>OrgPolicy</c> are project-wide and abort a zone
/// ladder immediately; <c>Quota</c> and <c>Stockout</c> are per-region/
/// per-zone and are worth continuing past (CLAUDE.md Critical Pitfalls:
/// "quota is not stockout").
/// </summary>
public enum CloudErrorKind
{
    Billing,
    ApiDisabled,
    Quota,
    Stockout,
    AlreadyExists,
    Permission,
    OrgPolicy,
    Network,
    Other,
}

/// <summary>
/// Thrown by a gateway (real or <see cref="DnaEntropyGraph.Cloud.FakeGcp"/>)
/// for any Google Cloud call that fails. Carries both the raw
/// <see cref="CloudError"/> and the <see cref="CloudErrorKind"/> the caller
/// needs to decide whether to abort or continue (docs/cloud_design.md
/// section 5's abort-vs-continue table).
/// </summary>
public sealed class CloudOperationException : Exception
{
    public CloudOperationException(CloudError error, CloudErrorKind kind)
        : base(error.Message)
    {
        Error = error;
        Kind = kind;
    }

    public CloudError Error { get; }

    public CloudErrorKind Kind { get; }
}
