namespace DnaEntropyGraph.Core.Cloud;

/// <summary>
/// The timeouts and clock of one <see cref="CloudJobRunner"/>, in one place. The runner's public <c>init</c> properties write
/// through to this object, and every collaborator holds the same reference, so a value a caller sets after construction
/// (an object initializer) is what every collaborator reads. Documentation of each value is on the runner's property.
/// </summary>
internal sealed class CloudRunSettings
{
    public TimeSpan CreateTimeout { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan CreateSettleTimeout { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan? UploadTimeout { get; set; }

    public TimeSpan BootTimeout { get; set; } = TimeSpan.FromMinutes(8);

    public TimeSpan ResultPollInterval { get; set; } = TimeSpan.FromSeconds(10);

    public TimeSpan? ResultTimeout { get; set; }

    /// <summary>The clock for every deadline, elapsed-time check and wait of the run (gateway call deadlines, the boot, result and lifecycle waits, poll sleeps). A test passes a clock it moves by hand, so a loaded machine cannot race a wall-clock deadline (#525).</summary>
    public TimeProvider TimeProvider { get; set; } = TimeProvider.System;

    /// <summary>Replaces the sleep between polls. Null sleeps on <see cref="TimeProvider"/>. A test passes one that advances its clock by the slept time and returns at once.</summary>
    public Func<TimeSpan, CancellationToken, Task>? PollDelay { get; set; }

    /// <summary>Sleeps between two looks, on <see cref="PollDelay"/> when set, else on <see cref="TimeProvider"/>.</summary>
    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        => PollDelay is { } custom ? custom(delay, cancellationToken) : Task.Delay(delay, TimeProvider, cancellationToken);

    /// <summary>A moment on <see cref="TimeProvider"/> to measure from with <see cref="ElapsedSince"/>.</summary>
    public long StartClock() => TimeProvider.GetTimestamp();

    /// <summary>Time elapsed on <see cref="TimeProvider"/> since <paramref name="start"/>.</summary>
    public TimeSpan ElapsedSince(long start) => TimeProvider.GetElapsedTime(start);

    /// <summary>
    /// How long after the wait for the result begins the worker's first heartbeat may take: booting 8 min plus image pull 15 min
    /// plus 2 min margin (docs/job_contract.md section 5's per-stage deadlines). THEORY (unverified): the stage deadlines are the
    /// contract's, not measured on a real VM.
    /// </summary>
    public TimeSpan FirstHeartbeatTimeout { get; set; } = TimeSpan.FromMinutes(25);

    /// <summary>How long a heartbeat may stay unchanged before the worker process is called dead or frozen (a hung main thread under the free-running heartbeat thread is not caught, #522). Null means 20 times <c>limits.heartbeatSeconds</c> (600 s, docs/job_contract.md section 5).</summary>
    public TimeSpan? HeartbeatStaleTimeout { get; set; }

    /// <summary>Per-stage deadlines that replace <see cref="HeartbeatWatch.DefaultStageDeadlines"/> for the stages named (#90). Null means the defaults.</summary>
    public IReadOnlyDictionary<string, TimeSpan>? StageDeadlines { get; set; }

    public TimeSpan CallTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// The budget of the one gateway call that is a whole sequence, <see cref="IStorageGateway.EnsureBucketAsync"/> (project get, list, insert,
    /// read-back, second list, loser delete or config write, patch: up to about ten requests, each with its own retries inside the gateway), so one
    /// <see cref="CallTimeout"/> could cut it between the insert and the config write (#600). Sizing from the retry defaults (4 retries, backoff about
    /// 1 + 2 + 4 + 8 = 15 s, each attempt capped at 30 s): a bad stretch where every one of 10 requests needs all its retries costs about 150 s of backoff
    /// plus request time, so 300 s leaves about twice that; a single request that never answers uses at most 5 x 30 s + 15 s = 165 s of it. This is the
    /// cap on the whole sequence, not the hung-request guard (that is the per-attempt cap). THEORY (unverified): a cut happens in practice; the next call
    /// repairs an unconfigured bucket either way.
    /// </summary>
    public TimeSpan EnsureBucketTimeout { get; set; } = TimeSpan.FromMinutes(5);

    public TimeSpan LifecycleTimeout { get; set; } = TimeSpan.FromMinutes(3);

    public TimeSpan LifecyclePollInterval { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>
    /// The budget for the whole worker-identity step (#606), which is many HTTP calls in one gateway method. THEORY (unverified,
    /// sized from the gateway's worst case, not measured): a project read, an account create, up to 6 tries with 5 waits of
    /// 2 s growing to 12 s for a new account to become visible, a role create and read-back, and two policy read-modify-writes
    /// of up to 5 attempts each is under 40 calls; at the pipeline's per-call limits that is well over the 60 s
    /// <see cref="CallTimeout"/>, so it gets 5 minutes. Abandoning it earlier would leave the gateway writing policies after the run failed.
    /// </summary>
    public TimeSpan WorkerIdentityTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The budget for the whole quota read (#88): <c>regions.list</c> (one request per page) and <c>projects.get</c> in one gateway method,
    /// each request with its own per-attempt cut and retries inside the pipeline, which is more than the 60 s <see cref="CallTimeout"/> allows.
    /// THEORY (unverified, sized from the pipeline's worst case, not measured): two to three requests of up to 165 s each in the worst case, and
    /// seconds in a healthy one, so 5 minutes. A read cut here is UNKNOWN (a network failure), and preflight proceeds (docs/cloud_design.md section 6.2).
    /// </summary>
    public TimeSpan QuotaReadTimeout { get; set; } = TimeSpan.FromMinutes(5);
}
