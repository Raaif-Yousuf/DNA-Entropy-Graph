using System.Text.Json;
using DnaEntropyGraph.Core.Cloud;
using Google;

namespace DnaEntropyGraph.Cloud.Rest;

/// <summary>A <c>google.rpc.Status</c> reduced to what the app decides on, whether it came as an HTTP error body or as the <c>error</c> of a polled operation.</summary>
internal sealed record RpcStatus(int? HttpStatus, string Status, string Message, IReadOnlyList<string> Reasons, bool HasQuotaFailure);

/// <summary>
/// Turns what Google says went wrong into the one exception the rest of the app understands (Hard Rule 7: Core never
/// sees a Google type). The shapes parsed here are Google's documented <c>google.rpc.Status</c> with
/// <c>ErrorInfo</c> and <c>QuotaFailure</c> details. THEORY (unverified): that Resource Manager marks a project-limit
/// refusal with a QuotaFailure detail or the word "quota", and an organization-policy refusal with an
/// ORG_POLICY reason, a <c>constraints/</c> id or the words "organization policy": no live project was available to
/// capture one, so docs/ToTest.md carries the row that proves it.
/// </summary>
internal static class GoogleApiErrors
{
    // google.rpc.Code, by number: the name Google uses and the HTTP status it maps to (google/rpc/code.proto).
    private static readonly (string Name, int Http)[] RpcCodes =
    [
        ("OK", 200), ("CANCELLED", 499), ("UNKNOWN", 500), ("INVALID_ARGUMENT", 400), ("DEADLINE_EXCEEDED", 504),
        ("NOT_FOUND", 404), ("ALREADY_EXISTS", 409), ("PERMISSION_DENIED", 403), ("RESOURCE_EXHAUSTED", 429),
        ("FAILED_PRECONDITION", 400), ("ABORTED", 409), ("OUT_OF_RANGE", 400), ("UNIMPLEMENTED", 501), ("INTERNAL", 500),
        ("UNAVAILABLE", 503), ("DATA_LOSS", 500), ("UNAUTHENTICATED", 401),
    ];

    /// <summary>The error of a failed HTTP call, from its JSON body when Google sent one, else from the status line.</summary>
    public static RpcStatus FromApiException(GoogleApiException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        var http = (int?)exception.HttpStatusCode;
        var body = exception.Error?.ErrorResponseContent;
        if (!string.IsNullOrWhiteSpace(body) && TryParseBody(body, http, out var parsed))
        {
            return parsed;
        }

        return new RpcStatus(http, StatusNameForHttp(http), exception.Error?.Message ?? exception.Message, [], false);
    }

    /// <summary>The <c>error</c> of a finished operation (a gRPC code number, a message, and the detail objects).</summary>
    public static RpcStatus FromOperationError(int? grpcCode, string? message, IEnumerable<IDictionary<string, object>>? details)
    {
        var code = grpcCode is >= 0 and < 17 ? grpcCode.Value : 2;
        var reasons = new List<string>();
        var quota = false;
        foreach (var detail in details ?? [])
        {
            if (detail.TryGetValue("@type", out var type) && type?.ToString()?.EndsWith("google.rpc.QuotaFailure", StringComparison.Ordinal) == true)
            {
                quota = true;
            }

            if (detail.TryGetValue("reason", out var reason) && reason?.ToString() is { Length: > 0 } text)
            {
                reasons.Add(text);
            }
        }

        return new RpcStatus(RpcCodes[code].Http, RpcCodes[code].Name, message ?? string.Empty, reasons, quota);
    }

    /// <summary>
    /// The exception a gateway throws. <paramref name="codeFor"/> lets one operation give a project-limit or policy
    /// refusal its own setup code (<see cref="SetupErrorCodes"/>); everything else keeps Google's status name.
    /// </summary>
    public static CloudOperationException ToException(RpcStatus status, Func<CloudErrorKind, string?>? codeFor = null)
    {
        ArgumentNullException.ThrowIfNull(status);

        var kind = KindOf(status);
        var code = IsRateLimit(status) ? CloudErrorClassifier.RateLimitCode : codeFor?.Invoke(kind) ?? status.Status;
        var error = new CloudError(code, status.HttpStatus, status.Message);

        // A refusal to run a VM as the worker service account is a permission error with its own code and action (issue #54).
        if (kind == CloudErrorKind.Permission && CloudErrorClassifier.IsActAsDenial(error))
        {
            error = error with { Code = SetupErrorCodes.PermissionActAs };
        }

        return new CloudOperationException(error, kind);
    }

    // Wording of a per-minute request rate limit, as Google words it ("Quota exceeded for quota metric 'Requests' and
    // limit 'Requests per minute' ..."). A project-count limit never says per minute, per second or "requests".
    private static readonly string[] RateLimitWording =
    [
        "per minute", "per second", "per 100 seconds", "requests per", "rate limit", "too many requests", "ratelimit",
    ];

