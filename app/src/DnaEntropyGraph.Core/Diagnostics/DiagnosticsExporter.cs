using DnaEntropyGraph.Core.Abstractions;

namespace DnaEntropyGraph.Core.Diagnostics;

/// <summary>Writes the support zip (issue #106) to a path the user chose.</summary>
public interface IDiagnosticsExporter
{
    /// <summary>
    /// Builds the bundle and saves it at <paramref name="destinationPath"/>. Blocking disk work: callers run it off the
    /// UI thread. Whenever this is called, even with a token that is already cancelled, a refused, cancelled or failed export
    /// removes a destination that is an empty file (the save picker creates one); a destination that already holds content is
    /// left exactly as it was.
    /// </summary>
    /// <exception cref="DiagnosticsLeakException">Sequence-like or credential-like text survived redaction.</exception>
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
        var temp = destinationPath + ".tmp-" + Guid.NewGuid().ToString("n");
        try
        {
            // Inside the try, so a token cancelled before the first await still reaches the cleanup below.
            cancellationToken.ThrowIfCancellationRequested();
            var history = await runs.GetAllAsync(cancellationToken).ConfigureAwait(false);
            var zip = DiagnosticsBundleBuilder.Build(source, history, infoFactory());
            await File.WriteAllBytesAsync(temp, zip, cancellationToken).ConfigureAwait(false);
            File.Move(temp, destinationPath, overwrite: true);
        }
        catch
        {
            DeleteIfEmpty(destinationPath);
            throw;
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    // The save picker creates a 0-byte file at the chosen name before we write anything; a failed export must not leave it.
    private static void DeleteIfEmpty(string path)
    {
        try
        {
            if (File.Exists(path) && new FileInfo(path).Length == 0)
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nothing more can be done; the original failure is the one to report.
        }
    }
}
