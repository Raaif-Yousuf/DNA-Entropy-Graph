using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using DnaEntropyGraph.Core.Cloud;

namespace DnaEntropyGraph.Core.Runs;

/// <summary>
/// A job's cloud copy was only partly deleted (issues #262 and #534): some objects went and some are still there.
/// <see cref="Remaining"/> is how many were under the prefix when the sweep stopped, or null when that could not be read.
/// Nothing is recorded as deleted; the same press finishes it.
/// </summary>
public sealed class PartialCloudDeleteException : Exception
{
    public PartialCloudDeleteException(int? remaining, Exception? inner = null)
        : base("The cloud copy was only partly deleted.", inner)
    {
        Remaining = remaining;
    }

    public int? Remaining { get; }
}

/// <summary>Why a delete was refused before any object was touched.</summary>
public enum CloudDeleteRefusal
{
    /// <summary>The bucket or prefix is not exactly one of our job folders.</summary>
    NotAJobFolder,

    /// <summary>The folder's manifest does not name this installation (or cannot be read): it was made on another computer, or is not ours.</summary>
    OtherComputer,
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
    private const string ManifestName = "manifest.json";

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

    public async Task DeleteJobObjectsAsync(string bucket, string jobPrefix, CancellationToken cancellationToken)
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
        var before = (await _storage.GetPrefixUsageAsync(bucket, jobPrefix, cancellationToken).ConfigureAwait(false)).ObjectCount;
        if (before == 0)
        {
            return;
        }

        await RequireOursAsync(bucket, jobPrefix, cancellationToken).ConfigureAwait(false);

        ExceptionDispatchInfo? failure = null;
        try
        {
            await _storage.DeletePrefixAsync(bucket, jobPrefix, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            failure = ExceptionDispatchInfo.Capture(ex);
        }

        var left = await CountLeftAsync(bucket, jobPrefix, cancellationToken).ConfigureAwait(false);
        if (left == 0)
        {
            // Gone, even if the call that did it threw afterwards (a dropped connection on the last reply).
            return;
        }

        if (left == before)
        {
            // Nothing went: the real error if there was one, else a delete that was accepted and not done.
            failure?.Throw();
            throw new InvalidOperationException("The delete was accepted but removed nothing.");
        }

        throw new PartialCloudDeleteException(left, failure?.SourceException);
    }

    private async Task RequireOursAsync(string bucket, string jobPrefix, CancellationToken cancellationToken)
    {
        var installation = _installationId();
        if (!InstallationId.IsValidLabelValue(installation))
        {
            throw new CloudDeleteRefusedException(CloudDeleteRefusal.OtherComputer, "This installation cannot be identified, so the folder is not known to be its own.");
        }

        var stream = await _storage.TryDownloadAsync(bucket, jobPrefix + ManifestName, cancellationToken).ConfigureAwait(false);
        if (stream is null)
        {
            throw new CloudDeleteRefusedException(CloudDeleteRefusal.OtherComputer, "The folder has no readable manifest, so it is not known to be this installation's.");
        }

        await using (stream.ConfigureAwait(false))
        {
            if (!await ManifestNamesAsync(stream, installation!, cancellationToken).ConfigureAwait(false))
            {
                throw new CloudDeleteRefusedException(CloudDeleteRefusal.OtherComputer, "The folder was made by another installation.");
            }
        }
    }

    private static async Task<bool> ManifestNamesAsync(Stream manifest, string installationId, CancellationToken cancellationToken)
    {
        try
        {
            using var document = await JsonDocument.ParseAsync(manifest, cancellationToken: cancellationToken).ConfigureAwait(false);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("createdBy", out var createdBy)
                && createdBy.ValueKind == JsonValueKind.Object
                && createdBy.TryGetProperty("installationId", out var id)
                && id.ValueKind == JsonValueKind.String
                && id.GetString() == installationId;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private async Task<int?> CountLeftAsync(string bucket, string jobPrefix, CancellationToken cancellationToken)
    {
        try
        {
            return (await _storage.GetPrefixUsageAsync(bucket, jobPrefix, cancellationToken).ConfigureAwait(false)).ObjectCount;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }
}
