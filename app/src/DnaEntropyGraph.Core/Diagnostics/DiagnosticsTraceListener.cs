using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace DnaEntropyGraph.Core.Diagnostics;

/// <summary>
/// Routes <see cref="Trace"/> output (the app's <c>Trace.TraceWarning/TraceError/TraceInformation</c> calls, issue #923) into
/// <c>&lt;root&gt;\logs\app.log</c>, the same file <see cref="Inputs.FileDiagnosticsLog"/> writes and the diagnostics zip collects.
/// Every call site in <c>app/src</c> passes only constant text plus an exception type name (a Guards test holds that), and every line is
/// redacted again here because third-party code can also write to Trace: absolute paths, email addresses and long nucleotide runs become
/// placeholders. Never throws into the caller.
/// </summary>
public sealed partial class DiagnosticsTraceListener : TraceListener
{
    private readonly AppLogFile _file;
    private readonly TimeProvider _time;
    private readonly StringBuilder _pending = new();

    public DiagnosticsTraceListener(string root, TimeProvider? time = null, long maxBytes = AppLogFile.DefaultMaxBytes)
    {
        _file = new AppLogFile(root, maxBytes);
        _time = time ?? TimeProvider.System;
    }

    /// <summary>
    /// The startup registration (called from <c>Program.Main</c> once the data folder is known). Idempotent: an earlier instance is removed first,
    /// because <see cref="Debug"/> and <see cref="Trace"/> share one listener collection and a double registration writes every line twice.
    /// </summary>
    public static void Install(string root, TimeProvider? time = null)
    {
        Uninstall();
        Trace.Listeners.Add(new DiagnosticsTraceListener(root, time));
        Trace.AutoFlush = true;
    }

    /// <summary>Removes every instance from the shared listener collection (tests; the app never calls this).</summary>
    public static void Uninstall()
    {
        foreach (var existing in Trace.Listeners.OfType<DiagnosticsTraceListener>().ToList())
        {
            Trace.Listeners.Remove(existing);
            existing.Dispose();
        }
    }

    /// <summary>The line hygiene and redaction every Trace line gets, exposed so the rules are testable on their own.</summary>
    public static string Redact(string text)
    {
        var result = EmailPattern().Replace(text, "<email>");
        result = PathPattern().Replace(result, "<path>");
        return SequencePattern().Replace(result, "<sequence>");
    }

    public override void Write(string? message)
    {
        if (message is null)
        {
            return;
        }

        lock (_pending)
        {
            _pending.Append(message);
        }
    }

    public override void WriteLine(string? message)
    {
        string text;
        lock (_pending)
        {
            _pending.Append(message);
            text = _pending.ToString();
            _pending.Clear();
        }

        Emit("INFO", text);
    }

    public override void TraceEvent(TraceEventCache? eventCache, string source, TraceEventType eventType, int id, string? message)
    {
        if (Filter is null || Filter.ShouldTrace(eventCache, source, eventType, id, message, null, null, null))
        {
            Emit(Level(eventType), message ?? string.Empty);
        }
    }

    public override void TraceEvent(TraceEventCache? eventCache, string source, TraceEventType eventType, int id, string? format, params object?[]? args)
    {
        if (Filter is not null && !Filter.ShouldTrace(eventCache, source, eventType, id, format, args, null, null))
        {
            return;
        }

        string message;
        try
        {
            message = args is { Length: > 0 } && format is not null
                ? string.Format(CultureInfo.InvariantCulture, format, args)
                : format ?? string.Empty;
        }
        catch (FormatException)
        {
            message = format ?? string.Empty;
        }

        Emit(Level(eventType), message);
    }

    public override void TraceData(TraceEventCache? eventCache, string source, TraceEventType eventType, int id, object? data)
        => Emit(Level(eventType), data is null ? string.Empty : "data " + data.GetType().Name);

    public override void TraceData(TraceEventCache? eventCache, string source, TraceEventType eventType, int id, params object?[]? data)
        => Emit(Level(eventType), "data " + string.Join(' ', (data ?? []).Select(d => d?.GetType().Name ?? "null")));

    private static string Level(TraceEventType type) => type switch
    {
        TraceEventType.Critical or TraceEventType.Error => "ERROR",
        TraceEventType.Warning => "WARN",
        _ => "INFO",
    };

    private void Emit(string level, string text)
    {
        try
        {
            var line = string.Create(
                CultureInfo.InvariantCulture,
                $"{_time.GetUtcNow():yyyy-MM-ddTHH:mm:ss.fffZ} {level} trace {AppLogFile.Clean(Redact(text))}");
            _file.AppendLine(line);
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
        {
        }
    }

    [GeneratedRegex(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9\-]+(?:\.[A-Za-z0-9\-]+)*\.[A-Za-z]{2,}")]
    private static partial Regex EmailPattern();

    // A drive path (either slash) or a UNC path, up to a quote, a character no path holds, or the colon that ends a clause. Greedy on purpose:
    // a path with spaces ("My Documents") must not leave its tail behind, and a lost word after a path costs less than a leaked folder name.
    [GeneratedRegex(@"(?:[A-Za-z]:[\\/]|\\\\)[^""'<>|*?:\r\n]*")]
    private static partial Regex PathPattern();

    // 20 or more IUPAC nucleotide letters in a row, as a whole word. A job id (yyyymmdd-hhmmss-xxxxxx) breaks on its digits and hyphens.
    [GeneratedRegex(@"\b[ACGTUNRYKMSWBDHV]{20,}\b", RegexOptions.IgnoreCase)]
    private static partial Regex SequencePattern();
}
