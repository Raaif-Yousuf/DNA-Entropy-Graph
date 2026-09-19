using System.Diagnostics;

namespace DnaEntropyGraph.LocalEngine;

/// <summary>
/// Launches the worker as a child process for the "Run on this PC" target
/// (Hard Rule 6 - never an in-process call). The skeleton exposes the
/// shape only; wiring the real `worker` executable path is a follow-up
/// issue once `worker/src/dna_entropy/worker/__main__.py` exists.
/// </summary>
public sealed class LocalJobRunner
{
    public Task<int> RunAsync(string workerExecutablePath, string manifestPath, CancellationToken cancellationToken)
    {
        throw new NotSupportedException("Local worker launch is not implemented in the solution skeleton (issue #61).");
    }
}
