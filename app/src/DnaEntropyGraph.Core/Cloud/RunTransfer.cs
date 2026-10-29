using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using DnaEntropyGraph.Core.Contract;
using DnaEntropyGraph.Core.Runs;

namespace DnaEntropyGraph.Core.Cloud;

/// <summary>
/// Everything that moves bytes between the user's disk and the bucket: the input upload, the output download with its
/// path-safety and checksum rules (Hard Rule 14), and the run's output folder.
/// </summary>
/// <summary>How many changed files a download renamed aside so far; it lives outside the download so the count survives a failure partway through (Hard Rule 14: the user must still hear about each one).</summary>
internal sealed class DownloadTally
{
    public int KeptAside { get; set; }
}

internal sealed class RunTransfer(IStorageGateway storage, GatewayCalls calls, CloudRunSettings settings, RunRowStore rows, IRunFileMover? fileMover = null)
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
            if (input.IsAccession)
            {
                // Issue #684: the worker fetches an accession itself; there is no local file to upload, the manifest names it.
                continue;
            }

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
    public async Task<int> DownloadAsync(CloudJobRequest request, string bucket, WorkerResultDocument result, CancellationToken cancellationToken, DownloadTally? tally = null)
    {
        var runFolder = await ResolveRunFolderAsync(request, cancellationToken).ConfigureAwait(false);
        var runFolderFull = Path.GetFullPath(runFolder);
        var prefix = WorkerManifestBuilder.JobPrefix(request.JobId);
        StalePartSweeper.Sweep(runFolderFull, result.Inputs.SelectMany(i => i.Files).Select(f => ResultSubDirectory(f.Path)));
        tally ??= new DownloadTally();

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

                // Hard Rule 14: a junction or symlink below the run folder would send the temp file, the rename and the final move somewhere else.
                EnsureNoLinkedFolder(runFolderFull, destination, label);

                try
                {
                    await DownloadFileAsync(runFolderFull, bucket, prefix + file.Path, destination, file, label, tally, cancellationToken).ConfigureAwait(false);
                }
                catch (CloudOperationException ex) when (!VmFacts.IsProjectWide(ex.Kind))
                {
                    // The results are still in the bucket: this is "download again", not "start again".
                    throw new RunFailureException(ex.Kind, RunErrorCodes.DownloadFailed, $"{label} could not be fetched (HTTP status {ex.Error.HttpStatus?.ToString(CultureInfo.InvariantCulture) ?? "none"}).");
                }
            }
        }

        return tally.KeptAside;
    }

    /// <summary>The folder (relative to the run folder) a result file lands in; a path that is not a safe output path names none here, and fails with its own label when it is downloaded.</summary>
    internal static string ResultSubDirectory(string path)
    {
        try
        {
            return Path.GetDirectoryName(SafeRelativeOutputPath(path, "A result file")) ?? string.Empty;
        }
        catch (RunFailureException)
        {
            return string.Empty;
        }
    }

    /// <summary>Throws when any folder from the run folder down to the file's folder is a junction or symlink. Called again just before the temp file is created, after it is streamed and before the final move, because a link can appear in between.</summary>
    private static void EnsureNoLinkedFolder(string runFolderFull, string destination, string label)
    {
        if (!RunOutputRoot.NoReparseBetween(runFolderFull, Path.GetDirectoryName(destination)!))
        {
            throw new RunFailureException(CloudErrorKind.Other, RunErrorCodes.DownloadFailed, $"{label} would be written through a linked folder, so it was not downloaded.");
        }
    }

    private async Task DownloadFileAsync(string runFolderFull, string bucket, string objectKey, string destination, WorkerResultFile file, string label, DownloadTally tally, CancellationToken cancellationToken)
    {
        var partial = StreamedDownload.TempPathFor(destination);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            EnsureNoLinkedFolder(runFolderFull, destination, label);

            // Issue #588: the gateway streams the object straight into a unique .part file (memory stays flat for a result of any size) under a stall deadline, then the file is verified below.
            var found = await StreamedDownload.RunAsync(storage, settings.TimeProvider, bucket, objectKey, partial, file.Bytes, settings.DownloadStallTimeout, cancellationToken).ConfigureAwait(false);
            if (!found)
            {
                throw new RunFailureException(CloudErrorKind.Other, RunErrorCodes.WorkerFailed, $"{label} is listed by the worker but is not in the bucket.");
            }
            EnsureNoLinkedFolder(runFolderFull, destination, label);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long written = 0;
            await using (var downloaded = new FileStream(partial, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await downloaded.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    hash.AppendData(buffer, 0, read);
                    written += read;
                }
            }

            if (file.Bytes is { } expectedBytes && expectedBytes != written)
            {
                throw new RunFailureException(CloudErrorKind.Other, RunErrorCodes.DownloadCorrupt, $"{label} is {written} bytes, the worker said {expectedBytes}.");
            }

            var actualHash = Convert.ToHexString(hash.GetHashAndReset());
            if (file.Sha256 is { } expectedHash && !string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new RunFailureException(CloudErrorKind.Other, RunErrorCodes.DownloadCorrupt, $"{label} does not match the checksum the worker reported.");
            }

            // Hard Rule 14: a resumed run finds its earlier files here, and the user may have edited one. Identical bytes are kept as they are; anything else is
            // renamed aside ("name (changed time).ext") before the verified download takes its place, never overwritten.
            EnsureNoLinkedFolder(runFolderFull, destination, label);
            string? aside = null;
            if (File.Exists(destination))
            {
                if (new FileInfo(destination).Length == written && await FileSha256Async(destination, cancellationToken).ConfigureAwait(false) == actualHash)
                {
                    return;
                }

                aside = ChangedFileAside.Move(destination, settings.TimeProvider, Mover);
                tally.KeptAside++;
            }

            try
            {
                Mover.Move(partial, destination);
            }
            catch (Exception) when (aside is not null)
            {
                // The user's file must never be left renamed with nothing in its place: put it back, and only then report the failure.
                try
                {
                    Mover.Move(aside, destination);
                    tally.KeptAside--;
                }
                catch (Exception restoreFailure) when (restoreFailure is IOException or UnauthorizedAccessException)
                {
                    // Could not put it back; it is still safe under its changed name.
                }

                throw;
            }
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

    private static readonly DiskRunFileMover DiskMover = new();

    private IRunFileMover Mover => fileMover ?? DiskMover;

    private static async Task<string> FileSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
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
        // OutputDir comes from the history database: a resumed run only reuses it when it still lies inside the output folder this run was
        // given and is a real folder with no junction or symlink below the output folder (Hard Rule 14). Otherwise it falls through to a fresh
        // folder under the chosen output folder, so nothing is written elsewhere.
        if (!string.IsNullOrWhiteSpace(existing) && Directory.Exists(existing) && IsInsideOutputFolder(existing, request.OutputFolder))
        {
            return existing;
        }

        string folder;
        try
        {
            folder = RunOutputFolders.CreateUnique(
                request.OutputFolder,
                RunOutputFolders.RunFolderName(request.Options, request.Inputs[0], settings.TimeProvider.GetLocalNow().DateTime));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new RunFailureException(CloudErrorKind.Other, RunErrorCodes.OutputFolderUnusable, $"The output folder could not be created: {ex.GetType().Name}.");
        }

        await rows.UpdateRowAsync(request.JobId, row => row with { OutputDir = folder }, cancellationToken).ConfigureAwait(false);
        return folder;
    }

    /// <summary>
    /// Whether a recorded run folder may be reused: inside the output folder and a real folder with no link below it (a whole-drive output folder is fine, reuse only writes the
    /// run's own files). A check that fails on the disk or the path means "not reusable", never an exception out of resume. <paramref name="containment"/> is a test seam.
    /// </summary>
    internal static bool IsInsideOutputFolder(string folder, string outputFolder, Func<string, string, bool>? containment = null)
    {
        try
        {
            return (containment ?? RunOutputRoot.IsUnderRootWithoutLinks)(folder, outputFolder);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
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
