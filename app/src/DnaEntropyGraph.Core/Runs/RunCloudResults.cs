using System.Security.Cryptography;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Cloud;
using DnaEntropyGraph.Core.Contract;

namespace DnaEntropyGraph.Core.Runs;

public enum CloudResultsStatus
{
    Done,

    /// <summary>The run has no cloud copy: none recorded, or it was already deleted.</summary>
    NoCloudCopy,

    /// <summary>The retention window has passed (RETENTION_EXPIRED).</summary>
    Expired,

    /// <summary>The bucket holds no <c>result.json</c> for this job.</summary>
    ResultNotFound,

    /// <summary>The request named a folder or prefix that is not this run's own.</summary>
    Refused,

    /// <summary>The job did not finish (failed or cancelled): the files it did upload were restored, and may be incomplete.</summary>
    Partial,

    /// <summary>No real cloud connection exists to delete through yet.</summary>
    NotConnected,

    Failed,
}

/// <summary>
/// What a re-download did. <see cref="ChangedKeptAside"/> counts local files that no longer matched the bucket copy and were
/// renamed (never overwritten) before the original was fetched. A bare <see cref="CloudResultsStatus"/> converts to a result with none.
/// </summary>
public sealed record RedownloadResult(CloudResultsStatus Status, int ChangedKeptAside = 0)
{
    public static implicit operator RedownloadResult(CloudResultsStatus status) => new(status);
}

/// <summary>Thrown by an <see cref="IJobObjectDeleter"/> that has no real cloud behind it.</summary>
public sealed class CloudNotConnectedException : Exception
{
    public CloudNotConnectedException()
        : base("Cloud deletion is not connected.")
    {
    }
}

/// <summary>
/// Deletes every object under one job's prefix in its bucket. Implemented in DnaEntropyGraph.Cloud
/// (Hard Rule 7); the cloud lane owns it (docs/ToTest.md and the area:cloud issue filed with #101).
/// </summary>
public interface IJobObjectDeleter
{
    /// <summary>False while nothing real is behind this deleter, so the UI offers no deletion it cannot perform.</summary>
    bool IsAvailable { get; }

    Task DeleteJobObjectsAsync(string bucket, string jobPrefix, CancellationToken cancellationToken);
}

/// <summary>A run's copy in the bucket (issue #101): fetch it again, or delete it.</summary>
public interface IRunCloudResults
{
    /// <summary>True when the bucket copy is believed to exist: a recorded location, not deleted, not past retention.</summary>
    bool IsAvailable(RunRecord run);

    /// <summary>True when <see cref="DeleteAsync"/> can really delete this run's cloud copy.</summary>
    bool CanDelete(RunRecord run);

    /// <summary>
    /// Restores the run's output files from the bucket into its output folder (recreated if it was deleted).
    /// A file already in the folder that matches the size and SHA-256 recorded in <c>result.json</c> is left exactly as it is.
    /// One that no longer matches (cut off, damaged or edited by the user) is renamed to <c>name (changed yyyy-MM-dd HHmmss).ext</c>
    /// in the same folder and the original is fetched; nothing the user may have changed is ever overwritten (Hard Rule 14).
    /// </summary>
    Task<RedownloadResult> RedownloadAsync(RunRecord run, CancellationToken cancellationToken);

    /// <summary>Deletes the objects under this run's job-id prefix and records that the cloud copy is gone. Never anything outside that prefix (Hard Rule 11).</summary>
    Task<CloudResultsStatus> DeleteAsync(RunRecord run, CancellationToken cancellationToken);
}

/// <summary>Moves one file without ever overwriting. A seam so a test can make a move fail where only a real disk race would.</summary>
public interface IRunFileMover
{
    /// <summary>Moves <paramref name="source"/> to <paramref name="destination"/>; throws <see cref="IOException"/> if the destination exists.</summary>
    void Move(string source, string destination);
}

public sealed class DiskRunFileMover : IRunFileMover
{
    public void Move(string source, string destination) => File.Move(source, destination, overwrite: false);
}

public sealed class RunCloudResults : IRunCloudResults
{
    private readonly IStorageGateway _storage;
    private readonly IJobObjectDeleter _deleter;
    private readonly IRunRepository _runs;
    private readonly Func<string> _defaultOutputParent;
    private readonly TimeProvider _time;
    private readonly IRunFileMover _mover;

