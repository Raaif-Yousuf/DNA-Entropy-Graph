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
/// is a list, a create, a read-back, a second list, a config write and possibly a patch or a delete, and a whole-method retry would replay the create. Each HTTP call
/// goes through <see cref="CloudCallPipeline"/> on its own (docs/cloud_design.md section 16).
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
        if (string.IsNullOrWhiteSpace(_options.InstallationId))
        {
            throw new InvalidOperationException("The results bucket needs the installation id: a bucket without the installation-id label is invisible to the Cloud page (Hard Rule 10).");
        }

        ResultsBucket.ValidateRetentionDays(_options.ResultsRetentionDays);
        var labels = ResultsBucket.Labels(_options.InstallationId, _options.AppVersion);
        var projectNumber = await _projects.GetProjectNumberAsync(projectId, cancellationToken).ConfigureAwait(false);

        var existing = await FindOursAsync(projectId, preferOwnInstallation: true, cancellationToken).ConfigureAwait(false);
        return existing is not null
            ? await ConvergeAsync(existing, cancellationToken).ConfigureAwait(false)
            : await CreateAsync(projectId, projectNumber, labels, cancellationToken).ConfigureAwait(false);
    }

    public async Task UploadAsync(string bucket, string objectKey, Stream content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);

        // The Google client sends a seekable stream from offset 0 whatever its Position, so a stream positioned part way is
        // wrapped to start where it stands. A retry rewinds to that start; a stream that cannot seek cannot be replayed and gets one attempt.
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

    // ------------------------------------------------------------------ discovery

    /// <summary>
    /// The bucket of ours in this project: carrying the app label, preferring this installation's, then the oldest (so two
    /// PCs that raced to create one converge on the same one), then by name.
    /// </summary>
    private async Task<StorageData.Bucket?> FindOursAsync(string projectId, bool preferOwnInstallation, CancellationToken cancellationToken)
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
            .OrderByDescending(b => preferOwnInstallation && string.Equals(InstallationOf(b), _options.InstallationId, StringComparison.Ordinal))
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

                replayed = true;
            }

            if (replayed)
            {
                // A replayed insert may have been the one that half-applied: repair it now (patch, read back) instead of failing.
                await RepairAsync(created, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                ThrowIfNotApplied(created);
            }


            // Two PCs can both list nothing and both insert. Look again before the config is written: if the preferred bucket is
            // somebody else's, adopt it and throw this one away (it is empty, and a bucket this call did not create is never touched).
            // Decided without regard to installation, so two different installations that raced agree on the same winner (oldest, then name).
            var preferred = await FindOursAsync(projectId, preferOwnInstallation: false, cancellationToken).ConfigureAwait(false);
            if (preferred is not null && !string.Equals(preferred.Name, name, StringComparison.Ordinal))
            {
                await DeleteOwnEmptyBucketAsync(name, cancellationToken).ConfigureAwait(false);
                return await ConvergeAsync(preferred, cancellationToken).ConfigureAwait(false);
            }

            await WriteConfigAsync(name, cancellationToken).ConfigureAwait(false);
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
                cancellationToken).ConfigureAwait(false);
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
        catch (CloudOperationException ex) when (ex.Error.Code == PreconditionFailedCode)
        {
            // Another PC wrote it first (the write is "only if absent"): its file stands. An organization-policy 412 is not this and surfaces.
        }
    }

    // ------------------------------------------------------------------ adopt and repair

    private async Task<string> ConvergeAsync(StorageData.Bucket bucket, CancellationToken cancellationToken)
    {
        // The config file is written whenever it is absent, also on an adopted bucket: the call that created the bucket may have
        // died before its write landed (conditional, so a file that is already there stands).
        await RepairAsync(bucket, cancellationToken).ConfigureAwait(false);
        await WriteConfigAsync(bucket.Name, cancellationToken).ConfigureAwait(false);
        return bucket.Name;
    }

    private async Task RepairAsync(StorageData.Bucket bucket, CancellationToken cancellationToken)
    {
        if (Problems(bucket).Count == 0)
        {
            return;
        }

        // Drifted (a changed retention, or a rule or setting that never applied): patch, then read back and compare. The
        // user's own lifecycle rules are kept; only the jobs/ and cache/ Delete rules are ours and are replaced.
        var patch = new StorageData.Bucket { IamConfiguration = DesiredIamConfiguration(), Lifecycle = DesiredLifecycle(bucket.Lifecycle?.Rule) };
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
    }

    private static StorageData.Bucket.IamConfigurationData DesiredIamConfiguration() => new()
    {
        UniformBucketLevelAccess = new StorageData.Bucket.IamConfigurationData.UniformBucketLevelAccessData { Enabled = true },
        PublicAccessPrevention = PublicAccessEnforced,
    };

    /// <summary>Our two rules, after any rules in <paramref name="existing"/> that are not ours (a user's own lifecycle rules survive a patch).</summary>
    private StorageData.Bucket.LifecycleData DesiredLifecycle(IEnumerable<StorageData.Bucket.LifecycleData.RuleData>? existing = null) => new()
    {
        Rule =
        [
            .. (existing ?? []).Where(r => !IsOurRule(r)),
            DeleteRule(ResultsBucket.JobsPrefix, Math.Max(_options.ResultsRetentionDays, LongestOwnJobsAge(existing))),
            DeleteRule(ResultsBucket.CachePrefix, ResultsBucket.CacheRetentionDays),
        ],
    };

    /// <summary>The longest age among the bucket's own jobs/ Delete rules (0 when none): a patch never shortens it.</summary>
    private static int LongestOwnJobsAge(IEnumerable<StorageData.Bucket.LifecycleData.RuleData>? existing)
        => (existing ?? []).Where(r => IsOurRule(r) && r.Condition!.MatchesPrefix![0] == ResultsBucket.JobsPrefix).Select(r => r.Condition!.Age ?? 0).DefaultIfEmpty(0).Max();

    /// <summary>A Delete rule whose only prefix is <c>jobs/</c> or <c>cache/</c>: the two the app owns.</summary>
    private static bool IsOurRule(StorageData.Bucket.LifecycleData.RuleData rule)
        => string.Equals(rule.Action?.Type, DeleteAction, StringComparison.Ordinal)
            && rule.Condition?.MatchesPrefix is { Count: 1 } prefixes
            && (prefixes[0] == ResultsBucket.JobsPrefix || prefixes[0] == ResultsBucket.CachePrefix);

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
                    SetupErrorCodes.BucketConfigNotApplied,
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

        // A jobs/ age LONGER than asked for is not drift: the bucket is shared, and shortening it would delete another
        // installation's results early (Hard Rule 14). Only a missing rule or a shorter age is repaired.
        if (!HasDeleteRule(bucket, ResultsBucket.JobsPrefix, _options.ResultsRetentionDays, orLonger: true))
        {
            problems.Add($"no rule deletes {ResultsBucket.JobsPrefix} objects after {_options.ResultsRetentionDays} days or more");
        }

        if (!HasDeleteRule(bucket, ResultsBucket.CachePrefix, ResultsBucket.CacheRetentionDays))
        {
            problems.Add($"no rule deletes {ResultsBucket.CachePrefix} objects after {ResultsBucket.CacheRetentionDays} days");
        }

        return problems;
    }

    private static bool HasDeleteRule(StorageData.Bucket bucket, string prefix, int ageDays, bool orLonger = false)
        => bucket.Lifecycle?.Rule?.Any(r =>
            string.Equals(r.Action?.Type, DeleteAction, StringComparison.Ordinal)
            && (orLonger ? r.Condition?.Age >= ageDays : r.Condition?.Age == ageDays)
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

    /// <summary>
    /// A 412 is a failed precondition unless it names an organization-policy constraint. THEORY (unverified): Cloud Storage
    /// answers an org-policy violation with 412 as well and may give both the same <c>errors[].reason</c>, so the reason
    /// cannot discriminate; the message naming a <c>constraints/</c> id (as <see cref="CloudErrorClassifier"/> reads org policy) does.
    /// </summary>
    private static CloudOperationException TranslateApi(GoogleApiException api)
    {
        var status = GoogleApiErrors.FromApiException(api);
        if (status.HttpStatus == 412 && GoogleApiErrors.KindOf(status with { HttpStatus = null }) != CloudErrorKind.OrgPolicy)
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
