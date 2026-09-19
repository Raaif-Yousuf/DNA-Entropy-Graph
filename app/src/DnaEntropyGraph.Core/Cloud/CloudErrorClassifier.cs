namespace DnaEntropyGraph.Core.Cloud;

/// <summary>
/// Buckets a <see cref="CloudError"/> into a <see cref="CloudErrorKind"/>.
/// Issue #57. Two layers, checked in this fixed order, because GCP does not
/// always give a structured code and the fallback must still agree with the
/// prototype's own, already-measured behaviour:
///
/// 1. Structured signals first - <see cref="CloudError.Code"/> and
///    <see cref="CloudError.HttpStatus"/> - per docs/cloud_design.md
///    section 5's table. Compute is operation-based: the real error lives
///    in the polled <c>Operation.Error</c>, not the initial Insert
///    response, and a caller mapping that into <see cref="CloudError"/> is
///    expected to populate <see cref="CloudError.Code"/> whenever GCP gives
///    one - this classifier never re-derives a code from the message.
/// 2. A case-insensitive substring fallback over
///    <see cref="CloudError.Message"/>, evaluated in the exact fixed
///    priority order recorded in
///    <c>tests/contract-fixtures/cloud_error_classification.json</c>'s
///    <c>evaluation_order</c> field (billing checked before quota, because
///    some billing errors also mention "account" - see that file's
///    <c>matching_notes</c>). This is a direct port of the prototype's
///    <c>worker/legacy/cloud/gcloud.py::classify_create_error</c>; do not
///    "simplify" the order, it is load-bearing.
///
/// <c>org_policy</c> has no substring fallback: the prototype never had
/// this bucket (it predates any org-policy-blocked project being seen), so
/// it is reachable only through the structured HTTP 412 /
/// <c>CONDITION_NOT_MET</c> signal.
/// </summary>
public static class CloudErrorClassifier
{
    public static CloudErrorKind Classify(CloudError error)
    {
        ArgumentNullException.ThrowIfNull(error);

        var code = error.Code?.ToUpperInvariant() ?? string.Empty;
        var status = error.HttpStatus;
        var message = error.Message ?? string.Empty;
        var lower = message.ToLowerInvariant();

        // --- 1. Structured signals (docs/cloud_design.md section 5) ---

        // A capacity-shaped structured code. RESOURCE_NOT_FOUND and
        // UNSUPPORTED_OPERATION are ambiguous on their own (they cover far
        // more than GPU capacity), so they only count here when the
        // message itself is about the accelerator - otherwise fall through
        // to the substring stage rather than guess.
        if (code.Contains("ZONE_RESOURCE_POOL_EXHAUSTED", StringComparison.Ordinal))
        {
            return CloudErrorKind.Stockout;
        }

        if ((code is "RESOURCE_NOT_FOUND" or "UNSUPPORTED_OPERATION") && lower.Contains("accelerator", StringComparison.Ordinal))
        {
            return CloudErrorKind.Stockout;
        }

        if (code == "QUOTA_EXCEEDED")
        {
            return CloudErrorKind.Quota;
        }

        if (code == "BILLING_DISABLED")
        {
            return CloudErrorKind.Billing;
        }

        if (status == 403 && (lower.Contains("accessnotconfigured", StringComparison.Ordinal) || lower.Contains("has not been used in project", StringComparison.Ordinal)))
        {
            return CloudErrorKind.ApiDisabled;
        }

        if (status == 403 && lower.Contains("billing", StringComparison.Ordinal))
        {
            return CloudErrorKind.Billing;
        }

        if (status == 403 && (code == "IAM_PERMISSION_DENIED" || lower.Contains("forbidden", StringComparison.Ordinal) || lower.Contains("actas", StringComparison.Ordinal) || lower.Contains("permission", StringComparison.Ordinal)))
        {
            return CloudErrorKind.Permission;
        }

        if (status == 412 || code == "CONDITION_NOT_MET" || lower.Contains("constraints/", StringComparison.Ordinal))
        {
            return CloudErrorKind.OrgPolicy;
        }

        if (status == 409 || code.Contains("ALREADY_EXISTS", StringComparison.Ordinal))
        {
            return CloudErrorKind.AlreadyExists;
        }

        // --- 2. Substring fallback, prototype's fixed evaluation order ---
        return ClassifyBySubstring(lower);
    }

    private static CloudErrorKind ClassifyBySubstring(string s)
    {
        // billing: checked before quota, because some billing errors also mention "account".
        if (s.Contains("billing", StringComparison.Ordinal)
            && (s.Contains("enable", StringComparison.Ordinal)
                || s.Contains("disabled", StringComparison.Ordinal)
                || s.Contains("not found", StringComparison.Ordinal)
                || s.Contains("not active", StringComparison.Ordinal)
                || s.Contains("account", StringComparison.Ordinal)))
        {
            return CloudErrorKind.Billing;
        }

        if (s.Contains("has not been used in project", StringComparison.Ordinal)
            || s.Contains("accessnotconfigured", StringComparison.Ordinal)
            || s.Contains("serviceusage", StringComparison.Ordinal)
            || (s.Contains("compute", StringComparison.Ordinal) && s.Contains("api", StringComparison.Ordinal) && (s.Contains("disabled", StringComparison.Ordinal) || s.Contains("not enabled", StringComparison.Ordinal)))
            || s.Contains("it is disabled", StringComparison.Ordinal))
        {
            return CloudErrorKind.ApiDisabled;
        }

        if (s.Contains("quota", StringComparison.Ordinal))
        {
            return CloudErrorKind.Quota;
        }

        if (s.Contains("stockout", StringComparison.Ordinal)
            || s.Contains("zone_resource_pool_exhausted", StringComparison.Ordinal)
            || s.Contains("does not have enough resources", StringComparison.Ordinal)
            || s.Contains("resource_availability", StringComparison.Ordinal))
        {
            return CloudErrorKind.Stockout;
        }

        if (s.Contains("already exists", StringComparison.Ordinal) || s.Contains("resource already exists", StringComparison.Ordinal))
        {
            return CloudErrorKind.AlreadyExists;
        }

        if (s.Contains("permission", StringComparison.Ordinal)
            || s.Contains("forbidden", StringComparison.Ordinal)
            || s.Contains("not authorized", StringComparison.Ordinal)
            || s.Contains("iam", StringComparison.Ordinal))
        {
            return CloudErrorKind.Permission;
        }

        if (s.Contains("could not reach", StringComparison.Ordinal)
            || s.Contains("connection", StringComparison.Ordinal)
            || s.Contains("network is unreachable", StringComparison.Ordinal)
            || s.Contains("timed out", StringComparison.Ordinal)
            || s.Contains("timeout", StringComparison.Ordinal))
        {
            return CloudErrorKind.Network;
        }

        return CloudErrorKind.Other;
    }
}
