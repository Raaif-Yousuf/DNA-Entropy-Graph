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

    public TimeProvider TimeProvider { get; set; } = TimeProvider.System;

    /// <summary>
    /// How long after the wait for the result begins the worker's first heartbeat may take: booting 8 min plus image pull 15 min
    /// plus 2 min margin (docs/job_contract.md section 5's per-stage deadlines). THEORY (unverified): the stage deadlines are the
    /// contract's, not measured on a real VM.
    /// </summary>
    public TimeSpan FirstHeartbeatTimeout { get; set; } = TimeSpan.FromMinutes(25);

    /// <summary>How long a heartbeat may stay unchanged before the worker process is called dead or frozen (a hung main thread under the free-running heartbeat thread is not caught, #522). Null means 20 times <c>limits.heartbeatSeconds</c> (600 s, docs/job_contract.md section 5).</summary>
    public TimeSpan? HeartbeatStaleTimeout { get; set; }

    public TimeSpan CallTimeout { get; set; } = TimeSpan.FromSeconds(60);

    public TimeSpan LifecycleTimeout { get; set; } = TimeSpan.FromMinutes(3);

    public TimeSpan LifecyclePollInterval { get; set; } = TimeSpan.FromSeconds(3);
}
