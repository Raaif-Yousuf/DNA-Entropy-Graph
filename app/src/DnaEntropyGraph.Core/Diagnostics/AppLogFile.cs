using System.Collections.Concurrent;

namespace DnaEntropyGraph.Core.Diagnostics;

/// <summary>
/// The one writer of <c>&lt;root&gt;\logs\app.log</c> (UTF-8, LF, one event per line), shared by <see cref="Inputs.FileDiagnosticsLog"/> and
/// <see cref="DiagnosticsTraceListener"/> (issue #923) so the two never interleave a half line: every writer of the same path takes the same lock.
/// Never throws (except for a fatal error the process cannot go on after): a log that cannot be written must not take the work it describes down.
/// The file is capped (issue #575): once it holds <c>maxBytes</c>, it is renamed to <c>app.log.1</c> (replacing the previous one) and a new
/// <c>app.log</c> starts, so a row that fails on every launch, or a storm of Trace lines, costs at most about twice the cap on disk.
/// </summary>
public sealed class AppLogFile
{
    /// <summary>One megabyte per file, two files at most: a diagnostics zip stays small and a day of failures still fits.</summary>
    public const long DefaultMaxBytes = 1_000_000;

    private static readonly ConcurrentDictionary<string, object> Gates = new(StringComparer.OrdinalIgnoreCase);

    private readonly string _path;
    private readonly long _maxBytes;
    private readonly object _gate;

    public AppLogFile(string root, long maxBytes = DefaultMaxBytes)
    {
        _path = Path.Combine(root, "logs", "app.log");
        _maxBytes = maxBytes;
        _gate = Gates.GetOrAdd(_path, static _ => new object());
    }

    /// <summary>Appends <paramref name="line"/> (already one line, no terminator) plus a line feed. A failure is swallowed.</summary>
    public void AppendLine(string line)
    {
        try
        {
            lock (_gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                if (!RotateIfFull())
                {
                    return;
                }

                File.AppendAllText(_path, line + "\n", new System.Text.UTF8Encoding(false));
            }
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
        {
        }
    }

    /// <summary>One event is one line: every control character (line feed, carriage return, NEL, form feed, escape, NUL...) and the Unicode line and paragraph separators become a space.</summary>
    public static string Clean(string value)
        => string.Create(value.Length, value, static (span, source) =>
        {
            for (var i = 0; i < source.Length; i++)
            {
                var c = source[i];
                span[i] = char.IsControl(c) || c is '\u2028' or '\u2029' ? ' ' : c;
            }
        });

    /// <summary>Rotates a full file. False when the rotation failed and the live file is already at twice the cap: the line is dropped, so a rotation that fails for good cannot grow the log without bound.</summary>
    private bool RotateIfFull()
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
                // The rotated copy is held open (the diagnostics zip reads it) or locked: append anyway, up to twice the cap. A brief overshoot
                // of the cap beats dropping every line until the lock goes away; the next write tries the rotation again. Past twice the cap the
                // rotation is not coming back soon (a read-only app.log.1), so the line is dropped rather than the file left to grow for good.
                return info.Length < 2 * _maxBytes;
            }
        }

        return true;
    }
}
