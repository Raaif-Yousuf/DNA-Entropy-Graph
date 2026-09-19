namespace DnaEntropyGraph.Presentation.Services;

/// <summary>
/// The live log tail for one run (issue #66's "Live log tail from
/// logs/worker.log"), per the local-run path job_contract.md section 1
/// describes: the bucket layout rooted at
/// <c>%LOCALAPPDATA%\DNAEntropyGraph\runs\&lt;jobId&gt;\</c> instead of a bucket
/// prefix for a local run, same relative file names. Never throws for a
/// run whose log does not exist yet (the common case: the VM has not
/// booted, or LocalJobRunner has not started) - returns empty instead, so
/// a log tail control renders "nothing yet", not an error.
/// </summary>
public interface ILogTailReader
{
    IReadOnlyList<string> ReadLines(string jobId);
}
