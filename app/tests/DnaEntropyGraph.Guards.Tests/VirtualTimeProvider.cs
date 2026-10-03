namespace DnaEntropyGraph.Guards.Tests;

/// <summary>
/// A <see cref="TimeProvider"/> a test moves by hand, including its timers (so <c>Task.Delay(delay, time)</c> ends when the test advances
/// past it). No real time passes and nothing sleeps: a test that waits on a backoff advances this instead.
/// </summary>
internal sealed class VirtualTimeProvider : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<VirtualTimer> _timers = [];
    private DateTimeOffset _now;

    public VirtualTimeProvider(DateTimeOffset? start = null) => _now = start ?? DateTimeOffset.UtcNow;

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return _now;
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new VirtualTimer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    /// <summary>How many timers are armed right now (a backoff someone is waiting on).</summary>
    public int PendingTimers
    {
        get
        {
            lock (_gate)
            {
                return _timers.Count(t => t.Due is not null);
            }
        }
    }

    /// <summary>Moves the clock forward and fires every timer that came due, in due order (a periodic one as often as it came due).</summary>
    public void Advance(TimeSpan by)
    {
        DateTimeOffset target;
        lock (_gate)
        {
            target = _now + by;
        }

        while (true)
        {
            VirtualTimer? next;
            lock (_gate)
            {
                next = _timers.Where(t => t.Due is { } due && due <= target).OrderBy(t => t.Due).FirstOrDefault();
                if (next is null)
                {
                    _now = target;
                    return;
                }

                _now = next.Due!.Value;
                next.Rearm();
            }

            next.Fire();
        }
    }

    private sealed class VirtualTimer(VirtualTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        private TimeSpan _period = Timeout.InfiniteTimeSpan;

        public DateTimeOffset? Due { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._gate)
            {
                _period = period;
                Due = dueTime == Timeout.InfiniteTimeSpan ? null : owner._now + dueTime;
                if (!owner._timers.Contains(this))
                {
                    owner._timers.Add(this);
                }
            }

            return true;
        }

        /// <summary>Called under the owner's lock, once this timer's due time has been reached.</summary>
        public void Rearm() => Due = _period == Timeout.InfiniteTimeSpan || _period == TimeSpan.Zero ? null : Due + _period;

        public void Fire() => callback(state);

        public void Dispose()
        {
            lock (owner._gate)
            {
                Due = null;
                owner._timers.Remove(this);
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
