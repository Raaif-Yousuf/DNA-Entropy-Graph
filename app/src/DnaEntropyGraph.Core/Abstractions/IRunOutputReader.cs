using DnaEntropyGraph.Core.Runs;

namespace DnaEntropyGraph.Core.Abstractions;

/// <summary>One file in a run's output folder. <see cref="RelativePath"/> uses <c>/</c> and is what the page shows.</summary>
public sealed record RunOutputFile(string RelativePath, string FullPath, long Bytes);

/// <summary>A summary file the worker wrote; <see cref="Summary"/> is null when it could not be read or understood.</summary>
public sealed record RunSummaryFile(string RelativePath, RunSummary? Summary);

/// <summary>What a run's output folder holds: every file, and the numbers read from the worker's summary files.</summary>
public sealed record RunOutputSnapshot(IReadOnlyList<RunOutputFile> Files, IReadOnlyList<RunSummaryFile> Summaries);

/// <summary>Reads a run's output folder for the Results page (issue #102). Touches the disk, so callers run it off the UI thread.</summary>
public interface IRunOutputReader
{
    /// <summary>The folder's contents, or null when the folder does not exist.</summary>
    RunOutputSnapshot? Read(string folder);
}
