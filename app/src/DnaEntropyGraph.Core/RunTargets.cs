namespace DnaEntropyGraph.Core;

/// <summary>
/// The values <c>RunRecord.Target</c> takes: where the run executed. Compare case-insensitively. The LocalEngine runner must write
/// <see cref="Local"/> (a run on this PC rents no VM, books no cost); today only <see cref="Cloud"/> is written.
/// </summary>
public static class RunTargets
{
    public const string Cloud = "cloud";

    public const string Local = "local";
}
