using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Runs;

namespace DnaEntropyGraph.Core.Cloud;

/// <summary>
/// The VM span a finished run is booked for (issue #109): its start (<see cref="RunRecord.VmReadyAt"/>) to its end (<see cref="RunRecord.FinishedAt"/>), but never longer than the
/// run's own limit (the run limit its options name plus the grace the VM is created with, Hard Rule 10), because Compute cannot have run it longer. The spend ledger prices
/// exactly this span, and the support zip reports exactly this span beside the cost, so the two can never disagree.
/// </summary>
public static class BookedVmSpan
{
    /// <summary>The run limit <c>RunOptions.MaxRunDurationMinutes</c> defaults to, used when a row's options cannot be read.</summary>
    private const int DefaultMaxRunMinutes = 240;

    /// <summary>How long the VM of <paramref name="run"/> can live: its run limit plus <see cref="CloudJobRequestFactory.VmGrace"/>.</summary>
    public static TimeSpan LimitOf(RunRecord run)
    {
        ArgumentNullException.ThrowIfNull(run);
        return TimeSpan.FromMinutes(RunOptionsJson.TryDeserialize(run.OptionsJson)?.MaxRunDurationMinutes ?? DefaultMaxRunMinutes) + CloudJobRequestFactory.VmGrace;
    }

    /// <summary>The latest instant billing can reach for a span that started at <paramref name="start"/>: <paramref name="end"/>, or the run's limit after the start when that is sooner.</summary>
    public static DateTimeOffset ClampEnd(RunRecord run, DateTimeOffset start, DateTimeOffset end)
    {
        var latest = start + LimitOf(run);
        return end > latest ? latest : end;
    }

    /// <summary>The seconds the run's VM is booked for, or null when the row has no span (no VM start or no finish, or a finish before the start).</summary>
    public static long? Seconds(RunRecord run)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (run.VmReadyAt is not { } start || run.FinishedAt is not { } finished || finished < start)
        {
            return null;
        }

        return (long)(ClampEnd(run, start, finished) - start).TotalSeconds;
    }
}
