using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using DnaEntropyGraph.Core.Cloud;

namespace DnaEntropyGraph.Core.Runs;

/// <summary>
/// A job's cloud copy was only partly deleted (issues #262 and #534): some objects went and some are still there.
/// <see cref="Remaining"/> is how many were under the prefix when the sweep stopped, or null when that could not be read (then some may have gone or none:
/// it is not known). <see cref="Exception.InnerException"/> is the classified failure that stopped the sweep, when there was one.
/// Nothing is recorded as deleted; the same press finishes it.
/// </summary>
public sealed class PartialCloudDeleteException : Exception
{
    public PartialCloudDeleteException(int? remaining, bool sweepCompleted, Exception? inner = null)
        : base("The cloud copy was only partly deleted.", inner)
    {
        Remaining = remaining;
        SweepCompleted = sweepCompleted;
    }

    public int? Remaining { get; }

    /// <summary>True when the sweep itself finished without error and only the count read back afterwards failed or disagreed. Set where the exception is made, never inferred from <see cref="Exception.InnerException"/>.</summary>
    public bool SweepCompleted { get; }
}

/// <summary>Why a delete was refused before any object was touched.</summary>
public enum CloudDeleteRefusal
{
    /// <summary>The bucket or prefix is not exactly one of our job folders.</summary>
    NotAJobFolder,

    /// <summary>The folder's manifest names a different installation: it was made on another computer.</summary>
    OtherComputer,

    /// <summary>Ownership could not be confirmed: no manifest, an unreadable or wrong-shaped one, or this installation's id cannot be read. Not the same as "another computer".</summary>
    Unconfirmed,

    /// <summary>The bucket does not exist (or the listing 404s), so there is nothing to prove was deleted.</summary>
    StorageNotFound,
}

/// <summary>A delete that was refused up front: nothing was requested to be deleted.</summary>
public sealed class CloudDeleteRefusedException : Exception
{
    public CloudDeleteRefusedException(CloudDeleteRefusal reason, string message)
        : base(message)
    {
        Reason = reason;
    }

    public CloudDeleteRefusal Reason { get; }
}

/// <summary>
/// The real <see cref="IJobObjectDeleter"/> (issues #262 and #534). It deletes only <c>jobs/&lt;job id&gt;/</c> in one of our <c>deg-</c> buckets, and only
/// when that folder's <c>manifest.json</c> names this installation (the same test <see cref="InstallationCleanup"/> uses): the shapes are checked before any
/// request (so <c>jobs/</c>, <c>cache/</c>, an empty or partial prefix or a <c>..</c> is refused), and a folder another computer made is refused untouched.
/// It reads the prefix back afterwards, so a delete that was accepted and not done is never reported as success (Hard Rule 14).
/// </summary>
public sealed partial class JobObjectDeleter : IJobObjectDeleter
{
    private const string ManifestName = ResultsBucket.ManifestObject;

    private readonly IStorageGateway _storage;
    private readonly Func<string?> _installationId;

    /// <param name="installationId">Read at each delete: this installation's id, or null when it cannot be read (every delete is then refused).</param>
    public JobObjectDeleter(IStorageGateway storage, Func<string?> installationId)
    {
        _storage = storage;
        _installationId = installationId;
    }

    public bool IsAvailable => true;

    [GeneratedRegex(@"\Ajobs/[a-z0-9_-]{1,63}/\z", RegexOptions.CultureInvariant)]
    private static partial Regex JobFolder();

    [GeneratedRegex(@"\Adeg-[a-z0-9][a-z0-9._-]{0,57}\z", RegexOptions.CultureInvariant)]
    private static partial Regex ResultsBucketName();

