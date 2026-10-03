using DnaEntropyGraph.Core.Abstractions;

namespace DnaEntropyGraph.Core.Diagnostics;

/// <summary>Writes the support zip (issue #106) to a path the user chose.</summary>
public interface IDiagnosticsExporter
{
    /// <summary>
    /// Builds the bundle and saves it at <paramref name="destinationPath"/>. Blocking disk work: callers run it off the
    /// UI thread. Nothing is left at the destination if the build is refused or the write fails.
    /// </summary>
    /// <exception cref="DiagnosticsLeakException">Sequence-like text survived redaction.</exception>
    Task ExportAsync(string destinationPath, CancellationToken cancellationToken);
}

/// <summary>
/// The glue between the real folder, the run history and <see cref="DiagnosticsBundleBuilder"/>. The zip is written to a
/// sibling temp file and moved into place, so a half-written file is never mistaken for a finished bundle.
/// </summary>
public sealed class DiagnosticsExporter(IDiagnosticsSource source, IRunRepository runs, Func<DiagnosticsInfo> infoFactory) : IDiagnosticsExporter
{
    public async Task ExportAsync(string destinationPath, CancellationToken cancellationToken)
    {
        var history = await runs.GetAllAsync(cancellationToken).ConfigureAwait(false);
        var zip = DiagnosticsBundleBuilder.Build(source, history, infoFactory());

        var temp = destinationPath + ".tmp-" + Guid.NewGuid().ToString("n");
        try
        {
            await File.WriteAllBytesAsync(temp, zip, cancellationToken).ConfigureAwait(false);
            File.Move(temp, destinationPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }
}
