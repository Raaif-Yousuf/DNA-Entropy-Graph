using DnaEntropyGraph.Core.Abstractions;

namespace DnaEntropyGraph.Core.Runs;

public enum LocalDeleteStatus
{
    Deleted,
    NothingToDelete,
    Refused,
}

/// <summary>The run's files on this PC (issue #101). Hard Rule 14: nothing here ever touches the input copy or anything outside the run's own output folder.</summary>
public interface ILocalRunFiles
{
    bool OutputFolderExists(RunRecord run);

    /// <summary>Deletes the run's own output folder, only if it lies strictly inside the folder the run was told to write under. Never the input copy.</summary>
    LocalDeleteStatus DeleteOutputFolder(RunRecord run);

    /// <summary>The input a re-run should use: the app's own copy, else the original file, else null.</summary>
    string? FindRerunInput(RunRecord run, RunOptions options);
}

public sealed class LocalRunFiles : ILocalRunFiles
{
    private readonly IRunInputStore _inputs;
    private readonly Func<string> _defaultOutputParent;
    private readonly IReadOnlyList<string> _protectedPaths;

    /// <param name="protectedPaths">Folders that must never be deleted from or through (the app data folder holding the input copies).</param>
    public LocalRunFiles(IRunInputStore inputs, Func<string> defaultOutputParent, IReadOnlyList<string> protectedPaths)
    {
        _inputs = inputs;
        _defaultOutputParent = defaultOutputParent;
        _protectedPaths = protectedPaths;
    }

    public bool OutputFolderExists(RunRecord run)
        => !string.IsNullOrWhiteSpace(run.OutputDir) && Directory.Exists(run.OutputDir);

    public LocalDeleteStatus DeleteOutputFolder(RunRecord run)
    {
        if (string.IsNullOrWhiteSpace(run.OutputDir))
        {
            return LocalDeleteStatus.NothingToDelete;
        }

        string folder;
        try
        {
            var root = RunOutputRoot.Resolve(run, _defaultOutputParent);
            folder = Path.GetFullPath(run.OutputDir);
            if (!RunOutputRoot.IsStrictlyInside(folder, root) || _protectedPaths.Any(p => RunOutputRoot.Overlaps(folder, p)))
            {
                return LocalDeleteStatus.Refused;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return LocalDeleteStatus.Refused;
        }

        if (!Directory.Exists(folder))
        {
            return LocalDeleteStatus.NothingToDelete;
        }

        Directory.Delete(folder, recursive: true);
        return LocalDeleteStatus.Deleted;
    }

    public string? FindRerunInput(RunRecord run, RunOptions options)
    {
        var staged = _inputs.TryFindStagedInput(run.JobId);
        if (!string.IsNullOrWhiteSpace(staged) && File.Exists(staged))
        {
            return staged;
        }

        return !string.IsNullOrWhiteSpace(options.InputPath) && File.Exists(options.InputPath) ? options.InputPath : null;
    }
}
