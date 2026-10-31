namespace DnaEntropyGraph.Core.Abstractions;

/// <summary>What the app remembers about one Google Cloud project, kept per project so a switch restores it (issue #260).</summary>
/// <param name="ProjectId">The Google project id, the key.</param>
/// <param name="DisplayName">The project's name when it is known; null otherwise.</param>
/// <param name="Bucket">The results bucket the last run in this project used; null until one ran.</param>
public sealed record ProjectSettings(string ProjectId, string? DisplayName = null, string? Bucket = null);

/// <summary>
/// The per-project settings the Projects table holds (Appendix A section 3), as Core and Presentation may see them. Implemented
/// in DnaEntropyGraph.Persistence. Per project today: only the results bucket. The signed-in account and its token are per account.
/// </summary>
public interface IProjectSettingsStore
{
    /// <summary>The projects the account <paramref name="accountSub"/> has chosen on this PC (another account's choices are not listed), by id. Empty for a null account.</summary>
    Task<IReadOnlyList<ProjectSettings>> GetKnownAsync(string? accountSub, CancellationToken cancellationToken);

    /// <summary>The settings of <paramref name="projectId"/>, or null when it has never been chosen here.</summary>
    Task<ProjectSettings?> GetAsync(string projectId, CancellationToken cancellationToken);

    /// <summary>
    /// Makes sure the project is known and, when <paramref name="accountSub"/> is given, that the account has it in its own list. Several accounts may choose the same
    /// project; choosing it again never takes it from another account and never touches anything else remembered about it.
    /// </summary>
    Task EnsureAsync(string projectId, string? accountSub, CancellationToken cancellationToken);

    /// <summary>Remembers the results bucket a run in <paramref name="projectId"/> used. Writes that one column only, so it never overwrites a concurrent change to another.</summary>
    Task RememberBucketAsync(string projectId, string bucket, CancellationToken cancellationToken);
}
