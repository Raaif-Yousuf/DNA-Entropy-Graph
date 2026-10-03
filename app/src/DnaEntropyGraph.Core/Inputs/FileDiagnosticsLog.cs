using System.Globalization;
using DnaEntropyGraph.Core.Abstractions;

namespace DnaEntropyGraph.Core.Inputs;

/// <summary>
/// <see cref="IDiagnosticsLog"/> as one line per event appended to <c>&lt;root&gt;\logs\app.log</c> (UTF-8, LF, ASCII text): the same folder the
/// diagnostics zip reads. Never throws (except for a fatal error the process cannot go on after): a log that cannot be written must not take the work it
/// describes down, and it is called from inside the reconciler's own catch blocks, so any exception it let out would end that pass.
/// The file is capped (issue #575): once it holds <c>maxBytes</c>, it is renamed to <c>app.log.1</c> (replacing the previous one) and a new
/// <c>app.log</c> starts, so a row that fails on every launch and every reconnect costs at most about twice the cap on disk.
/// </summary>
public sealed class FileDiagnosticsLog : IDiagnosticsLog
{
    /// <summary>One megabyte per file, two files at most: a diagnostics zip stays small and a day of failures still fits.</summary>
    public const long DefaultMaxBytes = 1_000_000;

    private readonly string _path;
    private readonly TimeProvider _time;
    private readonly long _maxBytes;
    private readonly object _gate = new();

    public FileDiagnosticsLog(string root, TimeProvider? time = null, long maxBytes = DefaultMaxBytes)
    {
        _path = Path.Combine(root, "logs", "app.log");
        _time = time ?? TimeProvider.System;
        _maxBytes = maxBytes;
    }

    public void Warning(string source, string? jobId, string errorClass)
    {
        try
        {
            var line = string.Create(
                CultureInfo.InvariantCulture,
                $"{_time.GetUtcNow():yyyy-MM-ddTHH:mm:ss.fffZ} WARN {Clean(source)} job={Clean(jobId ?? "-")} error={Clean(errorClass)}\n");
            lock (_gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                RotateIfFull();
                File.AppendAllText(_path, line, new System.Text.UTF8Encoding(false));
            }
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
        {
        }
    }

    private void RotateIfFull()
    {
        var info = new FileInfo(_path);
        if (info.Exists && info.Length >= _maxBytes)
        {
            try
            {
                File.Move(_path, _path + ".1", overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The rotated copy is held open (the diagnostics zip reads it) or locked: append anyway. A brief overshoot of the cap
                // beats dropping every line until the lock goes away; the next write tries the rotation again.
            }
        }
    }

    private static string Clean(string value) => value.Replace('\n', ' ').Replace('\r', ' ');
}
