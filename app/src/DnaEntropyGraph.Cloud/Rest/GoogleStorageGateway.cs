using System.Net;
using System.Text;
using DnaEntropyGraph.Core.Cloud;
using Google;
using Google.Apis.Download;
using Google.Apis.Storage.v1;
using Google.Apis.Upload;
using StorageData = Google.Apis.Storage.v1.Data;

namespace DnaEntropyGraph.Cloud.Rest;

/// <summary>
/// The real <see cref="IStorageGateway"/> over Cloud Storage v1 REST (issue #53): the results bucket, and the object
/// calls the job runner uses. It is never wrapped in <see cref="ResilientStorageGateway"/>: <see cref="EnsureBucketAsync"/>
/// is a project get, a list, a create, a read-back, a second list, a config write and possibly a patch or a delete, and a whole-method retry would replay the create. Each HTTP call
/// goes through <see cref="CloudCallPipeline"/> on its own (docs/cloud_design.md section 16); the metadata calls are capped per attempt
/// (<c>singleRequest: true</c>, <see cref="CloudRetryOptions.AttemptTimeout"/>), the media transfers (upload, download) are not.
/// <para>
/// <b>EnsureBucketAsync.</b> The bucket is found by LABEL (<c>app=dna-entropy-graph</c>) among the project's
/// <c>deg-</c> buckets, never by a fixed name (Hard Rule 9), so a second PC (same installation or another one in the same
/// project) adopts it. One that is found without its app-config.json gets it written (conditional, 412 ignored). One that is found but drifted (UBLA, public access prevention, or a lifecycle rule not as asked,
/// for instance a changed retention) is patched. A new one is <c>deg-&lt;projectNumber&gt;-&lt;rand6&gt;</c>; a 409 on the
/// insert is either our own insert replayed after a dropped connection (the bucket is ours, so it is kept) or a name
/// somebody else holds (a new suffix is drawn, five names at most). Every path ends by reading the bucket back and
/// comparing: "applied is not present", so a bucket that is not what was asked for fails with
/// <see cref="SetupErrorCodes.BucketConfigNotApplied"/> naming what differs, and no name is returned.
/// </para>
/// </summary>
internal sealed class GoogleStorageGateway : IStorageGateway
{
    private const int MaxNameAttempts = 5;
    private const string DeleteAction = "Delete";
    private const string PublicAccessEnforced = "enforced";

    /// <summary>The code of a 412 that is a failed precondition (<c>ifGenerationMatch</c>), not an organization-policy refusal.</summary>
    private const string PreconditionFailedCode = "PRECONDITION_FAILED";

    private readonly StorageService _service;
    private readonly GoogleProjectCatalogGateway _projects;
    private readonly CloudCallPipeline _pipeline;
    private readonly GoogleCloudOptions _options;

    public GoogleStorageGateway(StorageService service, GoogleProjectCatalogGateway projects, CloudCallPipeline pipeline, GoogleCloudOptions options)
    {
        _service = service;
        _projects = projects;
        _pipeline = pipeline;
        _options = options;
    }

    public async Task<string> EnsureBucketAsync(string projectId, CancellationToken cancellationToken)
    {
        var installationId = _options.CurrentInstallationId;
        if (string.IsNullOrWhiteSpace(installationId))
        {
            throw new InvalidOperationException("The results bucket needs the installation id: a bucket without the installation-id label is invisible to the Cloud page (Hard Rule 10).");
        }

        // Read the retention once for the whole call (#114) and pass it down: the option may read settings.json each time, and a change
        // landing mid-call must not make a bucket look drifted or fail the read-back.
        var retention = _options.ResultsRetentionDays;
        ResultsBucket.ValidateRetentionDays(retention);
        var labels = ResultsBucket.Labels(installationId, _options.AppVersion);
        var projectNumber = await _projects.GetProjectNumberAsync(projectId, cancellationToken).ConfigureAwait(false);

        var existing = await FindOursAsync(projectId, preferOwnInstallation: true, cancellationToken).ConfigureAwait(false);
        return existing is not null
            ? await ConvergeAsync(existing, retention, cancellationToken).ConfigureAwait(false)
            : await CreateAsync(projectId, projectNumber, labels, retention, cancellationToken).ConfigureAwait(false);
    }

