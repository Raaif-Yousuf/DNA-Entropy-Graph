using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Runs;

namespace DnaEntropyGraph.Core.Cloud;

/// <summary>
/// The clamp on the VM span a finished run is booked for (issue #109): its start (<see cref="RunRecord.VmReadyAt"/>) to its end (<see cref="RunRecord.FinishedAt"/>), but never longer than the
/// run's own limit (the run limit its options name plus the grace the VM is created with, Hard Rule 10), because Compute cannot have run it longer. The spend ledger prices
/// this span at booking and freezes it in the entry; the support zip reports the entry's frozen span (<see cref="IRunCostSource.GetBookedSecondsAsync"/>), not a fresh derivation from the row.
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
}
