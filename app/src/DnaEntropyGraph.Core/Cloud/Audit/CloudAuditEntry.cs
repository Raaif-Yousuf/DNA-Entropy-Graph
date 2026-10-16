namespace DnaEntropyGraph.Core.Cloud.Audit;

/// <summary>
/// One mutating cloud call, as support needs to see it (issue #245). Only identifiers: the installation id, the job id, a resource
/// NAME the app made (VM, bucket, an object prefix such as <c>jobs/&lt;id&gt;</c>), Google's request and operation ids, the outcome
/// class and the duration. Never a sequence, an input file name, an object name below the job prefix, an error message or an email
/// (CLAUDE.md Stack row "Logs").
/// </summary>
/// <param name="Outcome"><c>ok</c>, <c>cancelled</c>, or the <see cref="CloudErrorClassifier"/> bucket (<c>billing</c>, <c>api_disabled</c>, <c>quota</c>, <c>stockout</c>, <c>already_exists</c>, <c>permission</c>, <c>org_policy</c>, <c>network</c>, <c>other</c>).</param>
/// <param name="ErrorCode">The structured Google error code (such as <c>ZONE_RESOURCE_POOL_EXHAUSTED</c>), never the message.</param>
public sealed record CloudAuditEntry(
    DateTimeOffset TimeUtc,
    string? InstallationId,
    string? JobId,
    string Operation,
    string Resource,
    string? RequestId,
    string? OperationId,
    string Outcome,
    string? ErrorCode,
    long DurationMs);

/// <summary>Where the audit entries go. An implementation must never throw into the caller: a lost line is better than a lost run.</summary>
public interface ICloudAuditLog
{
    void Record(CloudAuditEntry entry);
}

public sealed class NullCloudAuditLog : ICloudAuditLog
{
    public static readonly NullCloudAuditLog Instance = new();

    public void Record(CloudAuditEntry entry)
    {
    }
}
