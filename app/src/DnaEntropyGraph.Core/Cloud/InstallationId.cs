using System.Text.RegularExpressions;
using DnaEntropyGraph.Core.Abstractions;

namespace DnaEntropyGraph.Core.Cloud;

/// <summary>
/// The per-installation id every cloud resource carries as the
/// <c>installation-id</c> label (Hard Rule 9/10: two users may share one
/// Google account, so discovery is by label). Generated once, persisted by the
/// settings store in its own write-once <c>installation_id</c> file (#558: a
/// settings.json problem must never change the id), and read back on every later run.
/// </summary>
public static class InstallationId
{
    public const string SettingsKey = "installation_id";

    private static readonly Regex LabelValue = new("^[a-z0-9_-]{1,63}$", RegexOptions.Compiled);

    // One lock for the whole process: the read-then-write below must be atomic,
    // or two first runs started together would label their VMs with different
    // installation ids (Hard Rule 9 discovers resources by this label).
    private static readonly object Gate = new();

    public static string GetOrCreate(ISettingsStore settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        lock (Gate)
        {
            var stored = settings.GetString(SettingsKey);
            if (IsValid(stored))
            {
                return stored!;
            }

            var created = Guid.NewGuid().ToString("n");
            settings.SetString(SettingsKey, created);

            // The store keeps the id write-once (#558), so a concurrent first
            // run in another process may have won: return what is stored, not
            // what this call minted, or the two runs would label differently.
            var winner = settings.GetString(SettingsKey);
            return IsValid(winner) ? winner! : created;
        }
    }

    /// <summary>The label-value rule an installation id must satisfy.</summary>
    public static bool IsValid(string? value) => value is not null && LabelValue.IsMatch(value);
}
