using System.Collections.Concurrent;

namespace DnaEntropyGraph.Presentation.Services;

/// <summary>One logged failure: when, where, and its error class. Never a message.</summary>
public sealed record AppLogEntry(DateTimeOffset At, string Area, string ErrorClass);

/// <summary>
/// The app's log sink until Serilog is wired in (CLAUDE.md "Logs" row; the same stop-gap as <c>CloudRetryLog</c>). Keeps the
/// most recent <see cref="Capacity"/> entries in memory so the diagnostics zip can read <see cref="Entries"/> when it exists.
/// </summary>
public sealed class InMemoryAppLog : IAppLog
{
    public const int Capacity = 200;

    private readonly ConcurrentQueue<AppLogEntry> _entries = new();
    private readonly TimeProvider _time;

    public InMemoryAppLog(TimeProvider? time = null) => _time = time ?? TimeProvider.System;

    public IReadOnlyList<AppLogEntry> Entries => _entries.ToList();

    public void Error(string area, string errorClass)
    {
        _entries.Enqueue(new AppLogEntry(_time.GetUtcNow(), area, errorClass));
        while (_entries.Count > Capacity && _entries.TryDequeue(out _))
        {
        }
    }
}
