namespace DnaEntropyGraph.Core.Contract;

/// <summary>
/// The app-worker contract DTOs (docs/job_contract.md), transport-agnostic
/// (a bucket object or a local file, per <c>Blobstore</c>). Full schema
/// validation against <c>docs/contract/*.schema.json</c> is out of scope for
/// the solution skeleton; these records exist so Core, Cloud and
/// Presentation can compile against a shared, real shape from day one.
/// </summary>
public sealed record JobManifest(string JobId, string ModelId, IReadOnlyList<string> InputObjectKeys);

/// <summary>
/// One progress step of a run, as DATA (Hard Rule 13: Core builds no user text). <paramref name="Window"/> and <paramref name="Windows"/> are the worker's
/// <c>detail.window</c>/<c>detail.windows</c> within the current input; the presentation layer turns the stage and counts into a resource string.
/// <paramref name="Cache"/> is the worker's <c>detail.cache</c> at restoring-cache and model-loading ("hit", "miss", "saving" or "unavailable"); null when the worker reported none (This PC, the mock predictor, the startup script).
/// </summary>
public sealed record ProgressEvent(string Stage, double FractionComplete, int? Window = null, int? Windows = null, string? Cache = null);

public sealed record WorkerStatus(string Stage, DateTimeOffset HeartbeatUtc);

public sealed record WorkerResult(bool Success, IReadOnlyList<string> OutputObjectKeys, string? ErrorCode);
