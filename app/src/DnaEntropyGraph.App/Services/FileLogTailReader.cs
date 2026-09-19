using DnaEntropyGraph.Presentation.Services;

namespace DnaEntropyGraph.App.Services;

/// <summary>
/// Reads <c>logs/worker.log</c> for one job from the local-run root
/// (job_contract.md section 1: for a local run, or the local mirror of a
/// cloud run's own bucket layout, rooted at
/// <c>%LOCALAPPDATA%\DNAEntropyGraph\runs\&lt;jobId&gt;\</c>). Reads the whole
/// file on every call rather than actually tailing an open handle -
/// correct and simple for a file that is, at most, a few thousand lines for
/// the run durations this app targets (6 to 20 minutes); a genuinely large
/// log would want a seek-from-last-offset reader instead, not yet needed.
/// </summary>
public sealed class FileLogTailReader : ILogTailReader
{
    private readonly string _rootPath;

    public FileLogTailReader()
        : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DNAEntropyGraph", "runs"))
    {
    }

    public FileLogTailReader(string rootPath)
    {
        _rootPath = rootPath;
    }

    public IReadOnlyList<string> ReadLines(string jobId)
    {
        var path = Path.Combine(_rootPath, jobId, "logs", "worker.log");
        return File.Exists(path) ? File.ReadAllLines(path) : Array.Empty<string>();
    }
}
