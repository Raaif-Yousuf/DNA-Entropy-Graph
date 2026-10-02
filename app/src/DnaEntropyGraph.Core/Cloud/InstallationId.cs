using System.Text.RegularExpressions;
using DnaEntropyGraph.Core.Abstractions;

namespace DnaEntropyGraph.Core.Cloud;

/// <summary>
/// The per-installation id every cloud resource carries as the
/// <c>installation-id</c> label (Hard Rule 9/10: two users may share one
/// Google account, so discovery is by label). Generated once, persisted in
/// <c>settings.json</c>, and read back on every later run.
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
            if (stored is not null && LabelValue.IsMatch(stored))
            {
                return stored;
            }

            var created = Guid.NewGuid().ToString("n");
            settings.SetString(SettingsKey, created);
            return created;
        }
    }
}
