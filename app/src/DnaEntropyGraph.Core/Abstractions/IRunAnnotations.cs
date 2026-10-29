namespace DnaEntropyGraph.Core.Abstractions;

/// <summary>
/// Writes a run's user notes and tags (issue #141) and nothing else. Deliberately separate from <see cref="IRunRepository"/>:
/// its general upsert leaves these two columns alone so a phase change never erases a note, and this writes only these two
/// columns so a note edit never overwrites a phase change that landed meanwhile.
/// </summary>
public interface IRunAnnotations
{
    /// <summary>Sets the notes and tags (a JSON array, see <c>RunTags</c>) of the run; null clears. An unknown id is not an error and creates nothing; the result is false for it (the run was removed meanwhile), true when a row was written.</summary>
    Task<bool> UpdateAsync(string jobId, string? notes, string? tagsJson, CancellationToken cancellationToken);
}
