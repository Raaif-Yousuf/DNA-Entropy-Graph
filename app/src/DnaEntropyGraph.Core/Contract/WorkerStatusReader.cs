using System.Text.Json;

namespace DnaEntropyGraph.Core.Contract;

/// <summary>
/// The parts of <c>status.json</c> the app reads to judge the worker's health (docs/job_contract.md sections 4 and 5).
/// <see cref="WorkerVersion"/> is empty in the snapshots <c>worker/vm/startup.sh</c> writes for its own infra stages
/// (booting, installing) and filled by the container's worker, which is how the two are told apart.
/// </summary>
public sealed record WorkerStatusSnapshot(string? Stage, long? HeartbeatSeq, string? UpdatedAt, string? WorkerVersion, string? StageStartedAt = null, string? StartedAt = null, bool CancelSeen = false, int? WindowsDone = null, string? LastProgressAt = null, double? Percent = null, int? Window = null, int? Windows = null, string? Cache = null)
{
    /// <summary>True once the container's worker (not only the startup script) has written the file: its heartbeat is running.</summary>
    public bool WorkerOwned => !string.IsNullOrEmpty(WorkerVersion);
}

/// <summary>What the worker's first progress line says about the GPU.</summary>
public enum WorkerGpuReport
{
    /// <summary>The worker's "worker starting" line is not (yet) readable.</summary>
    Unknown,

    /// <summary>A GPU was named.</summary>
    Present,

    /// <summary>The line says "no GPU detected".</summary>
    Absent,
}

/// <summary>
/// Lenient readers of the worker's live files. A diagnostic read of a file another machine is rewriting: a half-readable
/// or foreign file is "no information", never an exception and never a reason to fail a run by itself.
/// </summary>
public static class WorkerStatusReader
{
    private const string StartingPrefix = "worker starting; GPU: ";
    private const string NoGpuText = "no GPU detected";

    /// <summary>The snapshot in <paramref name="json"/>, or null when it is not a JSON object.</summary>
    public static WorkerStatusSnapshot? TryParseStatus(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            return new WorkerStatusSnapshot(
                ReadString(root, "stage"),
                root.TryGetProperty("heartbeatSeq", out var seq) && seq.ValueKind == JsonValueKind.Number && seq.TryGetInt64(out var value) ? value : null,
                ReadString(root, "updatedAt"),
                root.TryGetProperty("worker", out var worker) && worker.ValueKind == JsonValueKind.Object ? ReadString(worker, "version") : null,
                ReadString(root, "stageStartedAt"),
                ReadString(root, "startedAt"),
                root.TryGetProperty("cancelSeen", out var seen) && seen.ValueKind == JsonValueKind.True,
                root.TryGetProperty("windowsDone", out var windows) && windows.ValueKind == JsonValueKind.Number && windows.TryGetInt32(out var done) ? done : null,
                ReadString(root, "lastProgressAt"),
                root.TryGetProperty("percent", out var percent) && percent.ValueKind == JsonValueKind.Number && percent.TryGetDouble(out var pct) && double.IsFinite(pct) ? pct : null,
                ReadDetailInt(root, "window"),
                ReadDetailInt(root, "windows"),
                ReadDetailCache(root));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// What the worker's first progress line (<c>"worker starting; GPU: ..."</c>, docs/job_contract.md section 6) says about the
    /// GPU, read from the text of <c>progress.jsonl</c>. Unknown until that line is there.
    /// </summary>
    public static WorkerGpuReport ReadGpuReport(string progressJsonl)
    {
        foreach (var line in progressJsonl.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string? message;
            try
            {
                using var document = JsonDocument.Parse(line);
                message = document.RootElement.ValueKind == JsonValueKind.Object ? ReadString(document.RootElement, "message") : null;
            }
            catch (JsonException)
            {
                continue;
            }

            if (message is not null && message.StartsWith(StartingPrefix, StringComparison.Ordinal))
            {
                var gpu = message[StartingPrefix.Length..];
                return gpu.StartsWith(NoGpuText, StringComparison.Ordinal) ? WorkerGpuReport.Absent : WorkerGpuReport.Present;
            }
        }

        return WorkerGpuReport.Unknown;
    }

    private static int? ReadDetailInt(JsonElement root, string name)
        => root.TryGetProperty("detail", out var detail) && detail.ValueKind == JsonValueKind.Object
            && detail.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number
            : null;

    // The worker's own words for the model cache (docs/job_contract.md section 5); anything else is treated as not reported.
    private static string? ReadDetailCache(JsonElement root)
    {
        if (root.TryGetProperty("detail", out var detail) && detail.ValueKind == JsonValueKind.Object
            && detail.TryGetProperty("cache", out var value) && value.ValueKind == JsonValueKind.String)
        {
            var cache = value.GetString();
            return cache is "hit" or "miss" or "unavailable" or "saving" ? cache : null;
        }

        return null;
    }

    private static string? ReadString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