    public RunCloudResults(IStorageGateway storage, IJobObjectDeleter deleter, IRunRepository runs, Func<string> defaultOutputParent, TimeProvider time, IRunFileMover? mover = null)
    {
        _mover = mover ?? new DiskRunFileMover();
        _storage = storage;
        _deleter = deleter;
        _runs = runs;
        _defaultOutputParent = defaultOutputParent;
        _time = time;
    }

    public bool IsAvailable(RunRecord run)
        => Classify(run) is null;

    public bool CanDelete(RunRecord run)
        => _deleter.IsAvailable && !run.CloudResultsDeleted && !string.IsNullOrWhiteSpace(run.Bucket) && !string.IsNullOrWhiteSpace(run.JobPrefix);

    public async Task<RedownloadResult> RedownloadAsync(RunRecord run, CancellationToken cancellationToken)
    {
        if (Classify(run) is { } unavailable)
        {
            return unavailable;
        }

        if (run.JobPrefix != WorkerManifestBuilder.JobPrefix(run.JobId))
        {
            return CloudResultsStatus.Refused;
        }

        var tally = new KeptAsideTally();
        try
        {
            await using var resultStream = await _storage.TryDownloadAsync(run.Bucket!, run.JobPrefix + "result.json", cancellationToken).ConfigureAwait(false);
            if (resultStream is null)
            {
                return CloudResultsStatus.ResultNotFound;
            }

            WorkerResultDocument result;
            using (var reader = new StreamReader(resultStream))
            {
                result = WorkerResultReader.Parse(await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false));
            }

            var folder = await ResolveFolderAsync(run, cancellationToken).ConfigureAwait(false);
            if (folder is null)
            {
                return CloudResultsStatus.Refused;
            }

            foreach (var file in result.Inputs.SelectMany(i => i.Files))
            {
                await DownloadFileAsync(run, folder, file, tally, cancellationToken).ConfigureAwait(false);
            }

            // Same rule as RunOutcomeRecorder: every input finished with files, else the history says "partly completed".
            var allInputsDone = result.Inputs.All(i => i.Status == "done" && i.Files.Count > 0);
            return new(string.Equals(result.Status, "done", StringComparison.Ordinal) && allInputsDone ? CloudResultsStatus.Done : CloudResultsStatus.Partial, tally.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Network, storage, disk or a malformed result: every one is "download failed, try again", never a crash.
            // The files already moved aside stay reported: the user must still be told where their changed copies went.
            return new(CloudResultsStatus.Failed, tally.Count);
        }
    }

    public async Task<CloudResultsStatus> DeleteAsync(RunRecord run, CancellationToken cancellationToken)
    {
        if (run.CloudResultsDeleted || string.IsNullOrWhiteSpace(run.Bucket) || string.IsNullOrWhiteSpace(run.JobPrefix))
        {
            return CloudResultsStatus.NoCloudCopy;
        }

        // Hard Rule 11: only ever this job's own prefix, derived from its id, never a stored string taken on trust.
        if (!string.Equals(run.JobPrefix, WorkerManifestBuilder.JobPrefix(run.JobId), StringComparison.Ordinal) || string.IsNullOrWhiteSpace(run.JobId))
        {
            return CloudResultsStatus.Refused;
        }

        try
        {
            await _deleter.DeleteJobObjectsAsync(run.Bucket, run.JobPrefix, cancellationToken).ConfigureAwait(false);
        }
        catch (CloudNotConnectedException)
        {
            return CloudResultsStatus.NotConnected;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return CloudResultsStatus.Failed;
        }

        await _runs.UpsertAsync(run with { CloudResultsDeleted = true }, cancellationToken).ConfigureAwait(false);
        return CloudResultsStatus.Done;
    }

    private CloudResultsStatus? Classify(RunRecord run)
    {
        if (run.CloudResultsDeleted || string.IsNullOrWhiteSpace(run.Bucket) || string.IsNullOrWhiteSpace(run.JobPrefix))
        {
            return CloudResultsStatus.NoCloudCopy;
        }

        return run.CloudResultsExpireAt is { } expires && expires <= _time.GetUtcNow() ? CloudResultsStatus.Expired : null;
    }

    private async Task<string?> ResolveFolderAsync(RunRecord run, CancellationToken cancellationToken)
    {
        var root = RunOutputRoot.Resolve(run, _defaultOutputParent);
        if (string.IsNullOrWhiteSpace(run.OutputDir))
        {
            var created = RunOutputFolders.CreateUnique(root, string.IsNullOrWhiteSpace(run.Name) ? run.JobId : run.Name);
            await _runs.UpsertAsync(run with { OutputDir = created }, cancellationToken).ConfigureAwait(false);
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(created));
        }

