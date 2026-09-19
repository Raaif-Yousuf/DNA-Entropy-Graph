using System.Collections.Concurrent;
using DnaEntropyGraph.Core.Abstractions;

namespace DnaEntropyGraph.Persistence;

/// <summary>
/// In-memory placeholder for <c>%LOCALAPPDATA%\DNAEntropyGraph\settings.json</c>
/// (docs/architecture.md section 6). See this file's csproj comment.
/// </summary>
public sealed class SettingsStore : ISettingsStore
{
    private readonly ConcurrentDictionary<string, string> _values = new();

    public string? GetString(string key) => _values.TryGetValue(key, out var value) ? value : null;

    public void SetString(string key, string value) => _values[key] = value;
}
