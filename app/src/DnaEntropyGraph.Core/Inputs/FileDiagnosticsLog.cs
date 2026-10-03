using System.Globalization;
using DnaEntropyGraph.Core.Abstractions;

namespace DnaEntropyGraph.Core.Inputs;

/// <summary>
/// <see cref="IDiagnosticsLog"/> as one line per event appended to <c>&lt;root&gt;\logs\app.log</c> (UTF-8, LF, ASCII text): the same folder the
/// diagnostics zip reads. Never throws: a log that cannot be written must not take the work it describes down.
/// </summary>
public sealed class FileDiagnosticsLog : IDiagnosticsLog
{
    private readonly string _path;
    private readonly TimeProvider _time;
    private readonly object _gate = new();

    public FileDiagnosticsLog(string root, TimeProvider? time = null)
    {
        _path = Path.Combine(root, "logs", "app.log");
        _time = time ?? TimeProvider.System;
    }

    public void Warning(string source, string? jobId, string errorClass)
    {
        var line = string.Create(
            CultureInfo.InvariantCulture,
            $"{_time.GetUtcNow():yyyy-MM-ddTHH:mm:ss.fffZ} WARN {Clean(source)} job={Clean(jobId ?? "-")} error={Clean(errorClass)}\n");
        try
        {
            lock (_gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                File.AppendAllText(_path, line, new System.Text.UTF8Encoding(false));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static string Clean(string value) => value.Replace('\n', ' ').Replace('\r', ' ');
}
