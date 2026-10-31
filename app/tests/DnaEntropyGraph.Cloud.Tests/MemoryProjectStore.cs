using DnaEntropyGraph.Core.Abstractions;

namespace DnaEntropyGraph.Cloud.Tests;

/// <summary>An in-memory <see cref="IProjectSettingsStore"/> (issue #260): which projects each account chose, the bucket remembered per project, and a way to make it fail.</summary>
internal sealed class MemoryProjectStore : IProjectSettingsStore
{
    public Dictionary<string, ProjectSettings> Rows { get; } = [];

    public Dictionary<string, HashSet<string>> Chosen { get; } = [];

    public Exception? FailWith { get; set; }

    public Exception? FailListWith { get; set; }

    public Task<IReadOnlyList<ProjectSettings>> GetKnownAsync(string? accountSub, CancellationToken cancellationToken)
    {
        if (FailListWith is not null)
        {
            throw FailListWith;
        }

        return Task.FromResult<IReadOnlyList<ProjectSettings>>(
            accountSub is null ? [] : Rows.Values.Where(p => Chosen.GetValueOrDefault(accountSub)?.Contains(p.ProjectId) == true).ToList());
    }

    public Task<ProjectSettings?> GetAsync(string projectId, CancellationToken cancellationToken) => Task.FromResult(Rows.GetValueOrDefault(projectId));

    public Task EnsureAsync(string projectId, string? accountSub, CancellationToken cancellationToken)
    {
        if (FailWith is not null)
        {
            throw FailWith;
        }

        Rows.TryAdd(projectId, new ProjectSettings(projectId));
        if (accountSub is not null)
        {
            if (!Chosen.TryGetValue(accountSub, out var set))
            {
                Chosen[accountSub] = set = [];
            }

            set.Add(projectId);
        }

        return Task.CompletedTask;
    }

    public Task RememberBucketAsync(string projectId, string bucket, CancellationToken cancellationToken)
    {
        Rows[projectId] = new ProjectSettings(projectId, Bucket: bucket);
        return Task.CompletedTask;
    }
}
