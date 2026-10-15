namespace DnaEntropyGraph.App.Startup;

/// <summary>
/// Issue #107: where an activation (a redirected launch, a notification click) waits when it lands before the window and the router
/// exist. <see cref="SingleInstance"/> subscribes to the OS event the moment this process becomes the main instance and posts here;
/// App.OnLaunched attaches the router once the UI is ready, and everything queued is delivered in order. Without the queue a
/// redirect that arrived in that gap was lost.
/// </summary>
public sealed class ActivationInbox
{
    /// <summary>The one inbox the running app uses.</summary>
    public static ActivationInbox Shared { get; } = new();

    private readonly object _gate = new();
    private readonly List<string?> _pending = [];
    private Action<string?>? _handler;

    /// <summary>Delivers <paramref name="arguments"/> to the handler, or keeps it until there is one. Any thread.</summary>
    public void Post(string? arguments)
    {
        Action<string?>? handler;
        lock (_gate)
        {
            handler = _handler;
            if (handler is null)
            {
                _pending.Add(arguments);
                return;
            }
        }

        handler(arguments);
    }

    /// <summary>Sets the handler and delivers everything that was queued before it, in order.</summary>
    public void Attach(Action<string?> handler)
    {
        string?[] queued;
        lock (_gate)
        {
            _handler = handler;
            queued = [.. _pending];
            _pending.Clear();
        }

        foreach (var arguments in queued)
        {
            handler(arguments);
        }
    }
}
