using DnaEntropyGraph.Core.Abstractions;

namespace DnaEntropyGraph.Cloud.Tests;

/// <summary>An in-memory <see cref="IProjectSettingsStore"/> (issue #260): which projects each account chose, the bucket remembered per project, and a way to make it fail.</summary>
internal sealed class MemoryProjectStore : IProjectSettingsStore
{
    public Dictionary<string, ProjectSettings> Rows { get; } = [];

    public Dictionary<string, HashSet<string>> Chosen { get; } = [];

    public Exception? FailWith { get; set; }

    public Exception? FailListWith { get; set; }

    /// <summary>Runs inside <see cref="GetKnownAsync"/>, as the real store's awaited read would: a test switches the account here to prove nothing is written for the wrong one.</summary>
    public Action? DuringGetKnown { get; set; }

    /// <summary>Runs inside <see cref="EnsureAsync"/> after the row is written (the account changes while a switch is saving).</summary>
    public Action? DuringEnsure { get; set; }

    public async Task<IReadOnlyList<ProjectSettings>> GetKnownAsync(string? accountSub, CancellationToken cancellationToken)
    {
        await Task.Yield();
        DuringGetKnown?.Invoke();
        if (FailListWith is not null)
        {
            throw FailListWith;
        }

        return accountSub is null ? [] : Rows.Values.Where(p => Chosen.GetValueOrDefault(accountSub)?.Contains(p.ProjectId) == true).ToList();
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

        DuringEnsure?.Invoke();
        return Task.CompletedTask;
    }

    public Task RememberBucketAsync(string projectId, string bucket, CancellationToken cancellationToken)
    {
        Rows[projectId] = new ProjectSettings(projectId, Bucket: bucket);
        return Task.CompletedTask;
    }
}
