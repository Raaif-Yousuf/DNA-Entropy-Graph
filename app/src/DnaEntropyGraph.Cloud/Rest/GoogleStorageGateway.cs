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
/// is a list, a create, a read-back and possibly a patch, and a whole-method retry would replay the create. Each HTTP call
/// goes through <see cref="CloudCallPipeline"/> on its own (docs/cloud_design.md section 16).
/// <para>
/// <b>EnsureBucketAsync.</b> The bucket is found by LABEL (<c>app=dna-entropy-graph</c>) among the project's
/// <c>deg-</c> buckets, never by a fixed name (Hard Rule 9), so a second PC (same installation or another one in the same
/// project) adopts it. One that is found but drifted (UBLA, public access prevention, or a lifecycle rule not as asked,
/// for instance a changed retention) is patched. A new one is <c>deg-&lt;projectNumber&gt;-&lt;rand6&gt;</c>; a 409 on the
/// insert is either our own insert replayed after a dropped connection (the bucket is ours, so it is kept) or a name
/// somebody else holds (a new suffix is drawn, five names at most). Every path ends by reading the bucket back and
/// comparing: "applied is not present", so a bucket that is not what was asked for fails with
/// <see cref="ResultsBucketErrorCodes.ConfigNotApplied"/> naming what differs, and no name is returned.
/// </para>
/// </summary>
internal sealed class GoogleStorageGateway : IStorageGateway
{
    private const int MaxNameAttempts = 5;
    private const string DeleteAction = "Delete";
    private const string PublicAccessEnforced = "enforced";

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
        if (string.IsNullOrWhiteSpace(_options.InstallationId))
        {
            throw new InvalidOperationException("The results bucket needs the installation id: a bucket without the installation-id label is invisible to the Cloud page (Hard Rule 10).");
        }

        var labels = ResultsBucket.Labels(_options.InstallationId, _options.AppVersion);
        var projectNumber = await _projects.GetProjectNumberAsync(projectId, cancellationToken).ConfigureAwait(false);