    /// <summary>
    /// A per-minute rate limit clears in a minute: it is retried, never "you reached your project limit". Google marks
    /// one with the ErrorInfo reason <c>RATE_LIMIT_EXCEEDED</c>, but the reason is not always sent: a 429
    /// RESOURCE_EXHAUSTED whose message names a per-minute or per-second request limit is one too, QuotaFailure or not.
    /// </summary>
    private static bool IsRateLimit(RpcStatus status)
        => status.Reasons.Any(r => string.Equals(r, CloudErrorClassifier.RateLimitCode, StringComparison.OrdinalIgnoreCase))
            || (status.Status == "RESOURCE_EXHAUSTED"
                && RateLimitWording.Any(w => status.Message.Contains(w, StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// A 403 that says a quota was exceeded (Cloud Billing answers "Cloud billing quota exceeded" this way). A 403 that
    /// only mentions the word quota while denying permission ("use another project to pass your quota and billing")
    /// is a permission error, so the wording must say "exceeded" and must not read as a permission denial.
    /// </summary>
    private static bool IsQuotaExceeded403(RpcStatus status, string lower)
        => status.HttpStatus == 403
            && lower.Contains("quota", StringComparison.Ordinal)
            && lower.Contains("exceeded", StringComparison.Ordinal)
            && !lower.Contains("permission", StringComparison.Ordinal)
            && !lower.Contains("does not have", StringComparison.Ordinal)
            && !lower.Contains("denied", StringComparison.Ordinal);

    public static CloudErrorKind KindOf(RpcStatus status)
    {
        var lower = status.Message.ToLowerInvariant();

        if (IsRateLimit(status))
        {
            return CloudErrorKind.Other;
        }

        // The structured reason says what is wrong even when the message is worded differently.
        if (status.Reasons.Any(r => string.Equals(r, "SERVICE_DISABLED", StringComparison.OrdinalIgnoreCase)))
        {
            return CloudErrorKind.ApiDisabled;
        }

        if (status.Reasons.Any(r => string.Equals(r, "BILLING_DISABLED", StringComparison.OrdinalIgnoreCase)))
        {
            return CloudErrorKind.Billing;
        }

        // USER_PROJECT_DENIED is a permission error whose text says "pass your quota and billing": the structured reason
        // decides before any quota wording does.
        if (status.Reasons.Any(r => string.Equals(r, "USER_PROJECT_DENIED", StringComparison.OrdinalIgnoreCase)))
        {
            return CloudErrorKind.Permission;
        }

        if (status.HasQuotaFailure
            || (status.Status == "RESOURCE_EXHAUSTED" && lower.Contains("quota", StringComparison.Ordinal))
            || IsQuotaExceeded403(status, lower))
        {
            return CloudErrorKind.Quota;
        }

        // A plain PERMISSION_DENIED 403 can quote a "constraints/..." id without being an organization-policy refusal
        // (it only says the caller may not see something), so the wording counts only when the error is not that.
        var plainDenial = status.HttpStatus == 403 && status.Status == "PERMISSION_DENIED";
        if (status.Reasons.Any(r => r.Contains("ORG_POLICY", StringComparison.OrdinalIgnoreCase))
            || status.HttpStatus == 412
            || (!plainDenial
                && (lower.Contains("constraints/", StringComparison.Ordinal)
                    || lower.Contains("org policy", StringComparison.Ordinal)
                    || lower.Contains("organization policy", StringComparison.Ordinal))))
        {
            return CloudErrorKind.OrgPolicy;
        }

        return CloudErrorClassifier.Classify(new CloudError(status.Status, status.HttpStatus, status.Message));
    }

    private static bool TryParseBody(string body, int? http, out RpcStatus status)
    {
        status = null!;
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var reasons = new List<string>();
            var quota = false;
            if (error.TryGetProperty("details", out var details) && details.ValueKind == JsonValueKind.Array)
            {
                foreach (var detail in details.EnumerateArray())
                {
                    if (detail.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    if (detail.TryGetProperty("@type", out var type) && type.GetString()?.EndsWith("google.rpc.QuotaFailure", StringComparison.Ordinal) == true)
                    {
                        quota = true;
                    }

                    if (detail.TryGetProperty("reason", out var reason) && reason.ValueKind == JsonValueKind.String && reason.GetString() is { Length: > 0 } text)
                    {
                        reasons.Add(text);
                    }
                }
            }

            var code = error.TryGetProperty("code", out var codeElement) && codeElement.TryGetInt32(out var parsedCode) ? parsedCode : http;
            var name = error.TryGetProperty("status", out var statusElement) && statusElement.ValueKind == JsonValueKind.String
                ? statusElement.GetString()!
                : StatusNameForHttp(code);
            var message = error.TryGetProperty("message", out var messageElement) && messageElement.ValueKind == JsonValueKind.String ? messageElement.GetString()! : string.Empty;
            status = new RpcStatus(code, name, message, reasons, quota);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string StatusNameForHttp(int? http) => http switch
    {
        400 => "INVALID_ARGUMENT",
        401 => "UNAUTHENTICATED",
        403 => "PERMISSION_DENIED",
        404 => "NOT_FOUND",
        409 => "ALREADY_EXISTS",
        429 => "RESOURCE_EXHAUSTED",
        500 => "INTERNAL",
        503 => "UNAVAILABLE",
        504 => "DEADLINE_EXCEEDED",
        _ => "UNKNOWN",
    };
}
