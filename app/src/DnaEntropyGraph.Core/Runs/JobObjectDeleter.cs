using System.Text.RegularExpressions;
using DnaEntropyGraph.Core.Cloud;

namespace DnaEntropyGraph.Core.Runs;

/// <summary>
/// A job's cloud copy was only partly deleted (issues #262 and #534). <see cref="Remaining"/> is how many objects were still under the prefix
/// when the sweep stopped, or null when that could not be read either. Nothing is recorded as deleted; the same press finishes it.
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

/// <summary>
/// The real <see cref="IJobObjectDeleter"/> (issues #262 and #534). It deletes only <c>jobs/&lt;job id&gt;/</c> in one of our <c>deg-</c> buckets:
/// the prefix is checked against the exact shape of a job folder before any request, so an empty, partial or wider prefix (<c>jobs/</c>,
/// <c>cache/</c>, a path with <c>..</c>) is refused (Hard Rule 14). It reads the prefix back afterwards, so a delete that was accepted and not done
/// is reported as partial, never as success.
/// </summary>
public sealed partial class JobObjectDeleter : IJobObjectDeleter
{
    private readonly IStorageGateway _storage;

    public JobObjectDeleter(IStorageGateway storage)
    {
        _storage = storage;
    }

    public bool IsAvailable => true;

    [GeneratedRegex("^jobs/[a-z0-9_-]{1,63}/$", RegexOptions.CultureInvariant)]
    private static partial Regex JobFolder();

    [GeneratedRegex("^deg-[a-z0-9][a-z0-9._-]{0,57}$", RegexOptions.CultureInvariant)]
    private static partial Regex ResultsBucketName();

    public async Task DeleteJobObjectsAsync(string bucket, string jobPrefix, CancellationToken cancellationToken)
    {
        if (bucket is null || !ResultsBucketName().IsMatch(bucket))
        {
            throw new ArgumentException("That is not one of this app's results buckets.", nameof(bucket));
        }

        if (jobPrefix is null || !JobFolder().IsMatch(jobPrefix))
        {
            throw new ArgumentException("A delete may only target one job's own folder, jobs/<job id>/.", nameof(jobPrefix));
        }

        try
        {
            await _storage.DeletePrefixAsync(bucket, jobPrefix, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not ArgumentException)
        {
            throw new PartialCloudDeleteException(await CountLeftAsync(bucket, jobPrefix, cancellationToken).ConfigureAwait(false), ex);
        }

        var left = await CountLeftAsync(bucket, jobPrefix, cancellationToken).ConfigureAwait(false);
        if (left != 0)
        {
            throw new PartialCloudDeleteException(left);
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
