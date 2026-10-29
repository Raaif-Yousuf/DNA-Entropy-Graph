using DnaEntropyGraph.Core;
using DnaEntropyGraph.Core.Runs;
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

    public FileLogTailReader(AppDataRoot root)
        : this(root.RunsDirectory)
    {
    }

    public FileLogTailReader(string rootPath)
    {
        _rootPath = rootPath;
    }

    /// <summary>One folder name: no separator, colon, NUL or dot segments. "x\..\y" would resolve inside the runs folder but name a different job.</summary>
    private static bool IsPlainName(string jobId)
        => !string.IsNullOrWhiteSpace(jobId) && jobId is not ("." or "..") && jobId.IndexOfAny(['/', '\\', ':', '\0']) < 0;

    public IReadOnlyList<string> ReadLines(string jobId)
    {
        if (!IsPlainName(jobId))
        {
            return Array.Empty<string>();
        }

        var path = Path.Combine(_rootPath, jobId, "logs", "worker.log");
        // The job id is a run row's text: a rooted or climbing one must not point this read outside the runs folder.
        // A name the file system cannot parse (a NUL, a stray colon) throws from GetFullPath: that is "no log" too.
        bool inside;
        try
        {
            inside = RunOutputRoot.IsStrictlyInside(path, _rootPath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            inside = false;
        }

        if (!inside)
        {
            return Array.Empty<string>();
        }

        return File.Exists(path) ? File.ReadAllLines(path) : Array.Empty<string>();
    }
}
