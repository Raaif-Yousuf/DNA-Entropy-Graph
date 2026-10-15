namespace DnaEntropyGraph.Cloud.Tests;

/// <summary>
/// A <see cref="TimeProvider"/> that stands still until a test moves it (#525). Timers, <c>Task.Delay(.., provider)</c>,
/// <c>WaitAsync(.., provider)</c> and cancellation-token timeouts all run on it, so a deadline fires when the test says
/// time passed and never because a loaded machine was slow. With <paramref name="autoAdvance"/> a timer created with a
/// finite due time fires at once, moving the clock forward by that time: right for a retry delay (nothing else is
/// happening while it waits), wrong for a call deadline (the call is happening), so the runner's own clock does not use it.
/// </summary>
internal sealed class VirtualTimeProvider(bool autoAdvance = false) : TimeProvider
{
    private static readonly DateTimeOffset Epoch = new(2026, 10, 2, 8, 0, 0, TimeSpan.Zero);

    private readonly bool _autoAdvance = autoAdvance;
    private readonly object _gate = new();
    private readonly List<VirtualTimer> _timers = [];
    private long _ticks;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => Volatile.Read(ref _ticks);

    public override DateTimeOffset GetUtcNow() => Epoch + TimeSpan.FromTicks(Volatile.Read(ref _ticks));

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new VirtualTimer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    /// <summary>How many timers are armed right now (a deadline or backoff someone is waiting on).</summary>
    public int PendingTimers
    {
        get
        {
            lock (_gate)
            {
                return _timers.Count;
            }
        }
    }

    /// <summary>Moves the clock forward, firing every timer that falls due on the way in due-time order.</summary>
    public void Advance(TimeSpan by)
    {
        long target;
        lock (_gate)
        {
            target = _ticks + by.Ticks;
        }

        while (true)
        {
            VirtualTimer? next = null;
            lock (_gate)
            {
                foreach (var candidate in _timers)
                {
                    if (candidate.DueTicks <= target && (next is null || candidate.DueTicks < next.DueTicks))
                    {
                        next = candidate;
                    }
                }

                if (next is null)
                {
                    _ticks = Math.Max(_ticks, target);
                    return;
                }

                _ticks = Math.Max(_ticks, next.DueTicks);
                next.Reschedule(_ticks);
                if (next.DueTicks == long.MaxValue)
                {
                    _timers.Remove(next);
                }
            }

            next.Fire();
        }
    }

    private sealed class VirtualTimer(VirtualTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        private long _periodTicks;

        public long DueTicks { get; private set; } = long.MaxValue;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            var due = long.MaxValue;
            lock (owner._gate)
            {
                owner._timers.Remove(this);
                _periodTicks = period == Timeout.InfiniteTimeSpan ? 0 : period.Ticks;
                if (dueTime != Timeout.InfiniteTimeSpan)
                {
                    due = owner._ticks + dueTime.Ticks;
                    DueTicks = due;
                    owner._timers.Add(this);
                }
                else
                {
                    DueTicks = long.MaxValue;
                }
            }

            if (owner._autoAdvance && due != long.MaxValue)
            {
                owner.Advance(dueTime);
            }

            return true;
        }

        public void Reschedule(long now) => DueTicks = _periodTicks > 0 ? now + _periodTicks : long.MaxValue;

        public void Fire() => callback(state);

        public void Dispose()
        {
            lock (owner._gate)
            {
                owner._timers.Remove(this);
                DueTicks = long.MaxValue;
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
