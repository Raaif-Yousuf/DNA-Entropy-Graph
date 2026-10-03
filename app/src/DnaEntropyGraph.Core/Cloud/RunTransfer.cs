using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using DnaEntropyGraph.Core.Contract;

namespace DnaEntropyGraph.Core.Cloud;

/// <summary>
/// Everything that moves bytes between the user's disk and the bucket: the input upload, the output download with its
/// path-safety and checksum rules (Hard Rule 14), and the run's output folder.
/// </summary>
internal sealed class RunTransfer(IStorageGateway storage, GatewayCalls calls, CloudRunSettings settings, RunRowStore rows)
{
    /// <summary>
    /// One upload's deadline: <see cref="CloudRunSettings.UploadTimeout"/> when set, else a floor of 2 minutes plus 1 second per 128 KiB
    /// (a link as slow as 1 Mbit/s still finishes). A flat call deadline would cut every large input short.
    /// </summary>
    private TimeSpan UploadTimeoutFor(long bytes)
        => settings.UploadTimeout ?? TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(bytes / (128.0 * 1024.0));

    /// <summary>
    /// Uploads the staged inputs and then <c>manifest.json</c> to <c>jobs/&lt;jobId&gt;/</c>. The manifest goes
    /// last so its presence means every input is already there. Safe to call again for the same job
    /// until a VM exists; the runner never calls it for a run past Uploading.
    /// </summary>
    public async Task UploadAsync(CloudJobRequest request, string bucket, CancellationToken cancellationToken)
    {
        var prefix = WorkerManifestBuilder.JobPrefix(request.JobId);
        for (var index = 0; index < request.Inputs.Count; index++)
        {
            var input = request.Inputs[index];
            FileStream content;
            try
            {
                // Seekable on purpose: the retry pipeline (issue #258) rewinds and replays it. Only the
                // OPEN is guarded; a failure inside the upload is a cloud error, not a missing input.
                content = new FileStream(input.LocalPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new RunFailureException(CloudErrorKind.Other, RunErrorCodes.InputMissing, $"The staged copy of input {index + 1} of {request.Inputs.Count} could not be opened: {ex.GetType().Name}.");
            }

            await using (content.ConfigureAwait(false))
            {
                await calls.CallAsync(
                    async token =>
                    {
                        await storage.UploadAsync(bucket, prefix + input.ObjectName, content, token).ConfigureAwait(false);
                        return true;
                    },
                    cancellationToken,
                    UploadTimeoutFor(content.Length)).ConfigureAwait(false);
            }
        }

        var manifest = WorkerManifestBuilder.Build(
            request.Options,
            request.JobId,
            request.Spec.InstallationId,
            request.Spec.AppVersion,
            request.WorkerImage,
            bucket,
            request.Inputs,
            DateTimeOffset.UtcNow);
        using (var manifestStream = new MemoryStream(Encoding.UTF8.GetBytes(manifest)))
        {
            await calls.CallAsync(
                async token =>
                {
                    await storage.UploadAsync(bucket, prefix + "manifest.json", manifestStream, token).ConfigureAwait(false);
                    return true;
                },
                cancellationToken,
                UploadTimeoutFor(manifestStream.Length)).ConfigureAwait(false);
        }

        // The row keeps where the job lives so the results can be fetched again while the objects exist.
        await rows.UpdateRowAsync(
            request.JobId,
            row => row with { Bucket = bucket, JobPrefix = prefix, ManifestJson = manifest, WorkerImageDigest = request.WorkerImage },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Downloads every file the worker listed for an input that finished, into a fresh folder under
    /// the chosen output folder (Hard Rule 14), verifying each against the checksum <c>result.json</c>
    /// gives. A file lands under its final name only once complete and verified.
    /// </summary>
    public async Task DownloadAsync(CloudJobRequest request, string bucket, WorkerResultDocument result, CancellationToken cancellationToken)
    {
        var runFolder = await ResolveRunFolderAsync(request, cancellationToken).ConfigureAwait(false);
        var runFolderFull = Path.GetFullPath(runFolder);
        var prefix = WorkerManifestBuilder.JobPrefix(request.JobId);

        // Every input that listed files, whatever its status: a failed or cancelled input's partial files are kept
        // (docs/job_contract.md sections 6 and 7). Messages name the input id and file number, never the path, because
        // a path carries the user's file name and these messages are stored (CLAUDE.md "Logs").
        foreach (var input in result.Inputs.Where(i => i.Files.Count > 0))
        {
            for (var n = 0; n < input.Files.Count; n++)
            {
                var file = input.Files[n];
                var label = $"Result file {n + 1} of input {input.Id}";
                var relative = SafeRelativeOutputPath(file.Path, label);
                var destination = Path.GetFullPath(Path.Combine(runFolderFull, relative));
                if (!destination.StartsWith(runFolderFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    throw new RunFailureException(CloudErrorKind.Other, RunErrorCodes.WorkerFailed, $"{label} leaves the output folder.");
                }

                try
                {
                    await DownloadFileAsync(bucket, prefix + file.Path, destination, file, label, cancellationToken).ConfigureAwait(false);
                }
                catch (CloudOperationException ex) when (!VmFacts.IsProjectWide(ex.Kind))
                {
                    // The results are still in the bucket: this is "download again", not "start again".
                    throw new RunFailureException(ex.Kind, RunErrorCodes.DownloadFailed, $"{label} could not be fetched (HTTP status {ex.Error.HttpStatus?.ToString(CultureInfo.InvariantCulture) ?? "none"}).");
                }
            }
        }
    }

    private async Task DownloadFileAsync(string bucket, string objectKey, string destination, WorkerResultFile file, string label, CancellationToken cancellationToken)
    {
        var stream = await calls.CallAsync(token => storage.TryDownloadAsync(bucket, objectKey, token), cancellationToken).ConfigureAwait(false)
                     ?? throw new RunFailureException(CloudErrorKind.Other, RunErrorCodes.WorkerFailed, $"{label} is listed by the worker but is not in the bucket.");

        var partial = destination + ".part";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long written = 0;
            await using (stream.ConfigureAwait(false))
            await using (var target = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await calls.CallAsync(token => stream.ReadAsync(buffer, token).AsTask(), cancellationToken).ConfigureAwait(false)) > 0)
                {
                    hash.AppendData(buffer, 0, read);
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    written += read;
                }
            }

            if (file.Bytes is { } expectedBytes && expectedBytes != written)
            {
                throw new RunFailureException(CloudErrorKind.Other, RunErrorCodes.DownloadCorrupt, $"{label} is {written} bytes, the worker said {expectedBytes}.");
            }

            if (file.Sha256 is { } expectedHash && !string.Equals(Convert.ToHexString(hash.GetHashAndReset()), expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new RunFailureException(CloudErrorKind.Other, RunErrorCodes.DownloadCorrupt, $"{label} does not match the checksum the worker reported.");
            }

            File.Move(partial, destination, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new RunFailureException(CloudErrorKind.Other, RunErrorCodes.DownloadFailed, $"{label} could not be written: {ex.GetType().Name}.");
        }
        finally
        {
            try
            {
                File.Delete(partial);
            }
            catch (Exception)
            {
                // Nothing more to do for a leftover partial; it never carries the final name.
            }
        }
    }

    /// <summary>
    /// A result path must be <c>output/&lt;relative path&gt;</c> with no empty, <c>.</c> or <c>..</c> segment
    /// and no drive or backslash, the same rule the worker's own blobstore enforces; the text comes from
    /// an object in a bucket and is never trusted as a local path.
    /// </summary>
    internal static string SafeRelativeOutputPath(string path, string label)
    {
        const string Root = "output/";
        var invalid = Path.GetInvalidFileNameChars();
        if (!path.StartsWith(Root, StringComparison.Ordinal) || path.Contains('\\', StringComparison.Ordinal) || path.Contains(':', StringComparison.Ordinal))
        {
            throw new RunFailureException(CloudErrorKind.Other, RunErrorCodes.WorkerFailed, $"{label} has a path that is not an output path.");
        }

        var segments = path[Root.Length..].Split('/');
        if (segments.Any(seg => seg.Length == 0 || seg is "." or ".." || seg.IndexOfAny(invalid) >= 0))
        {
            throw new RunFailureException(CloudErrorKind.Other, RunErrorCodes.WorkerFailed, $"{label} has a path that is not a plain relative path.");
        }

        return string.Join(Path.DirectorySeparatorChar, segments);
    }

    /// <summary>
    /// The run's folder: the one already recorded on the row if it still exists (a resumed run), else a fresh one named
    /// after the first input. It is made, and proven writable, before anything is created in the cloud
    /// (<see cref="PrepareRunFolderAsync"/>); the download then finds it recorded and reuses it.
    /// </summary>
    private async Task<string> ResolveRunFolderAsync(CloudJobRequest request, CancellationToken cancellationToken)
    {
        var existing = (await rows.LatestRecordAsync(request.JobId, cancellationToken).ConfigureAwait(false))?.OutputDir;
        if (!string.IsNullOrWhiteSpace(existing) && Directory.Exists(existing))
        {
            return existing;
        }

        string folder;
        try
        {
            folder = RunOutputFolders.CreateUnique(request.OutputFolder, Path.GetFileNameWithoutExtension(request.Inputs[0].FileName));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new RunFailureException(CloudErrorKind.Other, RunErrorCodes.OutputFolderUnusable, $"The output folder could not be created: {ex.GetType().Name}.");
        }

        await rows.UpdateRowAsync(request.JobId, row => row with { OutputDir = folder }, cancellationToken).ConfigureAwait(false);
        return folder;
    }

    /// <summary>Creates the run folder and writes then deletes a probe file in it, so an unwritable folder fails here, before a VM is billed.</summary>
    public async Task PrepareRunFolderAsync(CloudJobRequest request, CancellationToken cancellationToken)
    {
        var folder = await ResolveRunFolderAsync(request, cancellationToken).ConfigureAwait(false);
        var probe = Path.Combine(folder, ".deg-write-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            await File.WriteAllBytesAsync(probe, [0], cancellationToken).ConfigureAwait(false);
            File.Delete(probe);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new RunFailureException(CloudErrorKind.Other, RunErrorCodes.OutputFolderUnusable, $"The output folder is not writable: {ex.GetType().Name}.");
        }
    }

    /// <summary>
    /// A run that ends Failed with nothing downloaded leaves no empty folder in the user's output folder. Only an EMPTY
    /// folder is ever removed (never a file, Hard Rule 14), and the row stops pointing at it.
    /// </summary>
    public async Task RemoveEmptyRunFolderAsync(string jobId)
    {
        try
        {
            var folder = (await rows.LatestRecordAsync(jobId, CancellationToken.None).ConfigureAwait(false))?.OutputDir;
            if (!string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any())
            {
                Directory.Delete(folder, recursive: false);
                await rows.UpdateRowAsync(jobId, row => row with { OutputDir = null }, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            // Clutter, not a correctness problem: the run's own failure is what gets recorded.
        }
    }
}