        var folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(run.OutputDir));
        return RunOutputRoot.IsStrictlyInside(folder, root) ? folder : null;
    }

    /// <summary>How many changed local files have been renamed aside so far in one re-download; survives a failure partway through.</summary>
    private sealed class KeptAsideTally
    {
        public int Count { get; set; }
    }

    private const int MaxAsideTries = 50;

    private string AsideCandidate(string path, int attempt)
    {
        var stamp = _time.GetLocalNow().ToString("yyyy-MM-dd HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        var stem = Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path) + " (changed " + stamp);
        return (attempt == 1 ? stem + ")" : $"{stem} {attempt})") + Path.GetExtension(path);
    }

    /// <summary>
    /// Renames <paramref name="path"/> to a free <c>name (changed time).ext</c> and returns that name. A name that turns out taken
    /// between the check and the move (another program, another re-download) is not an error: the next counter is tried, a bounded number of times.
    /// </summary>
    private string MoveAside(string path)
    {
        for (var attempt = 1; attempt <= MaxAsideTries; attempt++)
        {
            var candidate = AsideCandidate(path, attempt);
            if (File.Exists(candidate))
            {
                continue;
            }

            try
            {
                _mover.Move(path, candidate);
                return candidate;
            }
            catch (IOException) when (attempt < MaxAsideTries)
            {
            }
        }

        throw new IOException("No free name was found to keep the changed file under.");
    }

    /// <summary>
    /// True when the file already on disk is the one the worker uploaded: same size, and same SHA-256 where <c>result.json</c> lists one.
    /// A file the result gives no size or hash for cannot be checked, so it is kept as it is.
    /// </summary>
    private static async Task<bool> IsKeptFileIntactAsync(string path, WorkerResultFile file, CancellationToken cancellationToken)
    {
        if (file.Bytes is { } expected && new FileInfo(path).Length != expected)
        {
            return false;
        }

        if (file.Sha256 is not { } expectedHash)
        {
            return true;
        }

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
        return string.Equals(actual, expectedHash, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Fetches one file. A changed local file is renamed aside first and counted in <paramref name="tally"/>.</summary>
    private async Task DownloadFileAsync(RunRecord run, string folder, WorkerResultFile file, KeptAsideTally tally, CancellationToken cancellationToken)
    {
        var relative = RunTransfer.SafeRelativeOutputPath(file.Path, "A result file");
        var destination = Path.GetFullPath(Path.Combine(folder, relative));
        if (!destination.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("A result file leaves the output folder.");
        }

        var exists = File.Exists(destination);
        if (exists && await IsKeptFileIntactAsync(destination, file, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        await using var source = await _storage.TryDownloadAsync(run.Bucket!, run.JobPrefix + file.Path, cancellationToken).ConfigureAwait(false)
                                 ?? throw new InvalidDataException("A result file is listed but is not in the bucket.");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var partial = destination + ".part";
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long written = 0;
            await using (var target = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    hash.AppendData(buffer, 0, read);
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    written += read;
                }
            }

            if (file.Bytes is { } expected && expected != written)
            {
                throw new InvalidDataException("A result file has the wrong size.");
            }

            if (file.Sha256 is { } expectedHash && !string.Equals(Convert.ToHexString(hash.GetHashAndReset()), expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("A result file does not match its checksum.");
            }

            // Reached for a missing file, or a kept one that failed its size or checksum. The download is already verified, so a
            // failure above leaves the user's file exactly where it was; only now is it moved aside, never overwritten.
            string? aside = null;
            if (exists)
            {
                aside = MoveAside(destination);
                tally.Count++;
            }

            try
            {
                _mover.Move(partial, destination);
            }
            catch (Exception) when (aside is not null)
            {
                // The user's file must never be left renamed with nothing in its place: put it back, and only then report the failure.
                try
                {
                    _mover.Move(aside, destination);
                    tally.Count--;
                }
                catch (IOException)
                {
                    // Could not put it back; it is still safe under its changed name, which the tally keeps reporting.
                }

                throw;
            }
        }
        finally
        {
            try
            {
                File.Delete(partial);
            }
            catch (IOException)
            {
            }
        }
    }
}
