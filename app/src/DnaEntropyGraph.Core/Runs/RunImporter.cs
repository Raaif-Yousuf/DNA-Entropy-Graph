using System.Globalization;
using System.Text.Json;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Cloud;
using DnaEntropyGraph.Core.Contract;

namespace DnaEntropyGraph.Core.Runs;

/// <summary>What one "Find runs from other computers" scan did. Skipped counts job folders that are not a finished, readable run (no result yet, unreadable manifest).</summary>
public sealed record RunImportResult(int Imported, int AlreadyKnown, int Skipped);

/// <summary>Issue #111: finds finished runs in the project's results buckets that this computer's history does not know.</summary>
public interface IRunImporter
{
    Task<RunImportResult> ImportAsync(CloudScope scope, CancellationToken cancellationToken);
}

/// <summary>
/// Reads <c>jobs/&lt;job-id&gt;/manifest.json</c> and <c>result.json</c> from every results bucket of ours in the project and creates one
/// history row per run this computer has not seen, marked <see cref="RunRecord.Imported"/>. Runs from OTHER installations are exactly what this
/// is for (Hard Rule 9 is about never sharing a resource; reading is not sharing), so the manifest's installation id is kept on the row to say
/// where the run came from. Read-only to the cloud: it only lists and downloads. A job id already in the local history is never touched.
/// </summary>
public sealed class RunImporter(IStorageGateway storage, IRunRepository runs, ImportedRunDismissals? dismissals = null) : IRunImporter
{
    /// <summary>Issue #111: the most bytes read from a foreign manifest.json or result.json; anything larger is skipped, never stored.</summary>
    public const int MaxObjectBytes = 1024 * 1024;

    public async Task<RunImportResult> ImportAsync(CloudScope scope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(scope.ProjectId);
        var known = (await runs.GetAllAsync(cancellationToken).ConfigureAwait(false)).Select(r => r.JobId).ToHashSet(StringComparer.Ordinal);
        var dismissed = dismissals?.Load() ?? new HashSet<string>();
        int imported = 0, alreadyKnown = 0, skipped = 0;

        foreach (var bucket in await storage.ListBucketsAsync(scope, cancellationToken).ConfigureAwait(false))
        {
            foreach (var prefix in await storage.ListChildPrefixesAsync(scope, bucket.Name, ResultsBucket.JobsPrefix, cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var jobId = prefix[ResultsBucket.JobsPrefix.Length..].TrimEnd('/');
                if (known.Contains(jobId))
                {
                    alreadyKnown++;
                    continue;
                }

                if (dismissed.Contains(jobId))
                {
                    skipped++;
                    continue;
                }

                var row = await TryBuildRowAsync(scope, bucket.Name, jobId, cancellationToken).ConfigureAwait(false);
                if (row is null)
                {
                    skipped++;
                    continue;
                }

                await runs.UpsertAsync(row, cancellationToken).ConfigureAwait(false);
                known.Add(jobId);
                imported++;
            }
        }

        return new RunImportResult(imported, alreadyKnown, skipped);
    }

    private async Task<RunRecord?> TryBuildRowAsync(CloudScope scope, string bucket, string jobId, CancellationToken cancellationToken)
    {
        var prefix = WorkerManifestBuilder.JobPrefix(jobId);
        var manifestJson = await ReadTextAsync(scope, bucket, prefix + "manifest.json", cancellationToken).ConfigureAwait(false);
        var resultJson = manifestJson is null ? null : await ReadTextAsync(scope, bucket, prefix + "result.json", cancellationToken).ConfigureAwait(false);
        if (manifestJson is null || resultJson is null)
        {
            // No manifest: not one of our job folders. No result: still running or never finished, nothing to show or download yet.
            return null;
        }

        try
        {
            using var manifest = JsonDocument.Parse(manifestJson);
            var root = manifest.RootElement;
            if (!string.Equals(Str(root, "jobId"), jobId, StringComparison.Ordinal)
                || !DateTimeOffset.TryParse(Str(root, "createdAt"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var created))
            {
                return null;
            }

            var result = WorkerResultReader.Parse(resultJson);
            var allDone = result.Inputs.Count > 0 && result.Inputs.All(i => i.Status == "done" && i.Files.Count > 0);
            var phase = result.Status switch
            {
                "done" => allDone ? JobPhase.Completed : JobPhase.PartiallyCompleted,
                "cancelled" => JobPhase.Cancelled,
                _ => JobPhase.Failed,
            };

            var createdBy = root.TryGetProperty("createdBy", out var by) && by.ValueKind == JsonValueKind.Object ? by : default;
            var worker = root.TryGetProperty("worker", out var w) && w.ValueKind == JsonValueKind.Object ? w : default;
            return new RunRecord(
                jobId,
                phase,
                created,
                Name: FirstInputName(root) ?? jobId,
                ErrorCode: phase == JobPhase.Failed ? result.ErrorCode ?? RunErrorCodes.WorkerFailed : null,
                ManifestJson: manifestJson,
                ProjectId: scope.ProjectId,
                AccountSub: scope.AccountSub,
                Bucket: bucket,
                JobPrefix: prefix,
                AppVersion: Str(createdBy, "appVersion"),
                WorkerVersion: Str(worker, "version"),
                WorkerImageDigest: Str(worker, "image"),
                InstallationId: Str(createdBy, "installationId"),
                Imported: true);
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or CloudOperationException))
        {
            // Foreign content: a result this app does not understand (InvalidDataException, schema 2, bad JSON) skips this folder only.
            return null;
        }
    }

    private async Task<string?> ReadTextAsync(CloudScope scope, string bucket, string key, CancellationToken cancellationToken)
    {
        await using var stream = await storage.TryDownloadAsync(scope, bucket, key, cancellationToken).ConfigureAwait(false);
        if (stream is null)
        {
            return null;
        }

        // Read one byte past the cap: that byte is how an oversized object is told from one that is exactly at the cap.
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk.AsMemory(0, (int)Math.Min(chunk.Length, MaxObjectBytes + 1L - buffer.Length)), cancellationToken).ConfigureAwait(false)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > MaxObjectBytes)
            {
                return null;
            }
        }

        return System.Text.Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    private static string? FirstInputName(JsonElement root)
    {
        if (!root.TryGetProperty("inputs", out var inputs) || inputs.ValueKind != JsonValueKind.Array || inputs.GetArrayLength() == 0)
        {
            return null;
        }

        var first = inputs[0];
        return Str(first, "name") ?? Str(first, "accession");
    }

    private static string? Str(JsonElement owner, string name)
        => owner.ValueKind == JsonValueKind.Object && owner.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text ? text : null;
}
