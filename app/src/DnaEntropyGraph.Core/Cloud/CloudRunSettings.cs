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

    public TimeSpan CallTimeout { get; set; } = TimeSpan.FromSeconds(60);

    public TimeSpan LifecycleTimeout { get; set; } = TimeSpan.FromMinutes(3);

    public TimeSpan LifecyclePollInterval { get; set; } = TimeSpan.FromSeconds(3);
}
