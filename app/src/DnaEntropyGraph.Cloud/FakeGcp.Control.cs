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
    private bool _workerNeverAcknowledges;
    private bool _workerGpuLineMissing;

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

    /// <summary>A live worker keeps heartbeating and never says it saw <c>control/cancel</c> (no <c>cancelSeen</c>, no terminal stage): what a hung main thread under a free-running heartbeat looks like, or a worker built before the acknowledgement existed.</summary>
    public FakeGcp WithWorkerNeverAcknowledgingCancel()
    {
        _workerNeverAcknowledges = true;
        return this;
    }

    /// <summary>The simulated worker's first progress line is a warm-up line, not "worker starting; GPU: ...": the GPU is neither confirmed nor denied until a test writes the line itself.</summary>
    public FakeGcp WithWorkerGpuLineMissing()
    {
        _workerGpuLineMissing = true;
        return this;
    }

    private static string WorkerWarmupProgressLine()
        => "{\"seq\":1,\"stage\":\"" + WorkerStages.RestoringCache + "\",\"level\":\"notice\",\"message\":\"restoring the model cache\"}\n";

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
        if (!objectKey.EndsWith(CancelSuffix, StringComparison.Ordinal) || _workerNeverAcknowledges)
        {
            return;
        }

        var prefix = objectKey[..^CancelSuffix.Length];
        var statusKey = (bucket, prefix + "status.json");
        if (_workerIgnoresCancel)
        {
            // It saw the request (cancelSeen) and is slow to stop: it keeps heartbeating and never reaches a terminal stage.
            if (_liveWorkers.TryGetValue(statusKey, out var slow))
            {
                _liveWorkers[statusKey] = slow with { CancelSeen = true };
            }

            return;
        }

        if (!_liveWorkers.TryRemove(statusKey, out var live))
        {
            return; // no worker with a heartbeat of its own to see the request
        }

        _objects[statusKey] = Encoding.UTF8.GetBytes(WorkerStatusJson(live.JobId, live.Seq + 1, WorkerStages.Cancelled, cancelSeen: true));
        _objects[(bucket, prefix + "result.json")] = Encoding.UTF8.GetBytes($"{{\"schema\":1,\"jobId\":\"{live.JobId}\",\"status\":\"cancelled\",\"inputs\":[]}}");
    }
}
