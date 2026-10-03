namespace DnaEntropyGraph.Cloud.Rest;

/// <summary>What a test (or a developer) can change about the real Google gateways. Production passes nothing.</summary>
public sealed class GoogleCloudOptions
{
    /// <summary>When set, every request goes through this handler instead of the network: the scripted HTTP fake in tests.</summary>
    public HttpMessageHandler? HttpHandler { get; init; }

    /// <summary>The wait between polls of a long-running operation. Null waits for real; a test passes an instant one.</summary>
    public Func<TimeSpan, CancellationToken, Task>? Delay { get; init; }

    /// <summary>How long a long-running operation (create a project, enable a service) may run before it counts as timed out.</summary>
    public TimeSpan OperationDeadline { get; init; } = TimeSpan.FromMinutes(5);
}
