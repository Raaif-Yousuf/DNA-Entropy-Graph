namespace DnaEntropyGraph.Cloud.Rest;

/// <summary>What a test (or a developer) can change about the real Google gateways. Production passes nothing.</summary>
public sealed class GoogleCloudOptions
{
    /// <summary>When set, every request goes through this handler instead of the network: the scripted HTTP fake in tests.</summary>
    public HttpMessageHandler? HttpHandler { get; init; }

    /// <summary>The proxy choice every request follows (issue #239). Null is the system default (Windows settings, PAC, the Windows sign-in). Ignored when <see cref="HttpHandler"/> is set.</summary>
    public DnaEntropyGraph.Core.Net.IProxyPolicy? Proxy { get; init; }

    /// <summary>The wait between polls of a long-running operation. Null waits for real; a test passes an instant one.</summary>
    public Func<TimeSpan, CancellationToken, Task>? Delay { get; init; }

    /// <summary>The clock the operation deadline runs on (wall-clock time spent in each call counts). Production uses the system clock.</summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    /// <summary>How long a long-running operation (create a project, enable a service) may run before it counts as timed out.</summary>
    public TimeSpan OperationDeadline { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The <c>installation-id</c> label the results bucket carries (<see cref="DnaEntropyGraph.Core.Cloud.InstallationId"/>).
    /// Required to create or adopt a bucket: without it the bucket would be invisible to the Cloud page (Hard Rule 10), so
    /// <c>EnsureBucketAsync</c> fails before sending anything. The production switch (#56) passes the persisted id.
    /// </summary>
    public string? InstallationId { get; init; }

    /// <summary>
    /// Reads the id at each use, for a production build whose id file may be unusable at first and repaired later (issue #609: a value read once at startup would stay null for the whole process).
    /// Returns null when it cannot be read now. <see cref="CurrentInstallationId"/> prefers the fixed <see cref="InstallationId"/> when a test sets one.
    /// </summary>
    public Func<string?>? InstallationIdSource { get; init; }

    /// <summary>The installation id to use for this call: the fixed one, else a fresh read from <see cref="InstallationIdSource"/>, else null.</summary>
    public string? CurrentInstallationId => InstallationId ?? InstallationIdSource?.Invoke();

    /// <summary>The <c>app-version</c> label and the <c>app-config.json</c> field. Production passes the running app's version.</summary>
    public string AppVersion { get; init; } = "unknown";

    /// <summary>
    /// How many days a job's objects are kept before the bucket's own lifecycle rule deletes them: the user's "Cloud results
    /// retention" setting (<c>RunOptions.CloudResultsRetentionDays</c>, default 90). The cache has its own fixed 365 days.
    /// </summary>
    public int ResultsRetentionDays
    {
        get => ResultsRetentionDaysSource?.Invoke() ?? _resultsRetentionDays;
        init => _resultsRetentionDays = value;
    }

    private readonly int _resultsRetentionDays = DnaEntropyGraph.Core.Cloud.ResultsBucket.DefaultRetentionDays;

    /// <summary>
    /// Reads the retention at each use (issue #114), so a change in Settings reaches the very next <c>EnsureBucketAsync</c>. When set it
    /// wins over a fixed <see cref="ResultsRetentionDays"/>. Production passes <c>ResultsRetentionSetting.Days</c> over the settings store.
    /// </summary>
    public Func<int>? ResultsRetentionDaysSource { get; init; }

    /// <summary>The multi-region the bucket is created in (the region group: "US", "EU" or "ASIA"). Data residency (#149) changes this.</summary>
    public string BucketLocation { get; init; } = DnaEntropyGraph.Core.Cloud.ResultsBucket.DefaultLocation;

    /// <summary>Where the random part of a new bucket name comes from. A test passes a seeded one.</summary>
    public Random Random { get; init; } = Random.Shared;
}
