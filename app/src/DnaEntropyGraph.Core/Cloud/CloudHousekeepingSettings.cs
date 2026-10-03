using System.Globalization;
using DnaEntropyGraph.Core.Abstractions;

namespace DnaEntropyGraph.Core.Cloud;

/// <summary>
/// The settings the reconciler's housekeeping reads from <c>settings.json</c> (issue #530). The Settings page that shows them is a
/// separate issue; until then the key is edited in the file, like the other no-UI keys.
/// </summary>
public static class CloudHousekeepingSettings
{
    /// <summary>Whole hours a stopped VM of this installation may sit idle before the reconciler deletes it. <c>0</c> (or less) turns the sweep off.</summary>
    public const string IdleStoppedVmHoursKey = "idle_stopped_vm_hours";

    /// <summary>
    /// 72 hours: a stopped GPU VM keeps its boot disk (about $15 a month for 150 GB, so roughly $1.50 over three days), and three days
    /// covers a long weekend between two runs on the same VM. DECISION (agent-made, reversible): recorded in the decision issue filed with #530.
    /// </summary>
    public const int DefaultIdleStoppedVmHours = 72;

    /// <summary>How long a stopped VM may idle, or null when the sweep is switched off. An absent or unreadable value is the default, never "off".</summary>
    public static TimeSpan? IdleStoppedVmLimit(ISettingsStore settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var raw = settings.GetString(IdleStoppedVmHoursKey)?.Trim();
        var hours = int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : DefaultIdleStoppedVmHours;
        return hours <= 0 ? null : TimeSpan.FromHours(Math.Min(hours, 24 * 365));
    }
}
