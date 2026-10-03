using DnaEntropyGraph.Core.Abstractions;

namespace DnaEntropyGraph.Presentation.Services;

/// <summary>What the Results page asks for after a run (issue #586). <see cref="ExternalViewerOpener"/> is the real one.</summary>
public interface IExternalViewerOpener
{
    Task<ExternalViewerOutcome> OpenInIgvAsync(IReadOnlyList<RunOutputFile> files, CancellationToken cancellationToken);

    Task<ExternalViewerOutcome> OpenInGeneiousAsync(IReadOnlyList<RunOutputFile> files, CancellationToken cancellationToken);
}
