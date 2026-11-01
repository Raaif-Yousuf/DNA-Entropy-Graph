namespace DnaEntropyGraph.Core.Contract;

/// <summary>
/// Issue #1086: turns the worker's <c>status.json</c> snapshots into <see cref="ProgressEvent"/>s for the run page, one per CHANGE. Both runners (the cloud's polled
/// bucket object, This PC's polled file) read the same snapshot on every poll; this is what keeps an unchanged one from being re-sent each time. One instance per run.
/// <para>
/// The event is data only: the stage id, the fraction and the worker's window counters. It never carries the input, contig or record name from the snapshot's
/// <c>detail</c> (Hard Rule 14: the logs and the page never name the user's files), and builds no user text (Hard Rule 13): the presentation layer maps the data to Resources.resw.
/// A stage change with no percent keeps the last fraction, so the bar never goes backwards.
/// </para>
/// </summary>
public sealed class WorkerProgressTracker
{
    private ProgressEvent? _last;
    private double _fraction;

    /// <summary>The event for <paramref name="status"/> when its stage, fraction or window counts differ from the last one given; otherwise null (also null for no status or no stage).</summary>
    public ProgressEvent? Next(WorkerStatusSnapshot? status)
    {
        if (status is null || string.IsNullOrEmpty(status.Stage))
        {
            return null;
        }

        if (status.Percent is { } percent)
        {
            _fraction = Math.Clamp(percent / 100.0, 0.0, 1.0);
        }

        var next = new ProgressEvent(status.Stage, _fraction, status.Window, status.Windows);
        if (next == _last)
        {
            return null;
        }

        _last = next;
        return next;
    }
}
