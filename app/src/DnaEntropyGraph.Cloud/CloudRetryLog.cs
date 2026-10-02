using System.Collections.Concurrent;
using DnaEntropyGraph.Core.Cloud;

namespace DnaEntropyGraph.Cloud;

/// <summary>
/// An in-memory <see cref="ICloudCallObserver"/> that keeps the most recent
/// retries. THEORY (unverified): Serilog is not wired into the app yet
/// (CLAUDE.md "Logs" row), so this is the sink until it is; the diagnostics
/// zip should read <see cref="Retries"/> then.
/// </summary>
public sealed class CloudRetryLog : ICloudCallObserver
{
    private const int Capacity = 200;

    private readonly ConcurrentQueue<CloudRetryEvent> _retries = new();
    private int _tokenRefreshes;
    private volatile bool _offline;

    public IReadOnlyList<CloudRetryEvent> Retries => _retries.ToList();

    public int TokenRefreshes => _tokenRefreshes;

    public bool IsOffline => _offline;

    public void OnRetry(CloudRetryEvent retry)
    {
        _retries.Enqueue(retry);
        while (_retries.Count > Capacity && _retries.TryDequeue(out _))
        {
        }
    }

    public void OnTokenRefreshed(string operation) => Interlocked.Increment(ref _tokenRefreshes);

    public void OnConnectivityChanged(bool offline) => _offline = offline;
}
