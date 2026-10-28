using DnaEntropyGraph.Core.Abstractions;

namespace DnaEntropyGraph.Core.Runs;

/// <summary>
/// Issue #111: the job ids of imported runs the user removed from the list, so the next "Find runs from other computers" does not
/// bring them back. Kept in settings (comma separated, newest last) rather than the history database: it needs no schema
/// migration, and "Reset all settings" is the one way to bring dismissed runs back. A job id is a short date-and-hex string,
/// never a name or a path, so it is safe to keep here.
/// </summary>
public sealed class ImportedRunDismissals(ISettingsStore settings)
{
    public const string SettingsKey = "dismissed_imported_runs";

    /// <summary>The most ids kept; the oldest fall off first so the setting cannot grow without bound.</summary>
    public const int MaxEntries = 2000;

    public IReadOnlySet<string> Load() => Read().ToHashSet(StringComparer.Ordinal);

    public void Add(string jobId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        var all = Read();
        if (all.Contains(jobId, StringComparer.Ordinal))
        {
            return;
        }

        all.Add(jobId);
        settings.SetString(SettingsKey, string.Join(',', all.Skip(Math.Max(0, all.Count - MaxEntries))));
    }

    private List<string> Read()
        => (settings.GetString(SettingsKey) ?? string.Empty).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();
}
