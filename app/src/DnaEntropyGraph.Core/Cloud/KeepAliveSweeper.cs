using DnaEntropyGraph.Core.Abstractions;

namespace DnaEntropyGraph.Core.Cloud;

/// <summary>
/// Issue #476, Hard Rule 11: while the app is open, looks every <see cref="DefaultPeriod"/> for a Keep-alive VM that is still running past its
/// window and has it ended (<see cref="JobReconciler.EnforceKeepAliveAsync"/>). A run does not hold its slot while the window runs: a Completed run
/// is not an active run, so this timer is what the app does between launches. One sweep at a time (a slow listing never stacks), a failing sweep is
/// logged by error class and the next tick looks again, and nothing here ever throws into the timer thread. Started once from AppStartup.
/// </summary>
public sealed class KeepAliveSweeper : IDisposable
{
    /// <summary>How often a tick looks. A window is minutes to hours and the grace is five minutes, so five minutes keeps the VM's overrun short without a busy poll.</summary>
    public static readonly TimeSpan DefaultPeriod = TimeSpan.FromMinutes(5);

    private readonly Func<CancellationToken, Task> _sweep;
    private readonly TimeProvider _time;
    private readonly IDiagnosticsLog _log;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private ITimer? _timer;
    private bool _sweeping;
    private bool _disposed;

    public KeepAliveSweeper(Func<CancellationToken, Task> sweep, TimeProvider? time = null, IDiagnosticsLog? log = null)
    {
        _sweep = sweep;
        _time = time ?? TimeProvider.System;
        _log = log ?? NullDiagnosticsLog.Instance;
    }

    /// <summary>The most recent tick (completed when none has run yet), so a test can wait for it. Never faults.</summary>
    public Task LastTickAsync
    {
        get
        {
            lock (_gate)
            {
                return _lastTick;
            }
        }
    }

    private Task _lastTick = Task.CompletedTask;

    /// <summary>Arms the timer. The first look is one period from now (the launch pass has just enforced every lifecycle). A second call does nothing.</summary>
    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_timer is not null)
            {
                return;
            }

            _timer = _time.CreateTimer(_ => OnTick(), null, DefaultPeriod, DefaultPeriod);
        }
    }

    private void OnTick()
    {
        lock (_gate)
        {
            if (_sweeping || _disposed)
            {
                return;
            }

            _sweeping = true;
            _lastTick = SweepOnceAsync();
        }
    }

    private async Task SweepOnceAsync()
    {
        try
        {
            await _sweep(_stop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The shutdown cancel was taken by the clause above, so anything here (a lookup deadline included) is a failed sweep.
            try
            {
                _log.Warning("keepalive-sweep", null, ex.GetType().Name);
            }
            catch (Exception logEx) when (logEx is not OutOfMemoryException)
            {
                // The log is broken (a cancel thrown by it included): nothing is left to tell, and a broken log must not take the timer thread down or fault LastTickAsync.
                _ = logEx;
            }
        }
        finally
        {
            lock (_gate)
            {
                _sweeping = false;
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _timer?.Dispose();
            _timer = null;
        }

        _stop.Cancel();
        _stop.Dispose();
    }
}