        var existing = await FindOursAsync(projectId, cancellationToken).ConfigureAwait(false);
        return existing is not null
            ? await ConvergeAsync(existing, cancellationToken).ConfigureAwait(false)
            : await CreateAsync(projectId, projectNumber, labels, cancellationToken).ConfigureAwait(false);
    }

    public async Task UploadAsync(string bucket, string objectKey, Stream content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);

        // A retry re-reads the stream, so rewind it first; a stream that cannot seek cannot be replayed and gets one attempt.
        var start = content.CanSeek ? content.Position : 0;
        await _pipeline.ExecuteAsync(
            "Storage.Upload",
            async ct =>
            {
                if (content.CanSeek)
                {
                    content.Position = start;
                }

                await UploadObjectAsync(bucket, objectKey, content, "application/octet-stream", onlyIfAbsent: false, ct).ConfigureAwait(false);
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

    // ------------------------------------------------------------------ discovery

    /// <summary>
    /// The bucket of ours in this project: carrying the app label, preferring this installation's, then the oldest (so two
    /// PCs that raced to create one converge on the same one), then by name.
    /// </summary>
    private async Task<StorageData.Bucket?> FindOursAsync(string projectId, CancellationToken cancellationToken)
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
                cancellationToken).ConfigureAwait(false);

            ours.AddRange((page.Items ?? []).Where(IsOurs));
            pageToken = page.NextPageToken;
        }
        while (!string.IsNullOrEmpty(pageToken));

        return ours
            .OrderByDescending(b => string.Equals(InstallationOf(b), _options.InstallationId, StringComparison.Ordinal))
            .ThenBy(b => b.TimeCreatedDateTimeOffset ?? DateTimeOffset.MaxValue)
            .ThenBy(b => b.Name, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    private static bool IsOurs(StorageData.Bucket bucket)
        => bucket.Labels is { } labels && labels.TryGetValue("app", out var app) && app == VmSpec.AppLabelValue;

    private static string? InstallationOf(StorageData.Bucket bucket)
        => bucket.Labels is { } labels && labels.TryGetValue("installation-id", out var id) ? id : null;

    // ------------------------------------------------------------------ create

    private async Task<string> CreateAsync(string projectId, string projectNumber, IReadOnlyDictionary<string, string> labels, CancellationToken cancellationToken)
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
                Lifecycle = DesiredLifecycle(),
            };

            StorageData.Bucket? created;
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
                    cancellationToken).ConfigureAwait(false);
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
            }

            ThrowIfNotApplied(created);
            await WriteConfigAsync(name, cancellationToken).ConfigureAwait(false);
            return name;
        }

        throw new CloudOperationException(
            new CloudError(ResultsBucketErrorCodes.NameTaken, 409, $"Every one of {MaxNameAttempts} generated bucket names was already taken."),
            CloudErrorKind.AlreadyExists);
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
            cancellationToken);

    private async Task WriteConfigAsync(string bucket, CancellationToken cancellationToken)
    {
        var json = ResultsBucket.ConfigJson(_options.InstallationId!, _options.AppVersion, _options.ResultsRetentionDays, _options.TimeProvider.GetUtcNow());
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
                cancellationToken).ConfigureAwait(false);
        }
        catch (CloudOperationException ex) when (ex.Error.HttpStatus == 412)
        {
            // Another PC wrote it first (the write is "only if absent"): its file stands.
        }
    }

    // ------------------------------------------------------------------ adopt and repair

    private async Task<string> ConvergeAsync(StorageData.Bucket bucket, CancellationToken cancellationToken)
    {
        if (Problems(bucket).Count == 0)
        {
            return bucket.Name;
        }

        // Drifted (a changed retention, or a rule or setting that never applied): patch, then read back and compare.
        var patch = new StorageData.Bucket { IamConfiguration = DesiredIamConfiguration(), Lifecycle = DesiredLifecycle() };
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
            cancellationToken).ConfigureAwait(false);

        ThrowIfNotApplied(await GetAsync(bucket.Name, cancellationToken).ConfigureAwait(false));
        return bucket.Name;
    }

    private static StorageData.Bucket.IamConfigurationData DesiredIamConfiguration() => new()
    {
        UniformBucketLevelAccess = new StorageData.Bucket.IamConfigurationData.UniformBucketLevelAccessData { Enabled = true },
        PublicAccessPrevention = PublicAccessEnforced,
    };

    private StorageData.Bucket.LifecycleData DesiredLifecycle() => new()
    {
        Rule =
        [
            DeleteRule(ResultsBucket.JobsPrefix, _options.ResultsRetentionDays),
            DeleteRule(ResultsBucket.CachePrefix, ResultsBucket.CacheRetentionDays),
        ],
    };

    private static StorageData.Bucket.LifecycleData.RuleData DeleteRule(string prefix, int ageDays) => new()
    {
        Action = new StorageData.Bucket.LifecycleData.RuleData.ActionData { Type = DeleteAction },
        Condition = new StorageData.Bucket.LifecycleData.RuleData.ConditionData { Age = ageDays, MatchesPrefix = [prefix] },
    };

    // ------------------------------------------------------------------ read-back

    private void ThrowIfNotApplied(StorageData.Bucket readBack)
    {
        var problems = Problems(readBack);
        if (problems.Count > 0)
        {
            throw new CloudOperationException(
                new CloudError(
                    ResultsBucketErrorCodes.ConfigNotApplied,
                    null,
                    $"Bucket {readBack.Name} does not read back as asked: {string.Join("; ", problems)}."),
                CloudErrorKind.Other);
        }
    }

    /// <summary>What a bucket read back from Google gets wrong. Empty means every setting is what was asked for.</summary>
    private List<string> Problems(StorageData.Bucket bucket)
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

        if (!HasDeleteRule(bucket, ResultsBucket.JobsPrefix, _options.ResultsRetentionDays))
        {
            problems.Add($"no rule deletes {ResultsBucket.JobsPrefix} objects after {_options.ResultsRetentionDays} days");
        }

        if (!HasDeleteRule(bucket, ResultsBucket.CachePrefix, ResultsBucket.CacheRetentionDays))
        {
            problems.Add($"no rule deletes {ResultsBucket.CachePrefix} objects after {ResultsBucket.CacheRetentionDays} days");
        }

        return problems;
    }

    private static bool HasDeleteRule(StorageData.Bucket bucket, string prefix, int ageDays)
        => bucket.Lifecycle?.Rule?.Any(r =>
            string.Equals(r.Action?.Type, DeleteAction, StringComparison.Ordinal)
            && r.Condition?.Age == ageDays
            && r.Condition.MatchesPrefix?.Contains(prefix) == true) == true;

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

    /// <summary>A Google error becomes the app's exception; anything else (a dropped connection) is rethrown as it was so the pipeline classifies it as network.</summary>
    private static Exception Translate(Exception? failure) => failure switch
    {
        GoogleApiException api => GoogleApiErrors.ToException(GoogleApiErrors.FromApiException(api)),
        null => new InvalidOperationException("A Cloud Storage transfer failed without saying why."),
        _ => failure,
    };
}