    public async Task DeleteJobObjectsAsync(CloudScope scope, string bucket, string jobPrefix, CancellationToken cancellationToken)
    {
        if (bucket is null || !ResultsBucketName().IsMatch(bucket))
        {
            throw new CloudDeleteRefusedException(CloudDeleteRefusal.NotAJobFolder, "That is not one of this app's results buckets.");
        }

        if (jobPrefix is null || !JobFolder().IsMatch(jobPrefix))
        {
            throw new CloudDeleteRefusedException(CloudDeleteRefusal.NotAJobFolder, "A delete may only target one job's own folder, jobs/<job id>/.");
        }

        // A failure here is before any delete: it surfaces as itself (a classified cloud failure), never as a partial delete.
        int before;
        try
        {
            before = (await _storage.GetPrefixUsageAsync(scope, bucket, jobPrefix, cancellationToken).ConfigureAwait(false)).ObjectCount;
        }
        catch (CloudOperationException ex) when (ex.Error.HttpStatus == 404)
        {
            // An empty prefix in a bucket that exists is proof of "gone"; a bucket that does not exist is not (a wrong project, a deleted bucket).
            throw new CloudDeleteRefusedException(CloudDeleteRefusal.StorageNotFound, "The results bucket was not found.");
        }

        if (before == 0)
        {
            return;
        }

        await RequireOursAsync(scope, bucket, jobPrefix, cancellationToken).ConfigureAwait(false);

        ExceptionDispatchInfo? failure = null;
        try
        {
            await _storage.DeletePrefixAsync(scope, bucket, jobPrefix, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            failure = ExceptionDispatchInfo.Capture(ex);
        }

        var left = await CountLeftAsync(scope, bucket, jobPrefix, cancellationToken).ConfigureAwait(false);
        if (left is null && failure is not null)
        {
            // The count after the sweep could not be read, and the sweep itself failed with a real, classified error: some objects may have gone before it
            // (the failure can come after any number of them). That is a possibly-partial delete that CARRIES the classified cause as its inner exception, so
            // the page can name the action for that cause without claiming that nothing was deleted (it does not know).
            throw new PartialCloudDeleteException(null, sweepCompleted: false, failure.SourceException);
        }

        if (left == 0)
        {
            // Gone, even if the call that did it threw afterwards (a dropped connection on the last reply).
            return;
        }

        if (left == before)
        {
            // Nothing went: the real error if there was one; else a delete that was accepted and not done, reported as a failed cloud call
            // (the general "try again, then save diagnostics" copy), never a crash.
            failure?.Throw();
            throw new CloudOperationException(new CloudError("DELETE_NOT_APPLIED", null, "The delete was accepted but removed nothing."), CloudErrorKind.Other);
        }

        // Some went and some did not, or more objects than before are there now (something else wrote under the prefix meanwhile): partial, with the count.
        throw new PartialCloudDeleteException(left, sweepCompleted: failure is null, failure?.SourceException);
    }

    private async Task RequireOursAsync(CloudScope scope, string bucket, string jobPrefix, CancellationToken cancellationToken)
    {
        var installation = _installationId();
        if (!InstallationId.IsValidLabelValue(installation))
        {
            throw new CloudDeleteRefusedException(CloudDeleteRefusal.Unconfirmed, "This installation cannot be identified, so the folder is not known to be its own.");
        }

        var stream = await _storage.TryDownloadAsync(scope, bucket, jobPrefix + ManifestName, cancellationToken).ConfigureAwait(false);
        if (stream is null)
        {
            throw new CloudDeleteRefusedException(CloudDeleteRefusal.Unconfirmed, "The folder has no manifest, so it is not known to be this installation's.");
        }

        string? owner;
        await using (stream.ConfigureAwait(false))
        {
            owner = await ReadOwnerAsync(stream, cancellationToken).ConfigureAwait(false);
        }

        if (owner is null)
        {
            throw new CloudDeleteRefusedException(CloudDeleteRefusal.Unconfirmed, "The folder's manifest could not be read as one that names an installation.");
        }

        if (owner != installation)
        {
            throw new CloudDeleteRefusedException(CloudDeleteRefusal.OtherComputer, "The folder was made by another installation.");
        }
    }

    /// <summary>The <c>createdBy.installationId</c> of a manifest, or null when the JSON is not an object of that shape.</summary>
    private static async Task<string?> ReadOwnerAsync(Stream manifest, CancellationToken cancellationToken)
    {
        try
        {
            using var document = await JsonDocument.ParseAsync(manifest, cancellationToken: cancellationToken).ConfigureAwait(false);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("createdBy", out var createdBy)
                && createdBy.ValueKind == JsonValueKind.Object
                && createdBy.TryGetProperty("installationId", out var id)
                && id.ValueKind == JsonValueKind.String
                ? id.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<int?> CountLeftAsync(CloudScope scope, string bucket, string jobPrefix, CancellationToken cancellationToken)
    {
        try
        {
            return (await _storage.GetPrefixUsageAsync(scope, bucket, jobPrefix, cancellationToken).ConfigureAwait(false)).ObjectCount;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }
}
