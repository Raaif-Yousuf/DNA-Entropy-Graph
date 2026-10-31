using System.Globalization;

namespace DnaEntropyGraph.Core.Contract;

/// <summary>
/// Issue #1086: turns the worker's <c>status.json</c> snapshots into <see cref="ProgressEvent"/>s for the run page, one per CHANGE. Both runners (the cloud's polled
/// bucket object, This PC's polled file) read the same snapshot on every poll; this is what keeps an unchanged one from being re-sent each time. One instance per run.
/// <para>
/// The message is the contract's own plain counters ("Window 5 of 11") or, with none, the stage name; it never carries the input, contig or record name from the snapshot's
/// <c>detail</c> (Hard Rule 14: the logs and the page never name the user's files). DECISION (agent-made, reversible): the text is built here in English, not looked up in
/// Resources.resw, because Core has no resource provider; a localised build would map the stage and window text in the publishing lambda instead.
/// </para>
/// </summary>
public sealed class WorkerProgressTracker
{
    private ProgressEvent? _last;
    private double _fraction;

    /// <summary>The event for <paramref name="status"/> when its stage, fraction or message differs from the last one given; otherwise null (also null for no status or no stage).</summary>
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

        var message = status.Window is { } window && status.Windows is { } windows
            ? string.Create(CultureInfo.InvariantCulture, $"Window {window} of {windows}")
            : status.Stage;
        var next = new ProgressEvent(status.Stage, _fraction, message);
        if (next == _last)
        {
            return null;
        }

        _last = next;
        return next;
    }
}
