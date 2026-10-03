using System.Text.Json;

namespace DnaEntropyGraph.Core.Contract;

/// <summary>
/// The parts of <c>status.json</c> the app reads to judge the worker's health (docs/job_contract.md sections 4 and 5).
/// <see cref="WorkerVersion"/> is empty in the snapshots <c>worker/vm/startup.sh</c> writes for its own infra stages
/// (booting, installing) and filled by the container's worker, which is how the two are told apart.
/// </summary>
public sealed record WorkerStatusSnapshot(string? Stage, long? HeartbeatSeq, string? UpdatedAt, string? WorkerVersion)
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
                root.TryGetProperty("worker", out var worker) && worker.ValueKind == JsonValueKind.Object ? ReadString(worker, "version") : null);
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

    private static string? ReadString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
