using CommunityToolkit.Mvvm.Messaging;
using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Contract;
using DnaEntropyGraph.Presentation.Messaging;

namespace DnaEntropyGraph.Presentation.Services;

/// <summary>
/// Issue #1086 review: the last <see cref="ProgressEvent"/> published for each run. Progress is published on change only and the run page is a transient
/// ViewModel (a new one per navigation), so a page created or shown again mid-run would otherwise stay blank until the worker's next change. A singleton that
/// listens from construction (the runners' factories build it, so it exists before the first event); a run's entry goes when the run reaches a terminal phase.
/// Holds a progress record only: never an input name, a file name or a sequence.
/// </summary>
public sealed class RunProgressStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, ProgressEvent> _latest = new(StringComparer.Ordinal);

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

    private void Set(string jobId, ProgressEvent progress)
    {
        lock (_gate)
        {
            _latest[jobId] = progress;
        }
    }

    private void OnPhase(RunPhaseChangedMessage message)
    {
        if (message.Phase is JobPhase.Completed or JobPhase.PartiallyCompleted or JobPhase.Cancelled or JobPhase.Failed)
        {
            lock (_gate)
            {
                _latest.Remove(message.JobId);
            }
        }
    }
}
