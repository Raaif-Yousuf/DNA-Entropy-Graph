using DnaEntropyGraph.Core.Abstractions;

namespace DnaEntropyGraph.Core.Cost;

/// <summary>Picks, from the run history, the durations that say how long a good run on a machine takes.</summary>
public static class RunHistory
{
    /// <summary>How many of the newest runs are used: enough to smooth one odd run, few enough to follow a change in how the app starts a computer.</summary>
    public const int MaxRuns = 10;

    /// <summary>
    /// Start to finish of the newest completed cloud runs on <paramref name="machineType"/>, newest first. A failed, cancelled or
    /// unfinished run (or one with no timestamps) says nothing about how long a good run takes, so it is left out.
    /// </summary>
    public static IReadOnlyList<TimeSpan> DurationsFor(IEnumerable<RunRecord> runs, string machineType)
    {
        ArgumentNullException.ThrowIfNull(runs);
        return runs
            .Where(r => r.Phase == JobPhase.Completed
                && string.Equals(r.Target, "cloud", StringComparison.OrdinalIgnoreCase)
                && string.Equals(r.MachineType, machineType, StringComparison.Ordinal)
                && r.StartedAt is not null
                && r.FinishedAt is not null)
            .OrderByDescending(r => r.FinishedAt)
            .Select(r => r.FinishedAt!.Value - r.StartedAt!.Value)
            .Where(d => d > TimeSpan.Zero)
            .Take(MaxRuns)
            .ToList();
    }
}
