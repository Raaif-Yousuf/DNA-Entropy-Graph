namespace DnaEntropyGraph.Core.Contract;

/// <summary>
/// The app-worker contract DTOs (docs/job_contract.md), transport-agnostic
/// (a bucket object or a local file, per <c>Blobstore</c>). Full schema
/// validation against <c>docs/contract/*.schema.json</c> is out of scope for
/// the solution skeleton; these records exist so Core, Cloud and
/// Presentation can compile against a shared, real shape from day one.
/// </summary>
public sealed record JobManifest(string JobId, string ModelId, IReadOnlyList<string> InputObjectKeys);

public sealed record ProgressEvent(string Stage, double FractionComplete, string Message);

public sealed record WorkerStatus(string Stage, DateTimeOffset HeartbeatUtc);

public sealed record WorkerResult(bool Success, IReadOnlyList<string> OutputObjectKeys, string? ErrorCode);
