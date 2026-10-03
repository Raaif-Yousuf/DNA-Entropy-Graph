using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Contract;

namespace DnaEntropyGraph.Core.Inputs;

/// <summary>
/// <see cref="IRunInputStore"/> on the local disk: <c>&lt;root&gt;\runs\&lt;jobId&gt;\input\&lt;file&gt;</c>,
/// the same relative layout docs/job_contract.md section 1 gives a local run.
/// The source is opened read-only and shared for reading; nothing is ever
/// written next to it.
/// </summary>
public sealed class LocalRunInputStore : IRunInputStore
{
    private static readonly char[] NotInAJobId = ['/', '\\', ':'];

    private readonly string _root;

    /// <param name="root">The app data folder (<c>%LOCALAPPDATA%\DNAEntropyGraph</c> in the app, a temp folder in tests).</param>
    public LocalRunInputStore(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _root = root;
    }

    private static bool IsPlainName(string jobId)
        => !string.IsNullOrWhiteSpace(jobId) && jobId.IndexOfAny(NotInAJobId) < 0 && !jobId.Contains("..", StringComparison.Ordinal);

    public Task<StagedInput?> FindAsync(string jobId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsPlainName(jobId))
        {
            return Task.FromResult<StagedInput?>(null);
        }

        var directory = Path.Combine(_root, "runs", jobId, "input");
        var first = Directory.Exists(directory) ? Directory.EnumerateFiles(directory).Order(StringComparer.Ordinal).FirstOrDefault() : null;
        return Task.FromResult(first is null ? null : new StagedInput(first, Path.GetFileName(first)));
    }

    public async Task<StagedInput> StageAsync(string jobId, string sourcePath, CancellationToken cancellationToken)
    {
        if (!IsPlainName(jobId))
        {
            throw new ArgumentException("The job id is not a plain name.", nameof(jobId));
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("The input file could not be found.", sourcePath);
        }

        var fileName = Path.GetFileName(sourcePath);
        var directory = Path.Combine(_root, "runs", jobId, "input");
        Directory.CreateDirectory(directory);
        var destination = Path.Combine(directory, fileName);

        await using (var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true))
        await using (var target = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
        {
            await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
        }

        return new StagedInput(destination, fileName);
    }

    public string? TryFindStagedInput(string jobId)
    {
        if (string.IsNullOrWhiteSpace(jobId) || jobId.IndexOfAny(NotInAJobId) >= 0 || jobId.Contains("..", StringComparison.Ordinal))
        {
            return null;
        }

        var directory = Path.Combine(_root, "runs", jobId, "input");
        return Directory.Exists(directory) ? Directory.EnumerateFiles(directory).Order(StringComparer.Ordinal).FirstOrDefault() : null;
    }
}
