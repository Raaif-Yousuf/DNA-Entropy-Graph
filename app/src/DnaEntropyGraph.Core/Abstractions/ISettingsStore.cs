namespace DnaEntropyGraph.Core.Abstractions;

/// <summary>
/// <c>%LOCALAPPDATA%\DNAEntropyGraph\settings.json</c> (docs/architecture.md
/// section 6). See wired-to-nothing: a setting saved and never read is the
/// single most common instance of that bug class - every setter here has a
/// matching getter a ViewModel actually calls.
/// </summary>
public interface ISettingsStore
{
    string? GetString(string key);

    void SetString(string key, string value);
}
