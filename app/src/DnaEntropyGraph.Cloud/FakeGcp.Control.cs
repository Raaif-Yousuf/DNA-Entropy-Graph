using System.Collections.Concurrent;
using System.Text;
using DnaEntropyGraph.Core.Cloud;

namespace DnaEntropyGraph.Cloud;

// Issue #675: control/cancel on the fake. A live (heartbeating) worker that sees the object writes its cancelled stage and a cancelled
// result.json, like the real worker between windows; a worker with no heartbeat thread of its own (static status.json) never answers;
// WithWorkerIgnoringCancel keeps heartbeating but never finishes. WithUploadFailure scripts a write that fails.
public sealed partial class FakeGcp
{
    private const string CancelSuffix = "control/cancel";

    private readonly ConcurrentDictionary<string, CloudErrorKind> _uploadFailures = new(StringComparer.Ordinal);
    private bool _workerIgnoresCancel;

    /// <summary>An upload whose object key ends with <paramref name="keySuffix"/> fails with <paramref name="kind"/> and stores nothing.</summary>
    public FakeGcp WithUploadFailure(string keySuffix, CloudErrorKind kind)
    {
        _uploadFailures[keySuffix] = kind;
        return this;
    }

    /// <summary>A live worker keeps heartbeating after <c>control/cancel</c> but never reaches a terminal stage (it saw the request and is slow to stop).</summary>
    public FakeGcp WithWorkerIgnoringCancel()
    {
        _workerIgnoresCancel = true;
        return this;
    }

    private void ThrowIfUploadScripted(string objectKey)
    {
        foreach (var (suffix, kind) in _uploadFailures)
        {
            if (objectKey.EndsWith(suffix, StringComparison.Ordinal))
            {
                throw Build(kind, "SCRIPTED_UPLOAD_FAILURE", kind == CloudErrorKind.Permission ? 403 : null, $"The upload of '{objectKey}' was scripted to fail ({kind}).");
            }
        }
    }

    private void OnObjectStored(string bucket, string objectKey)
    {
        if (!objectKey.EndsWith(CancelSuffix, StringComparison.Ordinal) || _workerIgnoresCancel)
        {
            return;
        }

        var prefix = objectKey[..^CancelSuffix.Length];
        var statusKey = (bucket, prefix + "status.json");
        if (!_liveWorkers.TryRemove(statusKey, out var live))
        {
            return; // no worker with a heartbeat of its own to see the request
        }

        _objects[statusKey] = Encoding.UTF8.GetBytes(WorkerStatusJson(live.JobId, live.Seq + 1, "cancelled"));
        _objects[(bucket, prefix + "result.json")] = Encoding.UTF8.GetBytes($"{{\"schema\":1,\"jobId\":\"{live.JobId}\",\"status\":\"cancelled\",\"inputs\":[]}}");
    }
}
