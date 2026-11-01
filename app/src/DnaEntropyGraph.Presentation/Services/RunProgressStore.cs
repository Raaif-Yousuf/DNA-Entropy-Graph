using CommunityToolkit.Mvvm.Messaging;
using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Cloud;
using DnaEntropyGraph.Core.Contract;
using DnaEntropyGraph.Presentation.Messaging;

namespace DnaEntropyGraph.Presentation.Services;

/// <summary>
/// Issue #1086 review: the last <see cref="ProgressEvent"/> published for each run. Progress is published on change only and the run page is a transient
/// ViewModel (a new one per navigation), so a page created or shown again mid-run would otherwise stay blank until the worker's next change. A singleton that
/// listens from construction (the runners' factories build it, so it exists before the first event); a run's entry goes when the run reaches a terminal phase, and a progress event arriving after that is dropped.
/// Holds a progress record only: never an input name, a file name or a sequence.
/// </summary>
public sealed class RunProgressStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, ProgressEvent> _latest = new(StringComparer.Ordinal);

    // Run ids that have reached a terminal phase, oldest first. Progress and phase come from different threads, so a progress event can arrive AFTER the terminal phase;
    // without this memory it would re-create the entry for a finished run and never remove it. Bounded: a run finishes once and its late event follows within moments.
    private const int MaxFinishedRemembered = 1024;
    private readonly HashSet<string> _finished = new(StringComparer.Ordinal);
    private readonly Queue<string> _finishedOrder = new();

    public RunProgressStore(IMessenger messenger)
    {
        messenger.Register<RunProgressStore, RunProgressChangedMessage>(this, static (recipient, message) => recipient.Set(message.JobId, message.Progress));
        messenger.Register<RunProgressStore, RunPhaseChangedMessage>(this, static (recipient, message) => recipient.OnPhase(message));
    }

    /// <summary>The newest progress published for <paramref name="jobId"/>, or null when none has been (or the run has finished).</summary>
    public ProgressEvent? Latest(string jobId)
    {
        lock (_gate)
        {
            return _latest.GetValueOrDefault(jobId);
        }
    }

    /// <summary>How many runs currently hold stored progress (for tests).</summary>
    public int TrackedRunCount { get { lock (_gate) { return _latest.Count; } } }

    /// <summary>How many finished run ids are remembered (bounded; for tests).</summary>
    public int FinishedRunCount { get { lock (_gate) { return _finished.Count; } } }

    private void Set(string jobId, ProgressEvent progress)
    {
        lock (_gate)
        {
            if (_finished.Contains(jobId))
            {
                return; // a late event for a run that already ended
            }

            _latest[jobId] = progress;
        }
    }

    private void OnPhase(RunPhaseChangedMessage message)
    {
        if (!JobStateMachine.IsTerminal(message.Phase))
        {
            return;
        }

        lock (_gate)
        {
            _latest.Remove(message.JobId);
            if (_finished.Add(message.JobId))
            {
                _finishedOrder.Enqueue(message.JobId);
                while (_finishedOrder.Count > MaxFinishedRemembered)
                {
                    _finished.Remove(_finishedOrder.Dequeue());
                }
            }
        }
    }
}
