using System.Collections.Concurrent;
using DnaEntropyGraph.Core.Abstractions;

namespace DnaEntropyGraph.App.UiTests;

/// <summary>An in-memory <see cref="IProjectSettingsStore"/> for the engine tests (issue #260): what a run chose and the bucket remembered per project.</summary>
internal sealed class MemoryProjectSettings : IProjectSettingsStore
{
    private readonly ConcurrentDictionary<string, ProjectSettings> _rows = new();

    private readonly ConcurrentDictionary<(string Account, string Project), byte> _chosen = new();

    public IReadOnlyList<ProjectSettings> Rows => _rows.Values.ToList();

    public ProjectSettings? Row(string projectId) => _rows.GetValueOrDefault(projectId);

    public bool Chose(string accountSub, string projectId) => _chosen.ContainsKey((accountSub, projectId));

    public Task<IReadOnlyList<ProjectSettings>> GetKnownAsync(string? accountSub, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<ProjectSettings>>(
            accountSub is null ? [] : _rows.Values.Where(p => _chosen.ContainsKey((accountSub, p.ProjectId))).ToList());

    public Task<ProjectSettings?> GetAsync(string projectId, CancellationToken cancellationToken) => Task.FromResult(Row(projectId));

    public Task EnsureAsync(string projectId, string? accountSub, CancellationToken cancellationToken)
    {
        _rows.TryAdd(projectId, new ProjectSettings(projectId));
        if (accountSub is not null)
        {
            _chosen[(accountSub, projectId)] = 0;
        }

        return Task.CompletedTask;
    }

    public Task RememberBucketAsync(string projectId, string bucket, CancellationToken cancellationToken)
    {
        _rows.AddOrUpdate(projectId, new ProjectSettings(projectId, Bucket: bucket), (_, row) => row with { Bucket = bucket });
        return Task.CompletedTask;
    }
}