    public async Task UploadAsync(string bucket, string objectKey, Stream content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);

        // The Google client sends a seekable stream from offset 0 whatever its Position, so a stream positioned part way is
        // wrapped to start where it stands. A retry rewinds to that start; a stream that cannot seek cannot be replayed and gets one attempt.
        // MEASURED 2026-10-03: with the rewind below removed every upload test stayed green, including one that drops the connection part way
        // through the body, so the Google client already seeks a seekable stream to its start on each session; the rewind is defense in depth.
        Stream source = content.CanSeek ? new RemainderStream(content) : content;
        await _pipeline.ExecuteAsync(
            "Storage.Upload",
            async ct =>
            {
                if (source.CanSeek)
                {
                    source.Position = 0;
                }

                await UploadObjectAsync(bucket, objectKey, source, "application/octet-stream", onlyIfAbsent: false, ct).ConfigureAwait(false);
                return true;
            },
            cancellationToken,
            replayable: content.CanSeek).ConfigureAwait(false);
    }

    public async Task<Stream> DownloadAsync(string bucket, string objectKey, CancellationToken cancellationToken)
        => await DownloadCoreAsync(bucket, objectKey, cancellationToken).ConfigureAwait(false)
            ?? throw new CloudOperationException(new CloudError("NOT_FOUND", 404, $"No such object: {bucket}/{objectKey}"), CloudErrorKind.Other);

    public async Task<Stream?> TryDownloadAsync(string bucket, string objectKey, CancellationToken cancellationToken)
        => await DownloadCoreAsync(bucket, objectKey, cancellationToken).ConfigureAwait(false);

    // ------------------------------------------------------------------ issue #105: usage and cleanup of a prefix

    public async Task<PrefixUsage> GetPrefixUsageAsync(string bucket, string prefix, CancellationToken cancellationToken)
    {
        var count = 0;
        long bytes = 0;
        await foreach (var page in ListPagesAsync(bucket, prefix, delimiter: null, cancellationToken).ConfigureAwait(false))
        {
            foreach (var item in page.Items ?? [])
            {
                count++;
                bytes += (long)(item.Size ?? 0);
            }
        }

        return new PrefixUsage(count, bytes);
    }

    public async Task<IReadOnlyList<string>> ListChildPrefixesAsync(string bucket, string prefix, CancellationToken cancellationToken)
    {
        var children = new SortedSet<string>(StringComparer.Ordinal);
        await foreach (var page in ListPagesAsync(bucket, prefix, delimiter: "/", cancellationToken).ConfigureAwait(false))
        {
            foreach (var child in page.Prefixes ?? [])
            {
                children.Add(child);
            }
        }

        return children.ToList();
    }

    /// <summary>Issue #91: deletes exactly one object by its exact name; one already gone (404) counts as deleted. One HTTP request through the pipeline.</summary>
    public Task DeleteObjectAsync(string bucket, string objectKey, CancellationToken cancellationToken)
        => _pipeline.ExecuteAsync(
            "Storage.DeleteObject",
            async ct =>
            {
                try
                {
                    await _service.Objects.Delete(bucket, objectKey).ExecuteAsync(ct).ConfigureAwait(false);
                }
                catch (GoogleApiException ex) when (ex.HttpStatusCode == HttpStatusCode.NotFound)
                {
                    // Already gone is what a delete wants.
                }
                catch (GoogleApiException ex)
                {
                    throw GoogleApiErrors.ToException(GoogleApiErrors.FromApiException(ex));
                }
            },
            cancellationToken,
            bypassBreaker: true,
            singleRequest: true);

    /// <summary>
    /// Lists the objects under the prefix (one page at a time) and deletes each by its exact name; one already gone (404) counts as deleted. A prefix that is
    /// not a folder path is refused before any request (<see cref="StoragePrefixes.RequireDeletable"/>), so a typo can never empty the bucket. Each delete is one
    /// HTTP request through the pipeline; a failure stops the sweep and throws, leaving the rest in place for the caller's read-back to report.
    /// </summary>
    public async Task<int> DeletePrefixAsync(string bucket, string prefix, CancellationToken cancellationToken)
    {
        StoragePrefixes.RequireDeletable(prefix);
        var deleted = 0;
        var manifestKey = prefix + ResultsBucket.ManifestObject;
        var manifestListed = false;
        await foreach (var page in ListPagesAsync(bucket, prefix, delimiter: null, cancellationToken).ConfigureAwait(false))
        {
            foreach (var item in page.Items ?? [])
            {
                if (item.Name == manifestKey)
                {
                    // The listing is lexicographic, so the manifest would be deleted before output/, result.json and status.json. It is what proves
                    // a folder is ours (JobObjectDeleter), so it goes last: a sweep that dies keeps the proof for the retry.
                    manifestListed = true;
                    continue;
                }

                await DeleteObjectAsync(bucket, item.Name, cancellationToken).ConfigureAwait(false);
                deleted++;
            }
        }

        if (manifestListed)
        {
            await DeleteObjectAsync(bucket, manifestKey, cancellationToken).ConfigureAwait(false);
            deleted++;
        }

        return deleted;
    }

    private Task DeleteObjectAsync(string bucket, string name, CancellationToken cancellationToken)
        => _pipeline.ExecuteAsync(
            "Storage.DeleteObject",
            async ct =>
            {
                try
                {
                    await _service.Objects.Delete(bucket, name).ExecuteAsync(ct).ConfigureAwait(false);
                }
                catch (GoogleApiException ex) when (ex.HttpStatusCode == HttpStatusCode.NotFound)
                {
                    // Already gone is what a delete wants.
                }
                catch (GoogleApiException ex)
                {
                    throw GoogleApiErrors.ToException(GoogleApiErrors.FromApiException(ex));
                }
            },
            cancellationToken,
            bypassBreaker: true,
            singleRequest: true);

    /// <summary>Every page of <c>objects.list</c> under <paramref name="prefix"/>. A repeated page token fails the listing (a short total must never read as complete).</summary>
    private async IAsyncEnumerable<StorageData.Objects> ListPagesAsync(string bucket, string prefix, string? delimiter, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string? pageToken = null;
        do
        {
            var requested = pageToken;
            var page = await _pipeline.ExecuteAsync(
                "Storage.ListObjects",
                async ct =>
                {
                    var request = _service.Objects.List(bucket);
                    request.Prefix = prefix;
                    request.Delimiter = delimiter;
                    request.PageToken = requested;
                    try
                    {
                        return await request.ExecuteAsync(ct).ConfigureAwait(false);
                    }
                    catch (GoogleApiException ex)
                    {
                        throw GoogleApiErrors.ToException(GoogleApiErrors.FromApiException(ex));
                    }
                },
                cancellationToken,
                bypassBreaker: true,
                singleRequest: true).ConfigureAwait(false);

            yield return page;

            var next = string.IsNullOrEmpty(page.NextPageToken) ? null : page.NextPageToken;
            if (next is not null && next == pageToken)
            {
                throw new CloudOperationException(
                    new CloudError("LIST_PAGINATION_STUCK", null, "Cloud Storage returned the same page token twice, so the object list may be incomplete."),
                    CloudErrorKind.Network);
            }

            pageToken = next;
        }
        while (pageToken is not null);
    }

    // ------------------------------------------------------------------ discovery

    /// <summary>
    /// The bucket of ours in this project: carrying the app label, preferring this installation's, then the oldest (so two
    /// PCs that raced to create one converge on the same one), then by name.
    /// </summary>
    private async Task<StorageData.Bucket?> FindOursAsync(string projectId, bool preferOwnInstallation, CancellationToken cancellationToken)
    {
        var ours = await ListOursAsync(projectId, cancellationToken).ConfigureAwait(false);

        return ours
            .OrderByDescending(b => preferOwnInstallation && string.Equals(InstallationOf(b), _options.CurrentInstallationId, StringComparison.Ordinal))
            .ThenBy(b => b.TimeCreatedDateTimeOffset ?? DateTimeOffset.MaxValue)
            .ThenBy(b => b.Name, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    /// <summary>Read-only: every bucket of ours in the project (issue #60). Never creates or repairs one.</summary>
    public async Task<IReadOnlyList<BucketDescriptor>> ListBucketsAsync(string projectId, CancellationToken cancellationToken)
    {
        var ours = await ListOursAsync(projectId, cancellationToken).ConfigureAwait(false);
        return ours
            .OrderBy(b => b.TimeCreatedDateTimeOffset ?? DateTimeOffset.MaxValue)
            .ThenBy(b => b.Name, StringComparer.Ordinal)
            .Select(b => new BucketDescriptor(b.Name, b.Location ?? string.Empty, new Dictionary<string, string>(b.Labels ?? new Dictionary<string, string>()), b.TimeCreatedDateTimeOffset, SoftDeleteDays(b)))
            .ToList();
    }

    private async Task<List<StorageData.Bucket>> ListOursAsync(string projectId, CancellationToken cancellationToken)
    {
        var ours = new List<StorageData.Bucket>();
        string? pageToken = null;
        do
        {
            var token = pageToken;
            var page = await _pipeline.ExecuteAsync(
                "Storage.ListBuckets",
                async ct =>
                {
                    var request = _service.Buckets.List(projectId);
                    request.Prefix = ResultsBucket.NamePrefix;
                    request.PageToken = token;
                    try
                    {
                        return await request.ExecuteAsync(ct).ConfigureAwait(false);
                    }
                    catch (GoogleApiException ex)
                    {
                        throw GoogleApiErrors.ToException(GoogleApiErrors.FromApiException(ex));
                    }
                },
                cancellationToken, singleRequest: true).ConfigureAwait(false);

            ours.AddRange((page.Items ?? []).Where(IsOurs));
            pageToken = page.NextPageToken;
        }
        while (!string.IsNullOrEmpty(pageToken));

        return ours;
    }

    /// <summary>
    /// The days a deleted object stays recoverable (and billed) in the bucket: <c>softDeletePolicy.retentionDurationSeconds</c> rounded up to whole days, 0 when soft delete
    /// is off, null when the response carries no policy. MEASURED 2026-10-15 (cloud.google.com/storage/docs/soft-delete): a bucket created without a setting, including through
    /// the JSON API, gets 7 days, and "soft-deleted objects continue to accrue storage charges". THEORY (unverified, ToTest #105): buckets.list returns the policy.
    /// </summary>
    internal static int? SoftDeleteDays(StorageData.Bucket bucket)
        => bucket.SoftDeletePolicy?.RetentionDurationSeconds is { } seconds ? (int)Math.Ceiling(seconds / 86400d) : null;

    private static bool IsOurs(StorageData.Bucket bucket)
        => bucket.Labels is { } labels && labels.TryGetValue("app", out var app) && app == VmSpec.AppLabelValue;

    private static string? InstallationOf(StorageData.Bucket bucket)
        => bucket.Labels is { } labels && labels.TryGetValue("installation-id", out var id) ? id : null;

    // ------------------------------------------------------------------ create

    private async Task<string> CreateAsync(string projectId, string projectNumber, IReadOnlyDictionary<string, string> labels, int retention, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < MaxNameAttempts; attempt++)
        {
            var name = ResultsBucket.NewName(projectNumber, _options.Random);
            var body = new StorageData.Bucket
            {
                Name = name,
                Location = _options.BucketLocation,
                Labels = labels.ToDictionary(p => p.Key, p => p.Value),
                IamConfiguration = DesiredIamConfiguration(),
                Lifecycle = DesiredLifecycle(retention),
            };

            StorageData.Bucket? created;
            var replayed = false;
            try
            {
                await _pipeline.ExecuteAsync(
                    "Storage.CreateBucket",
                    async ct =>
                    {
                        try
                        {
                            return await _service.Buckets.Insert(body, projectId).ExecuteAsync(ct).ConfigureAwait(false);
                        }
                        catch (GoogleApiException ex)
                        {
                            throw GoogleApiErrors.ToException(GoogleApiErrors.FromApiException(ex));
                        }
                    },
                    cancellationToken, singleRequest: true).ConfigureAwait(false);
                created = await GetAsync(name, cancellationToken).ConfigureAwait(false);
            }
            catch (CloudOperationException ex) when (ex.Kind == CloudErrorKind.AlreadyExists)
            {
                // Our own insert, replayed after a dropped connection, lands here too: that bucket is ours, so keep it (this
                // read is its read-back). A name somebody else holds is not readable by us (403) or not ours: draw another suffix.
                created = await TryGetOursAsync(name, cancellationToken).ConfigureAwait(false);
                if (created is null)
                {
                    continue;
                }

                replayed = true;
            }

            if (replayed)
            {
                // A replayed insert may have been the one that half-applied: repair it now (patch, read back) instead of failing.
                await RepairAsync(created, retention, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                ThrowIfNotApplied(created, retention);
            }


            // Two PCs can both list nothing and both insert. Look again before the config is written: if the preferred bucket is
            // somebody else's, adopt it and throw this one away (it is empty, and a bucket this call did not create is never touched).
            // Decided without regard to installation, so two different installations that raced agree on the same winner (oldest, then name).
            var preferred = await FindOursAsync(projectId, preferOwnInstallation: false, cancellationToken).ConfigureAwait(false);
            if (preferred is not null && !string.Equals(preferred.Name, name, StringComparison.Ordinal))
            {
                await DeleteOwnEmptyBucketAsync(name, cancellationToken).ConfigureAwait(false);
                return await ConvergeAsync(preferred, retention, cancellationToken).ConfigureAwait(false);
            }

            await WriteConfigAsync(name, retention, cancellationToken).ConfigureAwait(false);
            return name;
        }

        throw new CloudOperationException(
            new CloudError(SetupErrorCodes.BucketNameTaken, 409, $"Every one of {MaxNameAttempts} generated bucket names was already taken."),
            CloudErrorKind.AlreadyExists);
    }

    /// <summary>
    /// Deletes the bucket this call just created and no longer wants. Best effort: Google refuses to delete a bucket that holds
    /// an object (another PC adopted it in the meantime and wrote its config), which is the right answer, so a refusal is
    /// swallowed and the labelled bucket simply stays (cloud_design.md section 16).
    /// </summary>
    private async Task DeleteOwnEmptyBucketAsync(string name, CancellationToken cancellationToken)
    {
        try
        {
            await _pipeline.ExecuteAsync(
                "Storage.DeleteBucket",
                async ct =>
                {
                    try
                    {
                        await _service.Buckets.Delete(name).ExecuteAsync(ct).ConfigureAwait(false);
                        return true;
                    }
                    catch (GoogleApiException ex)
                    {
                        throw GoogleApiErrors.ToException(GoogleApiErrors.FromApiException(ex));
                    }
                },
                cancellationToken, singleRequest: true).ConfigureAwait(false);
        }
        catch (CloudOperationException)
        {
            // Not empty, already gone or not ours to delete: leave it.
        }
    }

    /// <summary>Our bucket by name, or null when it does not exist or is somebody else's (a 403 or 404, or no app label).</summary>
    private async Task<StorageData.Bucket?> TryGetOursAsync(string name, CancellationToken cancellationToken)
    {
        try
        {
            var bucket = await GetAsync(name, cancellationToken).ConfigureAwait(false);
            return IsOurs(bucket) ? bucket : null;
        }
        catch (CloudOperationException ex) when (ex.Error.HttpStatus is 403 or 404)
        {
            return null;
        }
    }

    private Task<StorageData.Bucket> GetAsync(string name, CancellationToken cancellationToken)
        => _pipeline.ExecuteAsync(
            "Storage.GetBucket",
            async ct =>
            {
                try
                {
                    return await _service.Buckets.Get(name).ExecuteAsync(ct).ConfigureAwait(false);
                }
                catch (GoogleApiException ex)
                {
                    throw GoogleApiErrors.ToException(GoogleApiErrors.FromApiException(ex));
                }
            },
            cancellationToken, singleRequest: true);

    private async Task WriteConfigAsync(string bucket, int retention, CancellationToken cancellationToken)
    {
        var json = ResultsBucket.ConfigJson(_options.CurrentInstallationId ?? throw new InvalidOperationException("The installation id became unreadable while the bucket was being set up."), _options.AppVersion, retention, _options.TimeProvider.GetUtcNow());
        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(json);
        try
        {
            await _pipeline.ExecuteAsync(
                "Storage.WriteConfig",
                async ct =>
                {
                    using var stream = new MemoryStream(bytes);
                    await UploadObjectAsync(bucket, ResultsBucket.ConfigObject, stream, "application/json", onlyIfAbsent: true, ct).ConfigureAwait(false);
                    return true;
                },
                cancellationToken, singleRequest: true).ConfigureAwait(false);
        }
        catch (CloudOperationException ex) when (ex.Error.Code == PreconditionFailedCode)
        {
            // Another PC wrote it first (the write is "only if absent"): its file stands. An organization-policy 412 is not this and surfaces.
        }
    }

    // ------------------------------------------------------------------ adopt and repair

    private async Task<string> ConvergeAsync(StorageData.Bucket bucket, int retention, CancellationToken cancellationToken)
    {
        // The config file is written whenever it is absent, also on an adopted bucket: the call that created the bucket may have
        // died before its write landed (conditional, so a file that is already there stands).
        await RepairAsync(bucket, retention, cancellationToken).ConfigureAwait(false);
        await WriteConfigAsync(bucket.Name, retention, cancellationToken).ConfigureAwait(false);
        return bucket.Name;
    }

    private async Task RepairAsync(StorageData.Bucket bucket, int retention, CancellationToken cancellationToken)
    {
        if (Problems(bucket, retention).Count == 0)
        {
            return;
        }

        // Drifted (a changed retention, or a rule or setting that never applied): patch, then read back and compare. The
        // user's own lifecycle rules are kept; only the jobs/ and cache/ Delete rules are ours and are replaced.
        var patch = new StorageData.Bucket { IamConfiguration = DesiredIamConfiguration(), Lifecycle = DesiredLifecycle(retention, bucket.Lifecycle?.Rule) };
        await _pipeline.ExecuteAsync(
            "Storage.PatchBucket",
            async ct =>
            {
                try
                {
                    return await _service.Buckets.Patch(patch, bucket.Name).ExecuteAsync(ct).ConfigureAwait(false);
                }
                catch (GoogleApiException ex)
                {
                    throw GoogleApiErrors.ToException(GoogleApiErrors.FromApiException(ex));
                }
            },
            cancellationToken, singleRequest: true).ConfigureAwait(false);

        ThrowIfNotApplied(await GetAsync(bucket.Name, cancellationToken).ConfigureAwait(false), retention);
    }

    private static StorageData.Bucket.IamConfigurationData DesiredIamConfiguration() => new()
    {
        UniformBucketLevelAccess = new StorageData.Bucket.IamConfigurationData.UniformBucketLevelAccessData { Enabled = true },
        PublicAccessPrevention = PublicAccessEnforced,
    };

    /// <summary>Our two rules, after any rules in <paramref name="existing"/> that are not ours (a user's own lifecycle rules survive a patch).</summary>
    private StorageData.Bucket.LifecycleData DesiredLifecycle(int retention, IEnumerable<StorageData.Bucket.LifecycleData.RuleData>? existing = null) => new()
    {
        Rule =
        [
            .. (existing ?? []).Where(r => !IsOurRule(r)),
            DeleteRule(ResultsBucket.JobsPrefix, Math.Max(retention, LongestOwnAge(existing, ResultsBucket.JobsPrefix))),
            DeleteRule(ResultsBucket.CachePrefix, Math.Max(ResultsBucket.CacheRetentionDays, LongestOwnAge(existing, ResultsBucket.CachePrefix))),
        ],
    };

    /// <summary>The longest age among the bucket's own Delete rules for <paramref name="prefix"/> (0 when none): a patch never shortens it.</summary>
    private static int LongestOwnAge(IEnumerable<StorageData.Bucket.LifecycleData.RuleData>? existing, string prefix)
        => (existing ?? []).Where(r => IsOurRule(r) && r.Condition!.MatchesPrefix![0] == prefix).Select(r => r.Condition!.Age ?? 0).DefaultIfEmpty(0).Max();

    /// <summary>
    /// A Delete rule whose condition is exactly an age and the one prefix <c>jobs/</c> or <c>cache/</c>: the two the app owns.
    /// A user's rule on the same prefix with any extra condition (storage class, live state, noncurrent time, a suffix...) is
    /// theirs: it is neither counted toward our age nor replaced.
    /// </summary>
    private static bool IsOurRule(StorageData.Bucket.LifecycleData.RuleData rule)
        => string.Equals(rule.Action?.Type, DeleteAction, StringComparison.Ordinal)
            && rule.Condition is { MatchesPrefix: { Count: 1 } prefixes } c
            && (prefixes[0] == ResultsBucket.JobsPrefix || prefixes[0] == ResultsBucket.CachePrefix)
            && c.Age is not null
            && c.CreatedBefore is null && c.CustomTimeBefore is null && c.NoncurrentTimeBefore is null
            && c.DaysSinceCustomTime is null && c.DaysSinceNoncurrentTime is null && c.IsLive is null && c.NumNewerVersions is null
            && c.MatchesPattern is null && c.MatchesStorageClass is null && c.MatchesSuffix is null;

    private static StorageData.Bucket.LifecycleData.RuleData DeleteRule(string prefix, int ageDays) => new()
    {
        Action = new StorageData.Bucket.LifecycleData.RuleData.ActionData { Type = DeleteAction },
        Condition = new StorageData.Bucket.LifecycleData.RuleData.ConditionData { Age = ageDays, MatchesPrefix = [prefix] },
    };

    // ------------------------------------------------------------------ read-back

    private void ThrowIfNotApplied(StorageData.Bucket readBack, int retention)
    {
        var problems = Problems(readBack, retention);
        if (problems.Count > 0)
        {
            throw new CloudOperationException(
                new CloudError(
                    SetupErrorCodes.BucketConfigNotApplied,
                    null,
                    $"Bucket {readBack.Name} does not read back as asked: {string.Join("; ", problems)}."),
                CloudErrorKind.Other);
        }
    }

    /// <summary>What a bucket read back from Google gets wrong. Empty means every setting is what was asked for.</summary>
    private List<string> Problems(StorageData.Bucket bucket, int retention)
    {
        var problems = new List<string>();
        if (bucket.IamConfiguration?.UniformBucketLevelAccess?.Enabled != true)
        {
            problems.Add("uniform bucket-level access is not on");
        }

        if (!string.Equals(bucket.IamConfiguration?.PublicAccessPrevention, PublicAccessEnforced, StringComparison.Ordinal))
        {
            problems.Add("public access prevention is not enforced");
        }

        // A jobs/ or cache/ age LONGER than asked for is not drift: the bucket is shared, and shortening it would delete another
        // installation's files early (Hard Rule 14, #597). Only a missing rule or a shorter age is repaired.
        if (!HasDeleteRule(bucket, ResultsBucket.JobsPrefix, retention, orLonger: true))
        {
            problems.Add($"no rule deletes {ResultsBucket.JobsPrefix} objects after {retention} days or more");
        }

        if (!HasDeleteRule(bucket, ResultsBucket.CachePrefix, ResultsBucket.CacheRetentionDays, orLonger: true))
        {
            problems.Add($"no rule deletes {ResultsBucket.CachePrefix} objects after {ResultsBucket.CacheRetentionDays} days or more");
        }

        return problems;
    }

    private static bool HasDeleteRule(StorageData.Bucket bucket, string prefix, int ageDays, bool orLonger = false)
        => bucket.Lifecycle?.Rule?.Any(r =>
            IsOurRule(r)
            && r.Condition!.MatchesPrefix![0] == prefix
            && (orLonger ? r.Condition.Age >= ageDays : r.Condition.Age == ageDays)) == true;

    // ------------------------------------------------------------------ objects

    private async Task UploadObjectAsync(string bucket, string objectKey, Stream content, string contentType, bool onlyIfAbsent, CancellationToken cancellationToken)
    {
        var upload = _service.Objects.Insert(new StorageData.Object { Name = objectKey }, bucket, content, contentType);
        if (onlyIfAbsent)
        {
            upload.IfGenerationMatch = 0;
        }

        var progress = await upload.UploadAsync(cancellationToken).ConfigureAwait(false);
        if (progress.Status == UploadStatus.Failed)
        {
            throw Translate(progress.Exception);
        }
    }

    /// <summary>The object's bytes, or null for a 404. Held in memory: results and manifests are small, the multi-GB weights cache goes through the worker (issue #496).</summary>
    private Task<Stream?> DownloadCoreAsync(string bucket, string objectKey, CancellationToken cancellationToken)
        => _pipeline.ExecuteAsync<Stream?>(
            "Storage.Download",
            async ct =>
            {
                var buffer = new MemoryStream();
                var progress = await _service.Objects.Get(bucket, objectKey).DownloadAsync(buffer, ct).ConfigureAwait(false);
                if (progress.Status == DownloadStatus.Failed)
                {
                    await buffer.DisposeAsync().ConfigureAwait(false);
                    if (progress.Exception is GoogleApiException { HttpStatusCode: HttpStatusCode.NotFound })
                    {
                        return null;
                    }

                    throw Translate(progress.Exception);
                }

                buffer.Position = 0;
                return buffer;
            },
            cancellationToken);

    /// <summary>
    /// A 412 that positively looks like a failed precondition (the shared rule, <see cref="CloudErrorClassifier.IsPreconditionConflict"/>)
    /// is the app's own <c>PRECONDITION_FAILED</c>; every other 412 is classified as everywhere else, which makes it an
    /// organization-policy refusal. THEORY (unverified): Cloud Storage answers an org-policy violation with 412 as well.
    /// </summary>
    private static CloudOperationException TranslateApi(GoogleApiException api)
    {
        var status = GoogleApiErrors.FromApiException(api);
        if (CloudErrorClassifier.IsPreconditionConflict(status.HttpStatus, status.Status, status.Message, status.Reasons)
            && GoogleApiErrors.KindOf(status) != CloudErrorKind.OrgPolicy)
        {
            return new CloudOperationException(new CloudError(PreconditionFailedCode, 412, status.Message), CloudErrorKind.Other);
        }

        return GoogleApiErrors.ToException(status);
    }

    /// <summary>A Google error becomes the app's exception; anything else (a dropped connection) is rethrown as it was so the pipeline classifies it as network.</summary>
    private static Exception Translate(Exception? failure) => failure switch
    {
        GoogleApiException api => TranslateApi(api),
        null => new InvalidOperationException("A Cloud Storage transfer failed without saying why."),
        _ => failure,
    };
}

/// <summary>A seekable stream seen from the position it had when wrapped: offset 0 is that position, the length is what remains.</summary>
internal sealed class RemainderStream : Stream
{
    private readonly Stream _inner;
    private readonly long _start;

    public RemainderStream(Stream inner)
    {
        _inner = inner;
        _start = inner.Position;
    }

    public override bool CanRead => _inner.CanRead;

    public override bool CanSeek => true;

    public override bool CanWrite => false;

    public override long Length => _inner.Length - _start;

    public override long Position
    {
        get => _inner.Position - _start;
        set => _inner.Position = _start + value;
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

    public override int Read(Span<byte> buffer) => _inner.Read(buffer);

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => _inner.ReadAsync(buffer, offset, count, cancellationToken);

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        => _inner.ReadAsync(buffer, cancellationToken);

    public override long Seek(long offset, SeekOrigin origin)
    {
        Position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => Position + offset,
            _ => Length + offset,
        };
        return Position;
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
