using System.Globalization;
using DnaEntropyGraph.Core.Abstractions;
using DnaEntropyGraph.Core.Diagnostics;

namespace DnaEntropyGraph.Core.Inputs;

/// <summary>
/// <see cref="IDiagnosticsLog"/> as one line per event appended to <c>&lt;root&gt;\logs\app.log</c> (UTF-8, LF; every control character and the Unicode line and paragraph separators in a field become a space, so one event is one line; other text is kept as is): the same folder the
/// diagnostics zip reads. Never throws (except for a fatal error the process cannot go on after): a log that cannot be written must not take the work it
/// describes down, and it is called from inside the reconciler's own catch blocks, so any exception it let out would end that pass.
/// The file is capped (issue #575): once it holds <c>maxBytes</c>, it is renamed to <c>app.log.1</c> (replacing the previous one) and a new
/// <c>app.log</c> starts, so a row that fails on every launch and every reconnect costs at most about twice the cap on disk.
/// </summary>
public sealed class FileDiagnosticsLog : IDiagnosticsLog
{
    public const long DefaultMaxBytes = AppLogFile.DefaultMaxBytes;

    private readonly AppLogFile _file;
    private readonly TimeProvider _time;

    public FileDiagnosticsLog(string root, TimeProvider? time = null, long maxBytes = DefaultMaxBytes)
    {
        _file = new AppLogFile(root, maxBytes);
        _time = time ?? TimeProvider.System;
    }

    public void Warning(string source, string? jobId, string errorClass)
    {
        try
        {
            _file.AppendLine(string.Create(
                CultureInfo.InvariantCulture,
                $"{_time.GetUtcNow():yyyy-MM-ddTHH:mm:ss.fffZ} WARN {AppLogFile.Clean(source)} job={AppLogFile.Clean(jobId ?? "-")} error={AppLogFile.Clean(errorClass)}"));
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
        {
        }
    }
}