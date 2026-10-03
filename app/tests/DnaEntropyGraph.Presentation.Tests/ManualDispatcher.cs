using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Presentation.ViewModels;

namespace DnaEntropyGraph.Presentation.Tests;

/// <summary>An <see cref="IDispatcher"/> that queues until the test pumps it, so "off the UI thread" is observable.</summary>
internal sealed class ManualDispatcher : IDispatcher
{
    private readonly Queue<Action> _queue = new();

    public void Enqueue(Action action)
    {
        lock (_queue)
        {
            _queue.Enqueue(action);
        }
    }

    public void RunAll()
    {
        while (true)
        {
            Action next;
            lock (_queue)
            {
                if (_queue.Count == 0)
                {
                    return;
                }

                next = _queue.Dequeue();
            }

            next();
        }
    }
}

/// <summary>A <see cref="TimeProvider"/> whose timers fire only when the test says so.</summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    public List<ManualTimer> Timers { get; } = [];

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(callback, state, dueTime);
        Timers.Add(timer);
        return timer;
    }

    internal sealed class ManualTimer(TimerCallback callback, object? state, TimeSpan due) : ITimer
    {
        public TimeSpan Due { get; } = due;

        public void Fire() => callback(state);

        public bool Change(TimeSpan dueTime, TimeSpan period) => true;

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

internal static class TestMessages
{
    public static InAppMessageCenter Center() => new(new ManualDispatcher(), TimeProvider.System);
}
