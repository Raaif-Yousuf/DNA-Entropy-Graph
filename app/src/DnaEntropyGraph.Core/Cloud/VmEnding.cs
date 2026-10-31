using System.Globalization;

namespace DnaEntropyGraph.Core.Cloud;

/// <summary>
/// Issue #476: the mark the app puts on a VM it is about to stop or delete for a run's expired Keep-alive window. It is written with the
/// VM's label fingerprint (so it fails if another run relabelled the VM since the app last looked) and a reuse refuses a VM that carries a
/// recent one, so a VM another run claims between the app's last look and its stop or delete is never ended under the first run's window.
/// The value is the time the mark was made (unix seconds), so a mark on a VM the app only stopped expires by itself after <see cref="Window"/>
/// instead of making that stopped VM unreusable for ever; a reuse's own claim rewrites every label and drops the mark.
/// </summary>
public static class VmEnding
{
    /// <summary>The label key. Not one of the standard labels (Hard Rule 10): an extra, short-lived one.</summary>
    public const string LabelKey = "ending-since";

    /// <summary>How long a mark keeps a VM from being reused. Covers the end call plus the wait for it to finish.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(15);

    /// <summary>The longest the app's end call for a marked VM (every retry and the wait for the operation) may run. Shorter than <see cref="Window"/> by design: an end call is cut off while the mark still keeps every other run from reusing the VM.</summary>
    public static readonly TimeSpan EndBudget = TimeSpan.FromMinutes(10);

    /// <summary>The label value for a mark made at <paramref name="now"/>.</summary>
    public static string Stamp(DateTimeOffset now) => now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);

    /// <summary>True when <paramref name="labels"/> carry a mark made within <see cref="Window"/> of <paramref name="now"/> (a mark from the future, by a skewed clock, counts as recent).</summary>
    public static bool IsEnding(IReadOnlyDictionary<string, string>? labels, DateTimeOffset now)
        => labels is not null
            && labels.TryGetValue(LabelKey, out var value)
            && long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            && now - DateTimeOffset.FromUnixTimeSeconds(seconds) <= Window;
}
