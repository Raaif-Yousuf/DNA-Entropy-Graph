using System.Globalization;
using System.Numerics;
using DnaEntropyGraph.Core.Abstractions;

namespace DnaEntropyGraph.Core.Cloud;

/// <summary>
/// The settings the reconciler's housekeeping reads from <c>settings.json</c> (issue #530). The Settings page that shows them is a
/// separate issue; until then the key is edited in the file, like the other no-UI keys.
/// </summary>
public static class CloudHousekeepingSettings
{
    /// <summary>Whole hours a stopped VM of this installation may sit idle before the reconciler deletes it. Range <see cref="MinIdleStoppedVmHours"/> to <see cref="MaxIdleStoppedVmHours"/>; there is no off switch (decision #555).</summary>
    public const string IdleStoppedVmHoursKey = "idle_stopped_vm_hours";

    /// <summary>
    /// 72 hours: a stopped GPU VM keeps its boot disk (about $15 a month for 150 GB, so roughly $1.50 over three days), and three days
    /// covers a long weekend between two runs on the same VM. Decision #555.
    /// </summary>
    public const int DefaultIdleStoppedVmHours = 72;

    /// <summary>The shortest limit a user may set.</summary>
    public const int MinIdleStoppedVmHours = 1;

    /// <summary>The longest limit a user may set (30 days); a larger value is clamped to it.</summary>
    public const int MaxIdleStoppedVmHours = 720;

    /// <summary>
    /// How long a stopped VM may idle. An absent, zero, negative or unreadable value is the default (72 h), never "off"; a value above
    /// <see cref="MaxIdleStoppedVmHours"/> is clamped to it. A stopped VM always ends up deleted (Hard Rule 11).
    /// </summary>
    public static TimeSpan IdleStoppedVmLimit(ISettingsStore settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var raw = settings.GetString(IdleStoppedVmHoursKey)?.Trim();
        // Parsed as a BigInteger so a number of any size above the range clamps to the maximum instead of failing to parse and becoming the default.
        var hours = BigInteger.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed >= MinIdleStoppedVmHours
            ? (int)BigInteger.Min(parsed, MaxIdleStoppedVmHours)
            : DefaultIdleStoppedVmHours;
        return TimeSpan.FromHours(hours);
    }
}
