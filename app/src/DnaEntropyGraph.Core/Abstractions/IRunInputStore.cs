using DnaEntropyGraph.Core.Contract;

namespace DnaEntropyGraph.Core.Abstractions;

/// <summary>
/// Keeps the app's own copy of a run's input (Hard Rule 14: the user's file is
/// read-only to us, and a copy of every input is kept under app data).
/// Implemented by <c>LocalRunInputStore</c>; JobEngine stages the file before
/// any cloud resource exists, and the runner uploads the copy, never the original.
/// </summary>
public interface IRunInputStore
{
    /// <summary>Copies <paramref name="sourcePath"/> into the run's own folder and returns where the copy lives. Throws <see cref="FileNotFoundException"/> or <see cref="IOException"/> when the source cannot be read.</summary>
    Task<StagedInput> StageAsync(string jobId, string sourcePath, CancellationToken cancellationToken);

    /// <summary>The copy <see cref="StageAsync"/> kept for <paramref name="jobId"/>, or null when there is none (never staged, or removed). A reattached run (issue #59) uploads this, so it works after the user moved or deleted the original.</summary>
    Task<StagedInput?> FindAsync(string jobId, CancellationToken cancellationToken);
}
